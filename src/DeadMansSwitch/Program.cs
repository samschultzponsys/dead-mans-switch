using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace DeadMansSwitch
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--selftest")) return SelfTest(args);
            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length) return Screenshots(args[shot + 1]);

            if (!SingleInstance.TryClaim(waitForPrevious: Updater.StartedByUpdate(args)))
            {
                SingleInstance.SignalExisting();   // already running (maybe in the tray): bring that one up
                return 0;
            }
            Updater.CleanUpAfterUpdate(args);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>The app ID: Linux desktop file, icons, Flatpak. Also the X11 window class, so docks match windows to it.</summary>
        public const string AppId = "io.github.samschultzponsys.DeadMansSwitch";

        // Also used by the XAML designer.
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect()
                .With(new X11PlatformOptions { WmClass = AppId })
                .LogToTrace();

        /// <summary>Renders without a display, using the real Skia drawing (for screenshots and the self-test).</summary>
        static void StartHeadless() =>
            AppBuilder.Configure<App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

        static int Screenshots(string dir)
        {
            StartHeadless();
            var window = new MainWindow(new Settings(), headless: true);   // defaults, as a new user sees them
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.SaveScreenshots(dir);
            Console.WriteLine($"Screenshots saved to {dir}");
            return 0;
        }

        /// <summary>
        /// Builds the window and runs it through its states without a display,
        /// and checks settings, update selection, install detection and the
        /// built-in changelog. CI runs this on Windows and in Debian, Fedora and
        /// Flatpak installs. --expect-kind names how this copy should find
        /// itself installed (e.g. deb, rpm, flatpak, windows-installed,
        /// windows-portable). Exit code 0 means it passed.
        /// </summary>
        static int SelfTest(string[] args)
        {
            try
            {
                int k = Array.IndexOf(args, "--expect-kind");
                if (k >= 0)
                {
                    string expected = k + 1 < args.Length ? args[k + 1].Replace("-", "") : "";
                    if (!string.Equals(Updater.Kind.ToString(), expected, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Thinks it's installed as {Updater.Kind}, expected {args[k + 1]}.");
                }
                bool portable = Updater.Kind == InstallKind.WindowsPortable && Settings.IsPortable;
                string expectedDir = portable ? AppContext.BaseDirectory : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!Settings.FilePath.StartsWith(expectedDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Settings would be saved in the wrong place: {Settings.FilePath}");

                string path = Path.Combine(Path.GetTempPath(), $"dms-selftest-{Guid.NewGuid():N}.json");
                var saved = new Settings
                {
                    CheckMinutes = 90, WarnSeconds = 45, EndAction = EndAction.Hibernate, PlaySound = false, CloseToTray = true,
                    AutoInstallUpdates = false, GitHubToken = "ghp_selftest",
                };
                saved.Save(path);
                var loaded = Settings.Load(path);
                bool privateFile = OperatingSystem.IsWindows()
                    || File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(path);
                if (loaded.CheckMinutes != 90 || loaded.WarnSeconds != 45 || loaded.EndAction != EndAction.Hibernate
                    || loaded.PlaySound != false || loaded.CloseToTray != true || loaded.AutoInstallUpdates != false
                    || loaded.GitHubToken != "ghp_selftest")
                    throw new Exception("Settings did not survive a save and load.");
                if (OperatingSystem.IsWindows() && saved.GitHubTokenProtected.Contains("selftest"))
                    throw new Exception("The token was saved unencrypted.");
                if (!privateFile)
                    throw new Exception("The settings file can be read by other users.");

                File.WriteAllText(path, "{\"CheckMinutes\": 0, \"WarnSeconds\": 60}");   // older, partial file
                var partial = Settings.Load(path);
                File.Delete(path);
                if (partial.CheckMinutes != 5 || partial.PlaySound != true || partial.EndAction != EndAction.Shutdown
                    || partial.CloseToTray != null || partial.AutoInstallUpdates != true || partial.GitHubToken != null)
                    throw new Exception("Settings defaults were not applied to a partial file.");

                Updater.SelfTest();

                if (!Changelog.Installed().StartsWith($"## [{Updater.Current}]"))
                    throw new Exception($"CHANGELOG.md has no section for {Updater.Current} at the top. Date the Unreleased notes before releasing.");

                StartHeadless();
                var window = new MainWindow(loaded, headless: true);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.SelfTest();
                Console.WriteLine($"Self-test passed (v{Updater.Current}, {Updater.Kind}).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
