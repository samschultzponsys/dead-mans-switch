using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace DeadMansSwitch
{
    /// <summary>
    /// Linux desktop integration through D-Bus, so it works the same in a
    /// Flatpak sandbox as in a .deb/.rpm install (no host tools needed).
    /// </summary>
    static class LinuxDesktop
    {
        /// <summary>
        /// Asks systemd-logind to power off, reboot or hibernate. Active local
        /// users are allowed this without a password; with other users signed
        /// in, logind may refuse, and the error explains why.
        /// </summary>
        public static async Task<string> RunEndActionAsync(EndAction action)
        {
            string method = action switch
            {
                EndAction.Restart => "Reboot",
                EndAction.Hibernate => "Hibernate",
                _ => "PowerOff",
            };
            try
            {
                using var connection = new Connection(Address.System);
                await connection.ConnectAsync();
                await connection.CallMethodAsync(LogindCall(connection, method));
                return null;
            }
            catch (Exception ex)
            {
                return $"systemd-logind refused to {method}: {ex.Message}";
            }
        }

        /// <summary>
        /// True if the desktop shows tray icons (StatusNotifierItem). KDE and most
        /// others do; GNOME needs the AppIndicator extension.
        /// </summary>
        public static bool TraySupported()
        {
            try
            {
                var check = Task.Run(async () =>
                {
                    using var connection = new Connection(Address.Session);
                    await connection.ConnectAsync();
                    return await connection.CallMethodAsync(NameHasOwnerCall(connection, "org.kde.StatusNotifierWatcher"),
                        (Message message, object state) => message.GetBodyReader().ReadBool());
                });
                return check.Wait(TimeSpan.FromSeconds(2)) && check.Result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // MessageWriter can't live in an async method, so messages are built here.
        static MessageBuffer LogindCall(Connection connection, string method)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: "org.freedesktop.login1", path: "/org/freedesktop/login1",
                @interface: "org.freedesktop.login1.Manager", member: method, signature: "b");
            writer.WriteBool(false);   // not interactive: nobody may be there to answer a password prompt
            return writer.CreateMessage();
        }

        static MessageBuffer NameHasOwnerCall(Connection connection, string name)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
            writer.WriteString(name);
            return writer.CreateMessage();
        }

        static string soundFile;

        /// <summary>Plays the bundled chime with whichever player the system has.</summary>
        public static void PlayWarningSound()
        {
            try
            {
                if (soundFile == null)
                {
                    soundFile = Path.Combine(Path.GetTempPath(), $"dead-mans-switch-warning-{Environment.UserName}.wav");
                    using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeadMansSwitch.warning.wav");
                    using var file = File.Create(soundFile);
                    source.CopyTo(file);
                }
            }
            catch (Exception) { return; }

            foreach (var player in new[] { "pw-play", "paplay", "aplay" })
            {
                try
                {
                    var start = new ProcessStartInfo(player) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    start.ArgumentList.Add(soundFile);
                    Process.Start(start);
                    return;
                }
                catch (Win32Exception) { }   // not installed; try the next one
            }
        }
    }
}
