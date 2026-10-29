using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DeadMansSwitch
{
    static class Program
    {
        // Per Windows session: one copy per signed-in user.
        const string InstanceName = @"Local\DeadMansSwitch.Instance";
        const string ShowSignalName = @"Local\DeadMansSwitch.Show";

        static Mutex instance;   // static so it lives as long as the process

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

            if (!TakeInstance(waitForPrevious: Updater.StartedByUpdate(args)))
            {
                // Already running (maybe hidden in the tray): bring that one up instead.
                try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch (Exception) { }
                return 0;
            }

            Updater.CleanUpAfterUpdate(args);
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), "Dead Man's Switch crashed", MessageBoxButton.OK, MessageBoxImage.Error);
            var view = new MainView(Settings.Load());
            ListenForShowSignal(view);
            return app.Run(view.Window);
        }

        /// <summary>
        /// Claims the single-instance lock. After an update the old copy may
        /// still be exiting, so the new one waits for it to let go.
        /// </summary>
        static bool TakeInstance(bool waitForPrevious)
        {
            instance = new Mutex(true, InstanceName, out bool created);
            if (created) return true;
            if (!waitForPrevious) return false;
            try { return instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { return true; }   // the old copy exited without releasing it; it's ours now
        }

        static void ListenForShowSignal(MainView view)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            new Thread(() =>
            {
                while (signal.WaitOne())
                    view.Window.Dispatcher.BeginInvoke(new Action(view.ShowWindow));
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Builds the window and runs it through its states without showing it,
        /// and checks settings, update selection and the built-in changelog.
        /// CI runs this on Windows, since the XAML is only parsed at runtime.
        /// Exit code 0 means it passed.
        /// </summary>
        static int SelfTest()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), $"dms-selftest-{Guid.NewGuid():N}.json");
                var saved = new Settings { CheckMinutes = 90, WarnSeconds = 45, EndAction = EndAction.Hibernate, PlaySound = false, CloseToTray = true };
                saved.Save(path);
                var loaded = Settings.Load(path);
                File.Delete(path);
                if (loaded.CheckMinutes != 90 || loaded.WarnSeconds != 45 || loaded.EndAction != EndAction.Hibernate
                    || loaded.PlaySound != false || loaded.CloseToTray != true)
                    throw new Exception("Settings did not survive a save and load.");

                File.WriteAllText(path, "{\"CheckMinutes\": 0, \"WarnSeconds\": 60}");   // older, partial file
                var partial = Settings.Load(path);
                File.Delete(path);
                if (partial.CheckMinutes != 5 || partial.PlaySound != true || partial.EndAction != EndAction.Shutdown || partial.CloseToTray != null)
                    throw new Exception("Settings defaults were not applied to a partial file.");

                Updater.SelfTest();

                if (!Changelog.Installed().StartsWith($"## [{Updater.Current}]"))
                    throw new Exception($"CHANGELOG.md has no section for {Updater.Current} at the top. Date the Unreleased notes before releasing.");

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
