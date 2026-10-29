using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace DeadMansSwitch
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--selftest")) return SelfTest();
            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                new MainView(new Settings()).SaveScreenshots(args[shot + 1]);   // defaults, as a new user sees them
                return 0;
            }

            Updater.CleanUpAfterUpdate();
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), "Dead Man's Switch crashed", MessageBoxButton.OK, MessageBoxImage.Error);
            return app.Run(new MainView(Settings.Load()).Window);
        }

        /// <summary>
        /// Builds the window and runs it through both phases without showing it,
        /// and round-trips the settings file. CI runs this on Windows, since the
        /// XAML is only parsed at runtime. Exit code 0 means it passed.
        /// </summary>
        static int SelfTest()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), $"dms-selftest-{Guid.NewGuid():N}.json");
                var saved = new Settings { CheckMinutes = 90, WarnSeconds = 45, EndAction = EndAction.Hibernate, PlaySound = false };
                saved.Save(path);
                var loaded = Settings.Load(path);
                File.Delete(path);
                if (loaded.CheckMinutes != 90 || loaded.WarnSeconds != 45 || loaded.EndAction != EndAction.Hibernate || loaded.PlaySound != false)
                    throw new Exception("Settings did not survive a save and load.");

                File.WriteAllText(path, "{\"CheckMinutes\": 0, \"WarnSeconds\": 60}");   // older, partial file
                var partial = Settings.Load(path);
                File.Delete(path);
                if (partial.CheckMinutes != 5 || partial.PlaySound != true || partial.EndAction != EndAction.Shutdown)
                    throw new Exception("Settings defaults were not applied to a partial file.");

                new MainView(loaded).SelfTest();
                Console.WriteLine($"Self-test passed (v{Updater.Current}).");
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
