using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DeadMansSwitch
{
    /// <summary>
    /// The timer window. Runs the two phases: a check period the user resets
    /// with "I'm here" (and can pause), then a final warning that ends in the
    /// configured shutdown/restart/hibernate.
    /// </summary>
    public partial class MainWindow : Window
    {
        enum Phase { Check, Warn }
        enum Confirm { None, Pause, Quit }

        const int FrameMs = 50;            // redraws the ring and runs the phase logic
        const int RefrontEveryMs = 2000;   // during the warning, pull the window forward this often
        const double RingSize = 232, RingThickness = 14;
        const int SpaceHotkeyId = 1;
        static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

        static readonly Color NormalBg = Color.FromRgb(0x16, 0x18, 0x1D);
        static readonly Color WarnBg = Color.FromRgb(0x33, 0x14, 0x16);
        static readonly IBrush GreenRing = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
        static readonly IBrush RedRing = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        static readonly IBrush PausedRing = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B));
        static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0xB0, 0x41));
        static readonly IBrush ErrorText = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x7E));
        static readonly IBrush InfoText = new SolidColorBrush(Color.FromRgb(0x8B, 0x90, 0x9A));

        readonly Settings settings;
        readonly bool headless;   // --selftest/--screenshot: no real window, tray or hotkeys
        readonly DispatcherTimer timer, updateTimer;
        readonly List<Control> panels;

        Phase phase;
        DateTime deadline, lastFront;
        double phaseSeconds;
        bool failed;           // the end action couldn't run; the error stays on screen
        bool paused;
        double pausedRemaining;
        bool quitting;
        bool spaceClaimed;     // Space is a system-wide hotkey for this window (Windows, final warning only)
        bool removeTokenOnSave;
        Confirm pendingConfirm;
        UpdateInfo update;
        TrayIcon tray;
        NativeMenuItem trayPauseItem;
        Control currentPanel;

        public MainWindow() : this(new Settings(), headless: true) { }   // for the XAML previewer

        public MainWindow(Settings settings, bool headless = false)
        {
            this.settings = settings;
            this.headless = headless;
            Updater.Token = settings.GitHubToken;
            InitializeComponent();

            panels = new List<Control> { MainPanel, SettingsPanel, ChangelogPanel, AskTrayPanel, ConfirmPanel };
            ShowPanel(MainPanel);
            VersionBtn.Content = "v" + Updater.Current;
            if (!Platform.TraySupported)
            {
                TrayBox.IsEnabled = false;
                TrayNote.IsVisible = true;
                TrayNote.Text = "Your desktop doesn't show tray icons. On GNOME, install the AppIndicator extension.";
            }

            TitleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
            };
            MinBtn.Click += (s, e) => WindowState = WindowState.Minimized;
            CloseBtn.Click += (s, e) => RequestClose();
            StopBtn.Click += (s, e) => AskToConfirm(Confirm.Quit);
            SettingsBtn.Click += (s, e) => ToggleSettings();
            BackBtn.Click += (s, e) => ShowPanel(MainPanel);
            SaveBtn.Click += async (s, e) => await SaveSettings();
            ResetBtn.Click += (s, e) =>
            {
                FillSettingsForm(new Settings());
                SetSettingsMessage("Defaults filled in. Click Save to keep them.", error: false);
            };
            RemoveTokenBtn.Click += (s, e) =>
            {
                removeTokenOnSave = true;
                TokenBox.Text = "";
                ShowTokenState();
            };
            ChangelogCloseBtn.Click += (s, e) => ShowPanel(MainPanel);
            TrayYesBtn.Click += (s, e) => AnswerTrayQuestion(true);
            TrayNoBtn.Click += (s, e) => AnswerTrayQuestion(false);
            ConfirmYesBtn.Click += (s, e) => ConfirmYes();
            ConfirmNoBtn.Click += (s, e) => ShowPanel(MainPanel);
            VersionBtn.Click += (s, e) => ShowChangelog();
            ExtendBtn.Click += (s, e) => ImHere();
            PauseBtn.Click += (s, e) => PauseOrResume();
            UpdateBtn.Click += async (s, e) => await InstallUpdate();
            ChangelogUpdateBtn.Click += async (s, e) => await InstallUpdate();

            // Seen before the focused control gets it, so these work wherever focus is.
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if ((e.Key == Key.Space || e.Key == Key.Enter) && phase == Phase.Warn && !failed) { ImHere(); e.Handled = true; }
                else if (e.Key == Key.Escape && currentPanel == ConfirmPanel) { ShowPanel(MainPanel); e.Handled = true; }
            }, RoutingStrategies.Tunnel);

            Closing += (s, e) =>
            {
                if (quitting || e.CloseReason == WindowCloseReason.OSShutdown) return;
                e.Cancel = true;   // Alt+F4 and the taskbar's Close follow the same rules as the × button
                RequestClose();
            };

            if (OperatingSystem.IsWindows() && !headless) HookWindowsMessages();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
            timer.Tick += (s, e) => Tick();
            updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
            updateTimer.Tick += async (s, e) => await CheckForUpdate();

            if (!headless && settings.CloseToTray == true && Platform.TraySupported) EnsureTray();

            StartCheck();
            Opened += async (s, e) =>
            {
                FitToSystem();
                timer.Start();
                if (headless) return;

                // First start of this version: show what changed.
                string current = Updater.Current.ToString();
                if (settings.SeenChangelogFor != current)
                {
                    settings.SeenChangelogFor = current;
                    TrySave();
                    ShowChangelog();
                }
                if (settings.CheckForUpdates == true)
                {
                    await CheckForUpdate(atLaunch: true);
                    updateTimer.Start();
                }
            };
            Closed += (s, e) =>
            {
                timer.Stop();
                updateTimer.Stop();
                RemoveTray();
            };
        }

        /// <summary>Without a compositor a transparent window shows black corners, so drop the shadow and rounding there.</summary>
        void FitToSystem()
        {
            if (headless || ActualTransparencyLevel != WindowTransparencyLevel.None) return;
            Frame.Margin = new Thickness(0);
            Root.CornerRadius = new CornerRadius(0);
            Root.BoxShadow = default;
        }

        /// <summary>
        /// Windows only: Space as a hotkey during the warning, and no keyboard
        /// menu mode (a lone Alt tap), which would swallow the next key.
        /// </summary>
        void HookWindowsMessages()
        {
            Win32Properties.AddWndProcHookCallback(this, (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
                if (msg == WindowsNative.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == WindowsNative.SC_KEYMENU)
                    handled = true;
                else if (msg == WindowsNative.WM_HOTKEY && wParam.ToInt64() == SpaceHotkeyId)
                {
                    if (phase == Phase.Warn && !failed) ImHere();
                    handled = true;
                }
                return IntPtr.Zero;
            });
        }

        string ActionVerb => settings.EndAction switch
        {
            EndAction.Restart => "restart",
            EndAction.Hibernate => "hibernate",
            _ => "shut down",
        };

        string ActionNoun => settings.EndAction switch
        {
            EndAction.Restart => "restart",
            EndAction.Hibernate => "hibernation",
            _ => "shutdown",
        };

        void TrySave()
        {
            try { settings.Save(); }
            catch (Exception) { }   // only bookkeeping (what was already shown); not worth interrupting for
        }

        // ----- phases -----

        void ImHere()
        {
            StartCheck();
            timer.Start();
        }

        void StartCheck()
        {
            ReleaseSpace();
            phase = Phase.Check;
            failed = false;
            paused = false;
            phaseSeconds = settings.CheckMinutes.Value * 60;
            deadline = DateTime.Now.AddSeconds(phaseSeconds);
            Root.Background = new SolidColorBrush(NormalBg);
            ExtendBtn.Content = "I'm here";
            PauseBtn.IsVisible = true;
            FillRow(StopBtn, alone: false, column: 2);
            ShowPauseState();
            Refresh();
        }

        void StartWarning()
        {
            phase = Phase.Warn;
            paused = false;
            phaseSeconds = settings.WarnSeconds.Value;
            deadline = DateTime.Now.AddSeconds(phaseSeconds);
            RingArc.Stroke = RedRing;
            Root.Background = new SolidColorBrush(WarnBg);
            ExtendBtn.Content = "I'm still here – keep it on";
            PauseBtn.IsVisible = false;
            FillRow(StopBtn, alone: true, column: 2);
            SetTrayPause(false, enabled: false);
            TimeLabel.Text = "seconds until " + ActionNoun;
            StatusText.Text = "Final warning. Press Space or click to keep it on.";
            ShowPanel(MainPanel);
            if (settings.PlaySound == true && !headless) Platform.PlayWarningSound();
            BringToFront();
            ClaimSpace();
            Refresh();
        }

        void TogglePause()
        {
            if (phase != Phase.Check || failed) return;
            paused = !paused;
            if (paused) pausedRemaining = Math.Max(0, (deadline - DateTime.Now).TotalSeconds);
            else deadline = DateTime.Now.AddSeconds(pausedRemaining);
            ShowPauseState();
            Refresh();
        }

        void ShowPauseState()
        {
            PauseIcon.Data = (Geometry)Application.Current.FindResource(paused ? "PlayIcon" : "PauseIcon");
            PauseText.Text = paused ? "Resume" : "Pause";
            RingArc.Stroke = paused ? PausedRing : GreenRing;
            TimeLabel.Text = paused ? "paused" : "until next check";
            StatusText.Text = paused
                ? "Paused. Nothing happens until you resume."
                : $"If nobody answers: {settings.WarnSeconds}-second warning, then {ActionNoun}.";
            SetTrayPause(paused, enabled: true);
        }

        void Tick()
        {
            if (!paused && !failed)
            {
                double remaining = (deadline - DateTime.Now).TotalSeconds;
                if (phase == Phase.Check)
                {
                    if (remaining <= 0) StartWarning();
                }
                else
                {
                    if (remaining <= 0) { _ = RunEndAction(); return; }
                    if ((DateTime.Now - lastFront).TotalMilliseconds >= RefrontEveryMs) BringToFront();
                }
            }
            Refresh();
        }

        async Task RunEndAction()
        {
            timer.Stop();
            ReleaseSpace();
            string error = await Platform.RunEndActionAsync(settings.EndAction);
            if (error == null) { Quit(); return; }

            // Stay open so the failure is seen rather than silently doing nothing.
            failed = true;
            TimeText.Text = "!";
            TimeLabel.Text = ActionNoun + " failed";
            StatusText.Text = error;
            ExtendBtn.Content = "Start again";
            if (tray != null) tray.ToolTipText = "Dead Man's Switch - " + ActionNoun + " failed";
            BringToFront();
        }

        // ----- drawing -----

        void Refresh()
        {
            if (failed) return;
            double remaining = paused ? pausedRemaining : Math.Max(0, (deadline - DateTime.Now).TotalSeconds);
            int whole = (int)Math.Ceiling(remaining);
            string text = phase == Phase.Check
                ? (whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60}:{whole % 60:00}")
                : whole.ToString();
            if (TimeText.Text != text)
            {
                TimeText.Text = text;
                if (tray != null)
                    tray.ToolTipText = paused ? "Dead Man's Switch - paused"
                        : phase == Phase.Check ? $"Dead Man's Switch - {text} until next check"
                        : $"Dead Man's Switch - {text} s until {ActionNoun}";
            }
            DrawRing(phaseSeconds > 0 ? remaining / phaseSeconds : 0);
        }

        /// <summary>Draws the remaining fraction as an arc running clockwise from 12 o'clock.</summary>
        void DrawRing(double fraction)
        {
            fraction = Math.Max(0, Math.Min(0.99999, fraction));
            double r = (RingSize - RingThickness) / 2, c = RingSize / 2;
            double angle = fraction * 2 * Math.PI;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(c, c - r), isFilled: false);
                g.ArcTo(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0,
                        isLargeArc: fraction > 0.5, SweepDirection.Clockwise);
                g.EndFigure(isClosed: false);
            }
            RingArc.Data = geometry;
            RingArc.IsVisible = fraction > 0;
        }

        // ----- window, tray and closing -----

        /// <summary>
        /// Shows one panel. The others stay in the layout (invisible and inert)
        /// so the window keeps the size of the largest instead of jumping.
        /// </summary>
        void ShowPanel(Control panel)
        {
            currentPanel = panel;
            foreach (var p in panels)
            {
                bool on = p == panel;
                p.Opacity = on ? 1 : 0;
                p.IsHitTestVisible = on;
                p.IsEnabled = on;
                p.ZIndex = on ? 1 : 0;
            }
        }

        /// <summary>
        /// Two-button rows are 3-column grids (button, gap, button). When one
        /// button is hidden, the other takes the whole row instead of its column.
        /// </summary>
        static void FillRow(Button button, bool alone, int column)
        {
            Grid.SetColumn(button, alone ? 0 : column);
            Grid.SetColumnSpan(button, alone ? 3 : 1);
        }

        /// <summary>Shows the window, from the tray, minimized or behind other windows.</summary>
        public void ShowWindow()
        {
            if (headless) return;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            MaybeShowReleaseNotes();
        }

        void BringToFront()
        {
            lastFront = DateTime.Now;
            if (headless) return;
            Platform.BringToFront(this);
            ExtendBtn.Focus();
        }

        /// <summary>
        /// During the final warning, Space anywhere resets the timer, even when
        /// Windows won't let this window take the foreground. If another app
        /// already owns Space as a hotkey, the window's own key handling remains.
        /// On Linux, Space works while the window has the keyboard.
        /// </summary>
        void ClaimSpace()
        {
            if (headless || spaceClaimed || !OperatingSystem.IsWindows()) return;
            if (TryGetPlatformHandle()?.Handle is IntPtr hWnd)
                spaceClaimed = WindowsNative.ClaimSpace(hWnd, SpaceHotkeyId);
        }

        void ReleaseSpace()
        {
            if (!spaceClaimed || !OperatingSystem.IsWindows()) return;
            if (TryGetPlatformHandle()?.Handle is IntPtr hWnd) WindowsNative.ReleaseSpace(hWnd, SpaceHotkeyId);
            spaceClaimed = false;
        }

        void EnsureTray()
        {
            if (tray != null || headless) return;
            var open = new NativeMenuItem("Open");
            open.Click += (s, e) => ShowWindow();
            var imHere = new NativeMenuItem("I'm here (reset timer)");
            imHere.Click += (s, e) => ImHere();
            trayPauseItem = new NativeMenuItem("Pause");
            trayPauseItem.Click += (s, e) => PauseOrResume();
            var quit = new NativeMenuItem("Stop timer and quit");
            quit.Click += (s, e) => AskToConfirm(Confirm.Quit);

            string icon = OperatingSystem.IsWindows() ? "icon.ico" : "icon.png";
            tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DeadMansSwitch/Assets/" + icon))),
                ToolTipText = "Dead Man's Switch",
                Menu = new NativeMenu { open, imHere, trayPauseItem, new NativeMenuItemSeparator(), quit },
                IsVisible = true,
            };
            tray.Clicked += (s, e) => ShowWindow();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
            SetTrayPause(paused, enabled: phase == Phase.Check && !failed);
        }

        void RemoveTray()
        {
            if (tray == null) return;
            tray.IsVisible = false;
            TrayIcon.SetIcons(Application.Current, new TrayIcons());
            tray.Dispose();
            tray = null;
        }

        void SetTrayPause(bool isPaused, bool enabled)
        {
            if (trayPauseItem == null) return;
            trayPauseItem.Header = isPaused ? "Resume" : "Pause";
            trayPauseItem.IsEnabled = enabled;
        }

        /// <summary>The × button: hide to the tray or quit, asking the first time.</summary>
        void RequestClose()
        {
            if (!Platform.TraySupported) { AskToConfirm(Confirm.Quit); return; }
            switch (settings.CloseToTray)
            {
                case true: HideToTray(); break;
                case false: AskToConfirm(Confirm.Quit); break;
                default: ShowPanel(AskTrayPanel); break;
            }
        }

        void AnswerTrayQuestion(bool toTray)
        {
            settings.CloseToTray = toTray;
            TrySave();
            ShowPanel(MainPanel);
            if (toTray) HideToTray();
            else Quit();
        }

        void HideToTray()
        {
            EnsureTray();
            ShowPanel(MainPanel);
            Hide();
        }

        void Quit()
        {
            ReleaseSpace();
            quitting = true;
            RemoveTray();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            else Close();
        }

        /// <summary>Resume needs no confirmation; pausing does.</summary>
        void PauseOrResume()
        {
            if (paused) TogglePause();
            else AskToConfirm(Confirm.Pause);
        }

        /// <summary>
        /// Shows the confirmation for pausing or for stopping the timer and
        /// quitting, opening the window first if it's in the tray.
        /// </summary>
        void AskToConfirm(Confirm what)
        {
            if (what == Confirm.Pause && (phase != Phase.Check || failed)) return;
            ShowWindow();
            pendingConfirm = what;
            ConfirmYesBtn.Classes.Set("amber", what == Confirm.Pause);
            ConfirmYesBtn.Classes.Set("red", what == Confirm.Quit);
            if (what == Confirm.Pause)
            {
                ConfirmIcon.Data = (Geometry)Application.Current.FindResource("PauseIcon");
                ConfirmIcon.Stroke = Amber;
                ConfirmTitle.Text = "Pause the timer?";
                ConfirmText.Text = $"While it's paused, the PC won't {ActionVerb} no matter how long nobody answers. Resume any time.";
                ConfirmYesBtn.Content = "Pause the timer";
            }
            else
            {
                ConfirmIcon.Data = (Geometry)Application.Current.FindResource("StopIcon");
                ConfirmIcon.Stroke = RedRing;
                ConfirmTitle.Text = "Stop the timer and quit?";
                ConfirmText.Text = $"Dead Man's Switch will close, and the PC won't {ActionVerb} when the time runs out.";
                ConfirmYesBtn.Content = "Stop and quit";
            }
            ShowPanel(ConfirmPanel);
        }

        void ConfirmYes()
        {
            var what = pendingConfirm;
            pendingConfirm = Confirm.None;
            ShowPanel(MainPanel);
            if (what == Confirm.Pause && !paused) TogglePause();   // ignored if the warning started meanwhile
            else if (what == Confirm.Quit) Quit();
        }

        // ----- settings -----

        void ToggleSettings()
        {
            if (phase == Phase.Warn) return;   // the warning keeps the "I'm here" button on screen
            if (currentPanel == SettingsPanel) { ShowPanel(MainPanel); return; }
            FillSettingsForm(settings);
            removeTokenOnSave = false;
            TokenBox.Text = "";
            ShowTokenState();
            SettingsMsg.Text = "";
            SettingsScroll.Offset = default;
            ShowPanel(SettingsPanel);
        }

        /// <summary>The token itself is never shown; the box is for typing a new one.</summary>
        void ShowTokenState()
        {
            bool saved = settings.GitHubToken != null && !removeTokenOnSave;
            string storage = OperatingSystem.IsWindows()
                ? "stored encrypted for your Windows account"
                : "stored in your settings file, readable only by you";
            TokenStatus.Text = saved
                ? "A token is saved. Paste a new one to replace it."
                : removeTokenOnSave
                    ? "The saved token will be removed when you save."
                    : "Only needed if the repository is private, or GitHub starts limiting update checks. "
                      + $"It's checked with GitHub when you save, and {storage}.";
            RemoveTokenBtn.IsVisible = saved;
        }

        void FillSettingsForm(Settings from)
        {
            CheckMinBox.Text = from.CheckMinutes.ToString();
            WarnSecBox.Text = from.WarnSeconds.ToString();
            ShutdownOpt.IsChecked = from.EndAction == EndAction.Shutdown;
            RestartOpt.IsChecked = from.EndAction == EndAction.Restart;
            HibernateOpt.IsChecked = from.EndAction == EndAction.Hibernate;
            SoundBox.IsChecked = from.PlaySound;
            TrayBox.IsChecked = from.CloseToTray == true && Platform.TraySupported;
            UpdatesBox.IsChecked = from.CheckForUpdates;
            AutoInstallBox.IsChecked = from.AutoInstallUpdates;
        }

        void SetSettingsMessage(string text, bool error)
        {
            SettingsMsg.Foreground = error ? ErrorText : InfoText;
            SettingsMsg.Text = text;
        }

        async Task SaveSettings()
        {
            if (!int.TryParse(CheckMinBox.Text?.Trim(), out int minutes) || !Settings.ValidCheckMinutes(minutes))
            {
                SetSettingsMessage($"Check interval must be {Settings.MinCheckMinutes}-{Settings.MaxCheckMinutes} minutes.", error: true);
                return;
            }
            if (!int.TryParse(WarnSecBox.Text?.Trim(), out int seconds) || !Settings.ValidWarnSeconds(seconds))
            {
                SetSettingsMessage($"Final warning must be {Settings.MinWarnSeconds}-{Settings.MaxWarnSeconds} seconds.", error: true);
                return;
            }

            // A new token is only kept if GitHub accepts it.
            string newToken = TokenBox.Text?.Trim() ?? "";
            if (newToken.Length > 0)
            {
                SetSettingsMessage("Checking the token with GitHub...", error: false);
                SaveBtn.IsEnabled = false;
                string problem = await Updater.TestTokenAsync(newToken);
                SaveBtn.IsEnabled = true;
                if (problem != null) { SetSettingsMessage(problem, error: true); return; }
            }

            settings.CheckMinutes = minutes;
            settings.WarnSeconds = seconds;
            settings.EndAction = RestartOpt.IsChecked == true ? EndAction.Restart
                               : HibernateOpt.IsChecked == true ? EndAction.Hibernate
                               : EndAction.Shutdown;
            settings.PlaySound = SoundBox.IsChecked == true;
            if (Platform.TraySupported) settings.CloseToTray = TrayBox.IsChecked == true;
            settings.CheckForUpdates = UpdatesBox.IsChecked == true;
            settings.AutoInstallUpdates = AutoInstallBox.IsChecked == true;
            if (newToken.Length > 0) settings.GitHubToken = newToken;
            else if (removeTokenOnSave) settings.GitHubToken = null;

            try { settings.Save(); }
            catch (Exception ex)
            {
                SetSettingsMessage("Couldn't save settings: " + ex.Message, error: true);
                return;
            }

            Updater.Token = settings.GitHubToken;
            if (settings.CloseToTray == true && Platform.TraySupported) EnsureTray();
            else RemoveTray();
            if (settings.CheckForUpdates == true) updateTimer.Start();
            else updateTimer.Stop();

            ShowPanel(MainPanel);
            if (phase == Phase.Check) StartCheck();   // a warning in progress keeps its length
        }

        // ----- changelog and updates -----

        /// <summary>Shows notes for releases not installed yet (if any), then this version's changelog.</summary>
        void ShowChangelog()
        {
            if (phase == Phase.Warn) return;
            var markdown = new StringBuilder();
            foreach (var notes in update?.Notes ?? new List<ReleaseNotes>())
                markdown.Append($"## [{notes.Version}] - Available now\n\n{notes.Body}\n\n");
            markdown.Append(Changelog.Installed());
            Changelog.Render(ChangelogContent, markdown.ToString());

            ChangelogUpdateBtn.IsVisible = update != null;
            FillRow(ChangelogCloseBtn, alone: update == null, column: 0);
            if (update != null) ChangelogUpdateBtn.Content = (update.CanInstall ? "Update to " : "Get ") + update.Version;
            ChangelogScroll.Offset = default;
            ShowPanel(ChangelogPanel);
        }

        /// <summary>Shows a newer release's notes once, when the window is open and not busy.</summary>
        void MaybeShowReleaseNotes()
        {
            if (update == null || settings.SeenReleaseNotesFor == update.Version) return;
            if (!IsVisible || phase == Phase.Warn || currentPanel == SettingsPanel
                || currentPanel == AskTrayPanel || currentPanel == ConfirmPanel) return;
            settings.SeenReleaseNotesFor = update.Version;
            TrySave();
            ShowChangelog();
        }

        /// <summary>
        /// Looks for a newer release. At launch (with automatic installs on, and
        /// where this copy can install it) it's installed straight away;
        /// otherwise it's offered with a banner and its notes.
        /// </summary>
        async Task CheckForUpdate(bool atLaunch = false)
        {
            var found = await Updater.CheckAsync();
            if (found == null) return;
            update = found;
            if (atLaunch && settings.AutoInstallUpdates == true && update.CanInstall && phase == Phase.Check)
            {
                await InstallUpdate();
                return;
            }
            UpdateText.Text = $"Version {update.Version} is available.";
            UpdateBtn.Content = update.CanInstall ? "Update" : "Download";
            UpdateBanner.IsVisible = true;
            if (currentPanel == ChangelogPanel) ShowChangelog();   // add the new notes to what's showing
            if (IsVisible) MaybeShowReleaseNotes();
        }

        /// <summary>Installs the update, or opens its download page where this copy can't install it (Flatpak).</summary>
        async Task InstallUpdate()
        {
            if (update == null) return;
            if (!update.CanInstall)
            {
                await Launcher.LaunchUriAsync(new Uri(update.PageUrl));
                return;
            }
            UpdateBtn.IsEnabled = ChangelogUpdateBtn.IsEnabled = false;
            UpdateText.Text = $"Updating to {update.Version}...";
            UpdateBanner.IsVisible = true;
            try
            {
                await Updater.InstallAsync(update);
                Quit();
            }
            catch (Exception ex)
            {
                UpdateText.Text = "Update failed: " + ex.Message;
                UpdateBanner.IsVisible = true;
                ShowPanel(MainPanel);
                UpdateBtn.IsEnabled = ChangelogUpdateBtn.IsEnabled = true;
            }
        }

        // ----- CI helpers -----

        /// <summary>
        /// Renders each state to a PNG (used by --screenshot, so CI and the
        /// README can show the UI). Runs on the headless platform.
        /// </summary>
        public void SaveScreenshots(string dir)
        {
            Root.Transitions = null;   // no fades mid-capture
            System.IO.Directory.CreateDirectory(dir);

            void Shot(string name, double secondsLeft)
            {
                if (paused) pausedRemaining = secondsLeft;
                else deadline = DateTime.Now.AddSeconds(secondsLeft);
                Refresh();
                SaveWithTransparency(System.IO.Path.Combine(dir, name + ".png"));
            }

            StartCheck();
            Shot("check", phaseSeconds * 0.62);
            TogglePause();
            Shot("paused", phaseSeconds * 0.62);
            TogglePause();
            ToggleSettings();
            Shot("settings", phaseSeconds * 0.62);
            SettingsScroll.Offset = new Vector(0, 10_000);
            Shot("settings-updates", phaseSeconds * 0.62);
            ShowChangelog();
            Shot("changelog", phaseSeconds * 0.62);
            ShowPanel(AskTrayPanel);
            Shot("tray-question", phaseSeconds * 0.62);
            AskToConfirm(Confirm.Pause);
            Shot("confirm-pause", phaseSeconds * 0.62);
            AskToConfirm(Confirm.Quit);
            Shot("confirm-quit", phaseSeconds * 0.62);
            StartWarning();
            Shot("warning", 23.4);
        }

        /// <summary>
        /// The headless renderer drops transparency, so the rounded corners and
        /// shadow would land on black. Capturing on black and on white gives it
        /// back: a pixel's alpha is how little it changed between the two.
        /// </summary>
        void SaveWithTransparency(string path)
        {
            PixelFormat format = PixelFormat.Rgba8888;
            byte[] Capture(IBrush background, out PixelSize size, out int stride)
            {
                Background = background;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = this.CaptureRenderedFrame();
                using var pixels = frame.Lock();
                size = pixels.Size;
                stride = pixels.RowBytes;
                format = pixels.Format;   // channel order varies by platform; alpha math below doesn't care
                var bytes = new byte[stride * size.Height];
                Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
                return bytes;
            }

            byte[] onBlack = Capture(Brushes.Black, out var size, out int stride);
            byte[] onWhite = Capture(Brushes.White, out _, out _);
            Background = Brushes.Transparent;

            using var result = new WriteableBitmap(size, new Vector(96, 96), format, AlphaFormat.Unpremul);
            using (var target = result.Lock())
            {
                var row = new byte[size.Width * 4];
                for (int y = 0; y < size.Height; y++)
                {
                    for (int x = 0; x < size.Width; x++)
                    {
                        int i = y * stride + x * 4;   // 4 bytes per pixel, alpha last
                        int alpha = Math.Clamp(255 - (onWhite[i + 1] - onBlack[i + 1]), 0, 255);
                        for (int c = 0; c < 3; c++)
                            row[x * 4 + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, onBlack[i + c] * 255 / alpha);
                        row[x * 4 + 3] = (byte)alpha;
                    }
                    Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
                }
            }
            result.Save(path);
        }

        /// <summary>Runs the phases and panels without a real window (used by --selftest).</summary>
        public void SelfTest()
        {
            StartCheck();
            PauseOrResume();
            if (paused || currentPanel != ConfirmPanel) throw new Exception("Pause didn't ask for confirmation.");
            ConfirmYes();
            if (!paused) throw new Exception("Confirming pause did not pause.");
            PauseOrResume();
            if (paused) throw new Exception("Resume did not resume.");
            AskToConfirm(Confirm.Quit);
            ShowPanel(MainPanel);   // "Keep the timer running"
            if (quitting) throw new Exception("Backing out of quit still quit.");
            ToggleSettings();
            FillSettingsForm(new Settings());
            ShowChangelog();
            if (ChangelogContent.Children.Count == 0) throw new Exception("The changelog rendered empty.");
            ShowPanel(AskTrayPanel);
            StartWarning();
            ImHere();
            if (phase != Phase.Check) throw new Exception("I'm here did not leave the warning.");
            timer.Stop();
        }
    }
}
