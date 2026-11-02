using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DeadMansSwitch
{
    /// <summary>
    /// The timer window. Loads MainWindow.xaml and runs the two phases:
    /// a check period the user resets with "I'm here" (and can pause), then
    /// a final warning that ends in the configured shutdown/restart/hibernate.
    /// </summary>
    sealed class MainView
    {
        enum Phase { Check, Warn }
        enum Confirm { None, Pause, Quit }

        const int TickMs = 250;
        const int RefrontEveryMs = 2000;   // during the warning, pull the window forward this often
        const double RingSize = 232, RingThickness = 14;
        static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

        static readonly Color NormalBg = Color.FromRgb(0x16, 0x18, 0x1D);
        static readonly Color WarnBg = Color.FromRgb(0x33, 0x14, 0x16);
        static readonly Brush GreenRing = Frozen(Color.FromRgb(0x2E, 0xCC, 0x71));
        static readonly Brush RedRing = Frozen(Color.FromRgb(0xE7, 0x4C, 0x3C));
        static readonly Brush PausedRing = Frozen(Color.FromRgb(0x5A, 0x5F, 0x6B));
        static readonly Brush ErrorText = Frozen(Color.FromRgb(0xF0, 0x8A, 0x7E));
        static readonly Brush InfoText = Frozen(Color.FromRgb(0x8B, 0x90, 0x9A));
        static readonly Brush Amber = Frozen(Color.FromRgb(0xF5, 0xB0, 0x41));
        static readonly Brush DarkText = Frozen(Color.FromRgb(0x1E, 0x15, 0x05));
        static readonly Brush StopRed = Frozen(Color.FromRgb(0xE7, 0x4C, 0x3C));
        static readonly Brush White = Frozen(Colors.White);

        public Window Window { get; }

        readonly Settings settings;
        readonly DispatcherTimer timer, updateTimer;
        readonly SolidColorBrush background = new SolidColorBrush(NormalBg);
        readonly PathFigure ringFigure = new PathFigure { IsClosed = false };
        readonly ArcSegment ringArc = new ArcSegment { SweepDirection = SweepDirection.Clockwise };

        Phase phase;
        DateTime deadline;
        double phaseSeconds;
        int warnTicks;
        bool failed;           // the end action couldn't run; the error stays on screen
        bool paused;
        double pausedRemaining;
        bool quitting;
        bool trayHintShown;
        bool headless;         // --selftest/--screenshot: never show the real window
        bool spaceClaimed;     // Space is a system-wide hotkey for this window (final warning only)
        const int SpaceHotkeyId = 1;
        Confirm pendingConfirm;
        UpdateInfo update;
        TrayIcon tray;

        readonly Border root, updateBanner;
        readonly FrameworkElement mainPanel, settingsPanel, changelogPanel, askTrayPanel, confirmPanel;
        readonly List<FrameworkElement> panels;
        readonly TextBlock timeText, timeLabel, statusText, updateText, settingsMsg;
        readonly TextBlock pauseIcon, pauseText, confirmIcon, confirmTitle, confirmText;
        readonly Button confirmYesBtn;
        readonly Path ringPath;
        readonly Button versionBtn, extendBtn, pauseBtn, stopBtn, updateBtn, changelogUpdateBtn, changelogCloseBtn;
        readonly Panel changelogContent;
        readonly ScrollViewer changelogScroll;
        readonly TextBox checkMinBox, warnSecBox;
        readonly RadioButton shutdownOpt, restartOpt, hibernateOpt;
        readonly CheckBox soundBox, trayBox, updatesBox, autoInstallBox;
        readonly PasswordBox tokenBox;
        readonly TextBlock tokenStatus;
        readonly Button saveBtn, removeTokenBtn;
        readonly ScrollViewer settingsScroll;
        bool removeTokenOnSave;

        public MainView(Settings settings)
        {
            this.settings = settings;
            Updater.Token = settings.GitHubToken;
            using (var xaml = Resource("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(xaml);

            root = Find<Border>("Root");
            updateBanner = Find<Border>("UpdateBanner");
            mainPanel = Find<FrameworkElement>("MainPanel");
            settingsPanel = Find<FrameworkElement>("SettingsPanel");
            changelogPanel = Find<FrameworkElement>("ChangelogPanel");
            askTrayPanel = Find<FrameworkElement>("AskTrayPanel");
            confirmPanel = Find<FrameworkElement>("ConfirmPanel");
            panels = new List<FrameworkElement> { mainPanel, settingsPanel, changelogPanel, askTrayPanel, confirmPanel };
            pauseIcon = Find<TextBlock>("PauseIcon");
            pauseText = Find<TextBlock>("PauseText");
            confirmIcon = Find<TextBlock>("ConfirmIcon");
            confirmTitle = Find<TextBlock>("ConfirmTitle");
            confirmText = Find<TextBlock>("ConfirmText");
            confirmYesBtn = Find<Button>("ConfirmYesBtn");
            timeText = Find<TextBlock>("TimeText");
            timeLabel = Find<TextBlock>("TimeLabel");
            statusText = Find<TextBlock>("StatusText");
            updateText = Find<TextBlock>("UpdateText");
            settingsMsg = Find<TextBlock>("SettingsMsg");
            ringPath = Find<Path>("RingArc");
            versionBtn = Find<Button>("VersionBtn");
            extendBtn = Find<Button>("ExtendBtn");
            pauseBtn = Find<Button>("PauseBtn");
            stopBtn = Find<Button>("StopBtn");
            changelogCloseBtn = Find<Button>("ChangelogCloseBtn");
            updateBtn = Find<Button>("UpdateBtn");
            changelogUpdateBtn = Find<Button>("ChangelogUpdateBtn");
            changelogContent = Find<Panel>("ChangelogContent");
            changelogScroll = Find<ScrollViewer>("ChangelogScroll");
            checkMinBox = Find<TextBox>("CheckMinBox");
            warnSecBox = Find<TextBox>("WarnSecBox");
            shutdownOpt = Find<RadioButton>("ShutdownOpt");
            restartOpt = Find<RadioButton>("RestartOpt");
            hibernateOpt = Find<RadioButton>("HibernateOpt");
            soundBox = Find<CheckBox>("SoundBox");
            trayBox = Find<CheckBox>("TrayBox");
            updatesBox = Find<CheckBox>("UpdatesBox");
            autoInstallBox = Find<CheckBox>("AutoInstallBox");
            tokenBox = Find<PasswordBox>("TokenBox");
            tokenStatus = Find<TextBlock>("TokenStatus");
            saveBtn = Find<Button>("SaveBtn");
            removeTokenBtn = Find<Button>("RemoveTokenBtn");
            settingsScroll = Find<ScrollViewer>("SettingsScroll");

            root.Background = background;
            versionBtn.Content = "v" + Updater.Current;
            Find<Image>("AppIcon").Source = LoadImage("icon.png");
            // Set explicitly: a window doesn't reliably pick up the exe's icon for its taskbar button.
            Window.Icon = LoadImage("icon.ico");

            ringFigure.Segments.Add(ringArc);
            ringPath.Data = new PathGeometry { Figures = { ringFigure } };

            Find<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove(); };
            Find<Button>("MinBtn").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Find<Button>("CloseBtn").Click += (s, e) => RequestClose();
            stopBtn.Click += (s, e) => AskToConfirm(Confirm.Quit);
            confirmYesBtn.Click += (s, e) => ConfirmYes();
            Find<Button>("ConfirmNoBtn").Click += (s, e) => ShowPanel(mainPanel);
            Find<Button>("SettingsBtn").Click += (s, e) => ToggleSettings();
            Find<Button>("BackBtn").Click += (s, e) => ShowPanel(mainPanel);
            saveBtn.Click += async (s, e) => await SaveSettings();
            removeTokenBtn.Click += (s, e) =>
            {
                removeTokenOnSave = true;
                tokenBox.Clear();
                ShowTokenState();
            };
            Find<Button>("ResetBtn").Click += (s, e) =>
            {
                FillSettingsForm(new Settings());
                SetSettingsMessage("Defaults filled in. Click Save to keep them.", error: false);
            };
            changelogCloseBtn.Click += (s, e) => ShowPanel(mainPanel);
            Find<Button>("TrayYesBtn").Click += (s, e) => AnswerTrayQuestion(true);
            Find<Button>("TrayNoBtn").Click += (s, e) => AnswerTrayQuestion(false);
            versionBtn.Click += (s, e) => ShowChangelog();
            extendBtn.Click += (s, e) => ImHere();
            pauseBtn.Click += (s, e) => PauseOrResume();
            updateBtn.Click += async (s, e) => await InstallUpdate();
            changelogUpdateBtn.Click += async (s, e) => await InstallUpdate();

            // Space resets the timer during the final warning, so whoever is at
            // the keyboard can respond without finding the mouse.
            Window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Space && phase == Phase.Warn && !failed) { ImHere(); e.Handled = true; }
                else if (e.Key == Key.Escape && confirmPanel.Visibility == Visibility.Visible) { ShowPanel(mainPanel); e.Handled = true; }
            };
            // Swallow keyboard menu mode (a lone Alt tap, or Alt+Space). There's
            // no menu to show, and while in it the window ignores the next key,
            // which would stop Space from resetting the warning.
            Window.SourceInitialized += (s, e) =>
                HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle).AddHook(
                    (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_KEYMENU)
                            handled = true;
                        else if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == SpaceHotkeyId)
                        {
                            if (phase == Phase.Warn && !failed) ImHere();
                            handled = true;
                        }
                        return IntPtr.Zero;
                    });
            Window.Closing += (s, e) =>
            {
                if (quitting) return;
                e.Cancel = true;   // Alt+F4 and the taskbar's Close follow the same rules as the × button
                RequestClose();
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
            timer.Tick += (s, e) => Tick();
            updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
            updateTimer.Tick += async (s, e) => await CheckForUpdate();

            if (settings.CloseToTray == true) EnsureTray();

            Window.ContentRendered += async (s, e) =>
            {
                StartCheck();
                timer.Start();
                CompositionTarget.Rendering += OnFrame;

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
            Window.Closed += (s, e) =>
            {
                timer.Stop();
                updateTimer.Stop();
                CompositionTarget.Rendering -= OnFrame;
                tray?.Dispose();
            };
        }

        T Find<T>(string name) where T : class =>
            Window.FindName(name) as T ?? throw new InvalidOperationException($"MainWindow.xaml has no {typeof(T).Name} named '{name}'.");

        static System.IO.Stream Resource(string name) =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("DeadMansSwitch." + name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");

        /// <summary>Loads an embedded image; an .ico keeps all its sizes, so Windows picks the right one.</summary>
        static ImageSource LoadImage(string name)
        {
            using var stream = Resource(name);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
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
            AnimateBackground(NormalBg);
            extendBtn.Content = "I'm here";
            pauseBtn.Visibility = Visibility.Visible;
            FillRow(stopBtn, alone: false, column: 2);
            ShowPauseState();
            Refresh();
        }

        void StartWarning()
        {
            phase = Phase.Warn;
            paused = false;
            phaseSeconds = settings.WarnSeconds.Value;
            deadline = DateTime.Now.AddSeconds(phaseSeconds);
            warnTicks = 0;
            ringPath.Stroke = RedRing;
            AnimateBackground(WarnBg);
            extendBtn.Content = "I'm still here – keep it on";
            pauseBtn.Visibility = Visibility.Hidden;
            FillRow(stopBtn, alone: true, column: 2);
            tray?.SetPause(false, enabled: false);
            timeLabel.Text = "seconds until " + ActionNoun;
            statusText.Text = "Final warning. Press Space or click below if you're still here.";
            ShowPanel(mainPanel);
            if (settings.PlaySound == true) SystemSounds.Exclamation.Play();
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
            pauseIcon.Text = paused ? "\uE768" : "\uE769";   // play / pause glyphs
            pauseText.Text = paused ? "Resume" : "Pause";
            ringPath.Stroke = paused ? PausedRing : GreenRing;
            timeLabel.Text = paused ? "paused" : "until next check";
            statusText.Text = paused
                ? "Paused. Nothing happens until you resume."
                : $"If nobody answers: {settings.WarnSeconds}-second warning, then {ActionNoun}.";
            tray?.SetPause(paused, enabled: true);
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
                    if (remaining <= 0) { RunEndAction(); return; }
                    if (++warnTicks % (RefrontEveryMs / TickMs) == 0) BringToFront();
                }
            }
            Refresh();   // keeps the text current even when no frames render (minimized or in the tray)
        }

        void RunEndAction()
        {
            timer.Stop();
            ReleaseSpace();
            string args = settings.EndAction switch
            {
                EndAction.Restart => "/r /f /t 0",
                EndAction.Hibernate => "/h",
                _ => "/s /f /t 0",
            };
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
                Quit();
            }
            catch (Exception ex)
            {
                // Stay open so the failure is seen rather than silently doing nothing.
                failed = true;
                timeText.Text = "!";
                timeLabel.Text = ActionNoun + " failed";
                statusText.Text = ex.Message;
                extendBtn.Content = "Start again";
                tray?.SetText("Dead Man's Switch - " + ActionNoun + " failed");
            }
        }

        // ----- drawing -----

        void OnFrame(object sender, EventArgs e) => Refresh();

        void Refresh()
        {
            if (failed) return;
            double remaining = paused ? pausedRemaining : Math.Max(0, (deadline - DateTime.Now).TotalSeconds);
            int whole = (int)Math.Ceiling(remaining);
            string text = phase == Phase.Check
                ? (whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60}:{whole % 60:00}")
                : whole.ToString();
            if (timeText.Text != text)
            {
                timeText.Text = text;
                tray?.SetText(paused ? "Dead Man's Switch - paused"
                    : phase == Phase.Check ? $"Dead Man's Switch - {text} until next check"
                    : $"Dead Man's Switch - {text} s until {ActionNoun}");
            }
            DrawRing(phaseSeconds > 0 ? remaining / phaseSeconds : 0);
        }

        /// <summary>Draws the remaining fraction as an arc running clockwise from 12 o'clock.</summary>
        void DrawRing(double fraction)
        {
            fraction = Math.Max(0, Math.Min(0.99999, fraction));
            double r = (RingSize - RingThickness) / 2, c = RingSize / 2;
            double angle = fraction * 2 * Math.PI;
            ringFigure.StartPoint = new Point(c, c - r);
            ringArc.Point = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            ringArc.Size = new Size(r, r);
            ringArc.IsLargeArc = fraction > 0.5;
            ringPath.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        void AnimateBackground(Color to) =>
            background.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(400)));

        // ----- window, tray and closing -----

        /// <summary>
        /// Two-button rows are 3-column grids (button, gap, button). When one
        /// button is hidden, the other takes the whole row instead of its column.
        /// </summary>
        static void FillRow(Button button, bool alone, int column)
        {
            Grid.SetColumn(button, alone ? 0 : column);
            Grid.SetColumnSpan(button, alone ? 3 : 1);
        }

        void ShowPanel(FrameworkElement panel)
        {
            foreach (var p in panels) p.Visibility = p == panel ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>Shows the window, from the tray, minimized or behind other windows.</summary>
        public void ShowWindow()
        {
            if (!Window.IsVisible) Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
            MaybeShowReleaseNotes();
        }

        void BringToFront()
        {
            if (headless) return;
            if (!Window.IsVisible) Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Topmost = false;
            Window.Topmost = true;
            NativeMethods.ForceForeground(new WindowInteropHelper(Window).Handle);
            Window.Activate();
            // Keys go to the focused element, so keep focus on the button: Space
            // (handled above) and Enter then always work, even if focus was left
            // in a panel that is now hidden.
            extendBtn.Focus();
        }

        /// <summary>
        /// During the final warning, Space anywhere resets the timer, even when
        /// Windows won't let this window take the foreground. If another app
        /// already owns Space as a hotkey, the window's own key handling remains.
        /// </summary>
        void ClaimSpace()
        {
            if (headless || spaceClaimed) return;
            spaceClaimed = NativeMethods.ClaimSpace(new WindowInteropHelper(Window).Handle, SpaceHotkeyId);
        }

        void ReleaseSpace()
        {
            if (!spaceClaimed) return;
            NativeMethods.ReleaseSpace(new WindowInteropHelper(Window).Handle, SpaceHotkeyId);
            spaceClaimed = false;
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
            if (!headless) ShowWindow();
            pendingConfirm = what;
            if (what == Confirm.Pause)
            {
                confirmIcon.Text = "\uE769";
                confirmIcon.Foreground = Amber;
                confirmTitle.Text = "Pause the timer?";
                confirmText.Text = $"While it's paused, the PC won't {ActionVerb} no matter how long nobody answers. Resume any time.";
                confirmYesBtn.Content = "Pause the timer";
                confirmYesBtn.Background = Amber;
                confirmYesBtn.Foreground = DarkText;
            }
            else
            {
                confirmIcon.Text = "\uE71A";
                confirmIcon.Foreground = StopRed;
                confirmTitle.Text = "Stop the timer and quit?";
                confirmText.Text = $"Dead Man's Switch will close, and the PC won't {ActionVerb} when the time runs out.";
                confirmYesBtn.Content = "Stop and quit";
                confirmYesBtn.Background = StopRed;
                confirmYesBtn.Foreground = White;
            }
            ShowPanel(confirmPanel);
        }

        void ConfirmYes()
        {
            var what = pendingConfirm;
            pendingConfirm = Confirm.None;
            ShowPanel(mainPanel);
            if (what == Confirm.Pause && !paused) TogglePause();   // ignored if the warning started meanwhile
            else if (what == Confirm.Quit) Quit();
        }

        void EnsureTray()
        {
            if (tray != null) return;
            tray = new TrayIcon(open: ShowWindow, imHere: ImHere, togglePause: PauseOrResume,
                quit: () => AskToConfirm(Confirm.Quit));
            tray.SetPause(paused, enabled: phase == Phase.Check && !failed);
        }

        void RemoveTray()
        {
            tray?.Dispose();
            tray = null;
        }

        /// <summary>The × button: hide to the tray or quit, asking the first time.</summary>
        void RequestClose()
        {
            switch (settings.CloseToTray)
            {
                case true: HideToTray(); break;
                case false: AskToConfirm(Confirm.Quit); break;
                default: ShowPanel(askTrayPanel); break;
            }
        }

        void AnswerTrayQuestion(bool toTray)
        {
            settings.CloseToTray = toTray;
            TrySave();
            ShowPanel(mainPanel);
            if (toTray) HideToTray();
            else Quit();
        }

        void HideToTray()
        {
            EnsureTray();
            ShowPanel(mainPanel);
            Window.Hide();
            if (!trayHintShown)
            {
                tray.ShowHint("Dead Man's Switch is still running", "The timer keeps counting. Click the icon to open it.");
                trayHintShown = true;
            }
        }

        void Quit()
        {
            ReleaseSpace();
            quitting = true;
            RemoveTray();
            Window.Close();
        }

        // ----- settings -----

        void ToggleSettings()
        {
            if (phase == Phase.Warn) return;   // the warning keeps the "I'm here" button on screen
            if (settingsPanel.Visibility == Visibility.Visible) { ShowPanel(mainPanel); return; }
            FillSettingsForm(settings);
            removeTokenOnSave = false;
            tokenBox.Clear();
            ShowTokenState();
            settingsMsg.Text = "";
            settingsScroll.ScrollToTop();
            ShowPanel(settingsPanel);
        }

        /// <summary>The token itself is never shown; the box is for typing a new one.</summary>
        void ShowTokenState()
        {
            bool saved = settings.GitHubToken != null && !removeTokenOnSave;
            tokenStatus.Text = saved
                ? "A token is saved. Type a new one to replace it."
                : removeTokenOnSave
                    ? "The saved token will be removed when you save."
                    : "Only needed if the repository is private, or GitHub starts limiting update checks. "
                      + "It's checked with GitHub when you save, and stored encrypted for your Windows account.";
            removeTokenBtn.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        }

        void FillSettingsForm(Settings from)
        {
            checkMinBox.Text = from.CheckMinutes.ToString();
            warnSecBox.Text = from.WarnSeconds.ToString();
            shutdownOpt.IsChecked = from.EndAction == EndAction.Shutdown;
            restartOpt.IsChecked = from.EndAction == EndAction.Restart;
            hibernateOpt.IsChecked = from.EndAction == EndAction.Hibernate;
            soundBox.IsChecked = from.PlaySound;
            trayBox.IsChecked = from.CloseToTray == true;
            updatesBox.IsChecked = from.CheckForUpdates;
            autoInstallBox.IsChecked = from.AutoInstallUpdates;
        }

        void SetSettingsMessage(string text, bool error)
        {
            settingsMsg.Foreground = error ? ErrorText : InfoText;
            settingsMsg.Text = text;
        }

        async Task SaveSettings()
        {
            if (!int.TryParse(checkMinBox.Text.Trim(), out int minutes) || !Settings.ValidCheckMinutes(minutes))
            {
                SetSettingsMessage($"Check interval must be {Settings.MinCheckMinutes}-{Settings.MaxCheckMinutes} minutes.", error: true);
                return;
            }
            if (!int.TryParse(warnSecBox.Text.Trim(), out int seconds) || !Settings.ValidWarnSeconds(seconds))
            {
                SetSettingsMessage($"Final warning must be {Settings.MinWarnSeconds}-{Settings.MaxWarnSeconds} seconds.", error: true);
                return;
            }

            // A new token is only kept if GitHub accepts it.
            string newToken = tokenBox.Password.Trim();
            if (newToken.Length > 0)
            {
                SetSettingsMessage("Checking the token with GitHub...", error: false);
                saveBtn.IsEnabled = false;
                string problem = await Updater.TestTokenAsync(newToken);
                saveBtn.IsEnabled = true;
                if (problem != null) { SetSettingsMessage(problem, error: true); return; }
            }

            settings.CheckMinutes = minutes;
            settings.WarnSeconds = seconds;
            settings.EndAction = restartOpt.IsChecked == true ? EndAction.Restart
                               : hibernateOpt.IsChecked == true ? EndAction.Hibernate
                               : EndAction.Shutdown;
            settings.PlaySound = soundBox.IsChecked == true;
            settings.CloseToTray = trayBox.IsChecked == true;
            settings.CheckForUpdates = updatesBox.IsChecked == true;
            settings.AutoInstallUpdates = autoInstallBox.IsChecked == true;
            if (newToken.Length > 0) settings.GitHubToken = newToken;
            else if (removeTokenOnSave) settings.GitHubToken = null;

            try { settings.Save(); }
            catch (Exception ex)
            {
                SetSettingsMessage("Couldn't save settings: " + ex.Message, error: true);
                return;
            }

            Updater.Token = settings.GitHubToken;
            if (settings.CloseToTray == true) EnsureTray();
            else RemoveTray();
            if (settings.CheckForUpdates == true) updateTimer.Start();
            else updateTimer.Stop();

            ShowPanel(mainPanel);
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
            Changelog.Render(changelogContent, markdown.ToString());

            changelogUpdateBtn.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
            FillRow(changelogCloseBtn, alone: update == null, column: 0);
            if (update != null) changelogUpdateBtn.Content = $"Update to {update.Version}";
            changelogScroll.ScrollToTop();
            ShowPanel(changelogPanel);
        }

        /// <summary>Shows a newer release's notes once, when the window is open and not busy.</summary>
        void MaybeShowReleaseNotes()
        {
            if (update == null || settings.SeenReleaseNotesFor == update.Version) return;
            if (!Window.IsVisible || phase == Phase.Warn || settingsPanel.Visibility == Visibility.Visible
                || askTrayPanel.Visibility == Visibility.Visible || confirmPanel.Visibility == Visibility.Visible) return;
            settings.SeenReleaseNotesFor = update.Version;
            TrySave();
            ShowChangelog();
        }

        /// <summary>
        /// Looks for a newer release. At launch (with automatic installs on) it's
        /// installed straight away; otherwise it's offered with a banner and its notes.
        /// </summary>
        async Task CheckForUpdate(bool atLaunch = false)
        {
            var found = await Updater.CheckAsync();
            if (found == null) return;
            bool isNew = update?.Version != found.Version;
            update = found;
            if (atLaunch && settings.AutoInstallUpdates == true && phase == Phase.Check)
            {
                await InstallUpdate();
                return;
            }
            updateText.Text = $"Version {update.Version} is available.";
            updateBanner.Visibility = Visibility.Visible;
            if (changelogPanel.Visibility == Visibility.Visible) ShowChangelog();   // add the new notes to what's showing

            if (Window.IsVisible) MaybeShowReleaseNotes();
            else if (isNew) tray?.ShowHint($"Dead Man's Switch {update.Version} is available", "Open the app to see what's new and update.");
        }

        async Task InstallUpdate()
        {
            if (update == null) return;
            updateBtn.IsEnabled = changelogUpdateBtn.IsEnabled = false;
            updateText.Text = $"Updating to {update.Version}...";
            updateBanner.Visibility = Visibility.Visible;
            try
            {
                await Updater.InstallAsync(update);
                Quit();
            }
            catch (Exception ex)
            {
                updateText.Text = "Update failed: " + ex.Message;
                updateBanner.Visibility = Visibility.Visible;
                ShowPanel(mainPanel);
                updateBtn.IsEnabled = changelogUpdateBtn.IsEnabled = true;
            }
        }

        // ----- CI helpers -----

        /// <summary>
        /// Renders each state to a PNG without showing the window (used by
        /// --screenshot, so CI can show the UI).
        /// </summary>
        public void SaveScreenshots(string dir)
        {
            // An unshown window can't be captured, so render its content on its
            // own, taking the styles along with it.
            headless = true;
            var content = (FrameworkElement)Window.Content;
            var resources = Window.Resources;
            Window.Resources = new ResourceDictionary();
            Window.Content = null;
            content.Resources = resources;
            System.IO.Directory.CreateDirectory(dir);

            void Shot(string name, double secondsLeft)
            {
                if (paused) pausedRemaining = secondsLeft;
                else deadline = DateTime.Now.AddSeconds(secondsLeft);
                Refresh();
                background.BeginAnimation(SolidColorBrush.ColorProperty, null);   // skip the fade
                content.Measure(new Size(Window.Width, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, Window.Width, content.DesiredSize.Height));
                content.UpdateLayout();
                const double scale = 2;
                // DesiredSize includes the outer margin that holds the shadow; ActualWidth doesn't.
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Window.Width * scale),
                    (int)Math.Ceiling(content.DesiredSize.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var png = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
                using var file = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
                png.Save(file);
            }

            StartCheck();
            background.Color = NormalBg;
            Shot("check", phaseSeconds * 0.62);
            TogglePause();
            Shot("paused", phaseSeconds * 0.62);
            TogglePause();
            ToggleSettings();
            Shot("settings", phaseSeconds * 0.62);
            settingsScroll.ScrollToEnd();
            Shot("settings-updates", phaseSeconds * 0.62);
            ShowChangelog();
            Shot("changelog", phaseSeconds * 0.62);
            ShowPanel(askTrayPanel);
            Shot("tray-question", phaseSeconds * 0.62);
            AskToConfirm(Confirm.Pause);
            Shot("confirm-pause", phaseSeconds * 0.62);
            AskToConfirm(Confirm.Quit);
            Shot("confirm-quit", phaseSeconds * 0.62);
            StartWarning();
            background.Color = WarnBg;
            Shot("warning", 23.4);
        }

        /// <summary>Runs the phases and panels without showing the window (used by --selftest).</summary>
        public void SelfTest()
        {
            headless = true;
            StartCheck();
            PauseOrResume();
            if (paused || confirmPanel.Visibility != Visibility.Visible) throw new Exception("Pause didn't ask for confirmation.");
            ConfirmYes();
            if (!paused) throw new Exception("Confirming pause did not pause.");
            PauseOrResume();
            if (paused) throw new Exception("Resume did not resume.");
            AskToConfirm(Confirm.Quit);
            ShowPanel(mainPanel);   // "Keep the timer running"
            if (quitting) throw new Exception("Backing out of quit still quit.");
            ToggleSettings();
            FillSettingsForm(new Settings());
            ShowChangelog();
            if (changelogContent.Children.Count == 0) throw new Exception("The changelog rendered empty.");
            ShowPanel(askTrayPanel);
            StartWarning();
            ImHere();
            if (phase != Phase.Check) throw new Exception("I'm here did not leave the warning.");
            timer.Stop();
        }
    }
}
