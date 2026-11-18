using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace DeadMansSwitch
{
    /// <summary>The few things done differently on Windows and Linux.</summary>
    static class Platform
    {
        static readonly Lazy<bool> traySupported = new Lazy<bool>(() =>
            OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && LinuxDesktop.TraySupported()));

        /// <summary>Whether the desktop can show a tray icon (some Linux desktops can't, e.g. GNOME without an extension).</summary>
        public static bool TraySupported => traySupported.Value;

        /// <summary>Shuts down, restarts or hibernates. Returns null if that started, or why it didn't.</summary>
        public static Task<string> RunEndActionAsync(EndAction action)
        {
            if (!OperatingSystem.IsWindows()) return LinuxDesktop.RunEndActionAsync(action);
            string args = action switch
            {
                EndAction.Restart => "/r /f /t 0",
                EndAction.Hibernate => "/h",
                _ => "/s /f /t 0",
            };
            return Task.Run(() =>
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
                    if (!process.WaitForExit(10_000)) return null;   // still going: Windows is shutting down
                    return process.ExitCode == 0 ? null : $"shutdown.exe failed (code {process.ExitCode}).";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });
        }

        public static void PlayWarningSound()
        {
            if (OperatingSystem.IsWindows()) WindowsNative.Beep();
            else LinuxDesktop.PlayWarningSound();
        }

        /// <summary>
        /// Pulls the window in front of other apps. Windows needs help to get
        /// past its focus-stealing rules; on Linux the window manager decides,
        /// and the window is at least kept above others.
        /// </summary>
        public static void BringToFront(Window window)
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Topmost = false;
            window.Topmost = true;
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is IntPtr hWnd)
                WindowsNative.ForceForeground(hWnd);
            window.Activate();
        }

        // ----- the GitHub token at rest -----

        // Ties the encrypted token to this app as well as to the Windows account.
        static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("DeadMansSwitch.GitHubToken");
        const string PlainPrefix = "plain:";

        /// <summary>
        /// On Windows the token is encrypted for the user's account (DPAPI). Linux
        /// has no equivalent available to every package format, so there it's
        /// kept as is, in a settings file only the user can read.
        /// </summary>
        public static string ProtectToken(string token)
        {
            if (!OperatingSystem.IsWindows()) return PlainPrefix + token;
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), TokenEntropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Null if it can't be read here (e.g. saved on another PC or account).</summary>
        public static string UnprotectToken(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal)) return stored.Substring(PlainPrefix.Length);
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), TokenEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception) { return null; }
        }

        /// <summary>Makes a file readable only by its owner (Linux; Windows profiles are private already).</summary>
        public static void RestrictToOwner(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
