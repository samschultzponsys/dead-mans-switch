using System;
using System.Diagnostics;
using System.Media;
using System.Reflection;
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
    /// a check period the user resets with "I'm here", then a final warning
    /// that ends in the configured shutdown/restart/hibernate.
    /// </summary>
    sealed class MainView
    {
        enum Phase { Check, Warn }

        const int TickMs = 250;
        const int RefrontEveryMs = 2000;   // during the warning, pull the window forward this often
        const double RingSize = 232, RingThickness = 14;

        static readonly Color NormalBg = Color.FromRgb(0x16, 0x18, 0x1D);
        static readonly Color WarnBg = Color.FromRgb(0x33, 0x14, 0x16);
        static readonly Brush GreenRing = Frozen(Color.FromRgb(0x2E, 0xCC, 0x71));
        static readonly Brush RedRing = Frozen(Color.FromRgb(0xE7, 0x4C, 0x3C));

        public Window Window { get; }

        readonly Settings settings;
        readonly DispatcherTimer timer;
        readonly SolidColorBrush background = new SolidColorBrush(NormalBg);
        readonly PathFigure ringFigure = new PathFigure { IsClosed = false };
        readonly ArcSegment ringArc = new ArcSegment { SweepDirection = SweepDirection.Clockwise };

        Phase phase;
        DateTime deadline;
        double phaseSeconds;
        int warnTicks;
        bool failed;   // the end action couldn't run; the error stays on screen
        UpdateInfo update;

        readonly Border root, updateBanner;
        readonly Panel mainPanel, settingsPanel;
        readonly TextBlock versionText, timeText, timeLabel, statusText, updateText, settingsMsg;
        readonly Path ringPath;
        readonly Button extendBtn, updateBtn;
        readonly TextBox checkMinBox, warnSecBox;
        readonly RadioButton shutdownOpt, restartOpt, hibernateOpt;
        readonly CheckBox soundBox, updatesBox;

        public MainView(Settings settings)
        {
            this.settings = settings;
            using (var xaml = Resource("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(xaml);

            root = Find<Border>("Root");
            updateBanner = Find<Border>("UpdateBanner");
            mainPanel = Find<Panel>("MainPanel");
            settingsPanel = Find<Panel>("SettingsPanel");
            versionText = Find<TextBlock>("VersionText");
            timeText = Find<TextBlock>("TimeText");
            timeLabel = Find<TextBlock>("TimeLabel");
            statusText = Find<TextBlock>("StatusText");
            updateText = Find<TextBlock>("UpdateText");
            settingsMsg = Find<TextBlock>("SettingsMsg");
            ringPath = Find<Path>("RingArc");
            extendBtn = Find<Button>("ExtendBtn");
            updateBtn = Find<Button>("UpdateBtn");
            checkMinBox = Find<TextBox>("CheckMinBox");
            warnSecBox = Find<TextBox>("WarnSecBox");
            shutdownOpt = Find<RadioButton>("ShutdownOpt");
            restartOpt = Find<RadioButton>("RestartOpt");
            hibernateOpt = Find<RadioButton>("HibernateOpt");
            soundBox = Find<CheckBox>("SoundBox");
            updatesBox = Find<CheckBox>("UpdatesBox");

            root.Background = background;
            versionText.Text = "v" + Updater.Current;
            Find<Image>("AppIcon").Source = LoadIcon();

            ringFigure.Segments.Add(ringArc);
            ringPath.Data = new PathGeometry { Figures = { ringFigure } };

            Find<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove(); };
            Find<Button>("MinBtn").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Find<Button>("CloseBtn").Click += (s, e) => Window.Close();
            Find<Button>("StopBtn").Click += (s, e) => Window.Close();
            Find<Button>("SettingsBtn").Click += (s, e) => ShowSettings(settingsPanel.Visibility != Visibility.Visible);
            Find<Button>("BackBtn").Click += (s, e) => ShowSettings(false);
            Find<Button>("SaveBtn").Click += (s, e) => SaveSettings();
            extendBtn.Click += (s, e) => { StartCheck(); timer.Start(); };
            updateBtn.Click += async (s, e) => await InstallUpdate();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
            timer.Tick += (s, e) => Tick();

            Window.ContentRendered += async (s, e) =>
            {
                StartCheck();
                timer.Start();
                CompositionTarget.Rendering += OnFrame;
                if (settings.CheckForUpdates == true) await CheckForUpdate();
            };
            Window.Closed += (s, e) =>
            {
                timer.Stop();
                CompositionTarget.Rendering -= OnFrame;
            };
        }

        T Find<T>(string name) where T : class =>
            Window.FindName(name) as T ?? throw new InvalidOperationException($"MainWindow.xaml has no {typeof(T).Name} named '{name}'.");

        static System.IO.Stream Resource(string name) =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("DeadMansSwitch." + name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");

        static ImageSource LoadIcon()
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = Resource("icon.png");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        string ActionNoun => settings.EndAction switch
        {
            EndAction.Restart => "restart",
            EndAction.Hibernate => "hibernation",
            _ => "shutdown",
        };

        // ----- phases -----

        void StartCheck()
        {
            phase = Phase.Check;
            failed = false;
            phaseSeconds = settings.CheckMinutes.Value * 60;
            deadline = DateTime.Now.AddSeconds(phaseSeconds);
            ringPath.Stroke = GreenRing;
            AnimateBackground(NormalBg);
            extendBtn.Content = "I'm here";
            timeLabel.Text = "until next check";
            statusText.Text = $"If nobody answers: {settings.WarnSeconds}-second warning, then {ActionNoun}.";
            Refresh();
        }

        void StartWarning()
        {
            phase = Phase.Warn;
            phaseSeconds = settings.WarnSeconds.Value;
            deadline = DateTime.Now.AddSeconds(phaseSeconds);
            warnTicks = 0;
            ringPath.Stroke = RedRing;
            AnimateBackground(WarnBg);
            extendBtn.Content = "I'm still here – keep it on";
            timeLabel.Text = "seconds until " + ActionNoun;
            statusText.Text = "Final warning. Click below if you're still here.";
            ShowSettings(false);
            if (settings.PlaySound == true) SystemSounds.Exclamation.Play();
            BringToFront();
            Refresh();
        }

        void Tick()
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
            Refresh();   // keeps the text current even when no frames render (minimized)
        }

        void RunEndAction()
        {
            timer.Stop();
            string args = settings.EndAction switch
            {
                EndAction.Restart => "/r /f /t 0",
                EndAction.Hibernate => "/h",
                _ => "/s /f /t 0",
            };
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
                Window.Close();
            }
            catch (Exception ex)
            {
                // Stay open so the failure is seen rather than silently doing nothing.
                failed = true;
                timeText.Text = "!";
                timeLabel.Text = ActionNoun + " failed";
                statusText.Text = ex.Message;
                extendBtn.Content = "Start again";
            }
        }

        // ----- drawing -----

        void OnFrame(object sender, EventArgs e) => Refresh();

        void Refresh()
        {
            if (failed) return;
            double remaining = Math.Max(0, (deadline - DateTime.Now).TotalSeconds);
            int whole = (int)Math.Ceiling(remaining);
            string text = phase == Phase.Check
                ? (whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60}:{whole % 60:00}")
                : whole.ToString();
            if (timeText.Text != text) timeText.Text = text;
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

        void BringToFront()
        {
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            var handle = new WindowInteropHelper(Window).Handle;
            // A tap of Alt lets this process take the foreground from another app.
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.SetForegroundWindow(handle);
            Window.Topmost = false;
            Window.Topmost = true;
            Window.Activate();
        }

        // ----- settings -----

        void ShowSettings(bool show)
        {
            if (show)
            {
                checkMinBox.Text = settings.CheckMinutes.ToString();
                warnSecBox.Text = settings.WarnSeconds.ToString();
                shutdownOpt.IsChecked = settings.EndAction == EndAction.Shutdown;
                restartOpt.IsChecked = settings.EndAction == EndAction.Restart;
                hibernateOpt.IsChecked = settings.EndAction == EndAction.Hibernate;
                soundBox.IsChecked = settings.PlaySound;
                updatesBox.IsChecked = settings.CheckForUpdates;
                settingsMsg.Text = "";
            }
            settingsPanel.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            mainPanel.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        }

        void SaveSettings()
        {
            if (!int.TryParse(checkMinBox.Text.Trim(), out int minutes) || !Settings.ValidCheckMinutes(minutes))
            {
                settingsMsg.Text = $"Check interval must be {Settings.MinCheckMinutes}-{Settings.MaxCheckMinutes} minutes.";
                return;
            }
            if (!int.TryParse(warnSecBox.Text.Trim(), out int seconds) || !Settings.ValidWarnSeconds(seconds))
            {
                settingsMsg.Text = $"Final warning must be {Settings.MinWarnSeconds}-{Settings.MaxWarnSeconds} seconds.";
                return;
            }

            settings.CheckMinutes = minutes;
            settings.WarnSeconds = seconds;
            settings.EndAction = restartOpt.IsChecked == true ? EndAction.Restart
                               : hibernateOpt.IsChecked == true ? EndAction.Hibernate
                               : EndAction.Shutdown;
            settings.PlaySound = soundBox.IsChecked == true;
            settings.CheckForUpdates = updatesBox.IsChecked == true;

            try { settings.Save(); }
            catch (Exception ex)
            {
                settingsMsg.Text = "Couldn't save settings: " + ex.Message;
                return;
            }
            ShowSettings(false);
            if (phase == Phase.Check) StartCheck();   // a warning in progress keeps its length
        }

        // ----- updates -----

        async System.Threading.Tasks.Task CheckForUpdate()
        {
            update = await Updater.CheckAsync();
            if (update == null) return;
            updateText.Text = $"Version {update.Version} is available.";
            updateBanner.Visibility = Visibility.Visible;
        }

        async System.Threading.Tasks.Task InstallUpdate()
        {
            updateBtn.IsEnabled = false;
            updateText.Text = $"Downloading {update.Version}...";
            try
            {
                await Updater.InstallAsync(update);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                updateText.Text = "Update failed: " + ex.Message;
                updateBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// Renders the check, warning and settings states to PNGs without
        /// showing the window (used by --screenshot, so CI can show the UI).
        /// </summary>
        public void SaveScreenshots(string dir)
        {
            // An unshown window can't be captured, so render its content on its
            // own, taking the styles along with it.
            var content = (FrameworkElement)Window.Content;
            var resources = Window.Resources;
            Window.Resources = new ResourceDictionary();
            Window.Content = null;
            content.Resources = resources;
            System.IO.Directory.CreateDirectory(dir);

            void Shot(string name, double secondsLeft)
            {
                deadline = DateTime.Now.AddSeconds(secondsLeft);
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
            ShowSettings(true);
            Shot("settings", phaseSeconds * 0.62);
            StartWarning();   // also closes settings
            background.Color = WarnBg;
            Shot("warning", 23.4);
        }

        /// <summary>Runs both phases and the settings panel without showing the window (used by --selftest).</summary>
        public void SelfTest()
        {
            StartCheck();
            StartWarning();
            ShowSettings(true);
            ShowSettings(false);
        }
    }
}
