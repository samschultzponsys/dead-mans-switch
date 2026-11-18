using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace DeadMansSwitch
{
    /// <summary>
    /// Keeps one copy running per user. The first copy holds a lock file and
    /// listens on a local socket; a second copy pokes that socket (so the first
    /// shows its window) and exits. Unix domain sockets work on Windows 10+ too.
    /// </summary>
    static class SingleInstance
    {
        static FileStream lockFile;   // held open for the life of the process
        static Socket listener;

        /// <summary>Per user, and inside a Flatpak, the folder all its instances share.</summary>
        static string Folder
        {
            get
            {
                if (OperatingSystem.IsWindows())
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadMansSwitch");
                string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                string flatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
                if (string.IsNullOrEmpty(runtime)) return Path.Combine(Path.GetTempPath(), $"dead-mans-switch-{Environment.UserName}");
                return string.IsNullOrEmpty(flatpak) ? Path.Combine(runtime, "dead-mans-switch") : Path.Combine(runtime, "app", flatpak);
            }
        }

        static string SocketPath => Path.Combine(Folder, "show.sock");

        /// <summary>
        /// Claims the lock. After an update the old copy may still be exiting, so
        /// the new one waits for it to let go.
        /// </summary>
        public static bool TryClaim(bool waitForPrevious)
        {
            Directory.CreateDirectory(Folder);
            var until = DateTime.Now.AddSeconds(waitForPrevious ? 15 : 0);
            while (true)
            {
                try
                {
                    lockFile = new FileStream(Path.Combine(Folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    if (DateTime.Now >= until) return false;
                    Thread.Sleep(250);
                }
            }
        }

        /// <summary>Asks the running copy to show its window.</summary>
        public static void SignalExisting()
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                socket.Send(new byte[] { 1 });
            }
            catch (Exception) { }   // it may be closing; nothing else to do
        }

        public static void ListenForShow(Action show)
        {
            try
            {
                if (File.Exists(SocketPath)) File.Delete(SocketPath);   // left by a copy that crashed
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                listener.Listen(4);
            }
            catch (Exception)
            {
                return;   // a second start just won't bring this window up
            }
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var client = listener.Accept();
                        show();
                    }
                    catch (Exception) { return; }
                }
            }) { IsBackground = true, Name = "single-instance" }.Start();
        }
    }
}
