using System;
using System.Runtime.InteropServices;

namespace DeadMansSwitch
{
    static class NativeMethods
    {
        const byte VK_MENU = 0x12;   // Alt
        const uint KEYEVENTF_KEYUP = 0x2;

        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_KEYMENU = 0xF100;   // keyboard menu mode, entered on a lone Alt tap
        public const int WM_HOTKEY = 0x0312;
        const uint MOD_NOREPEAT = 0x4000;
        const uint VK_SPACE = 0x20;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Claims the Space key system-wide: while registered, Space presses in
        /// any app arrive at hWnd as WM_HOTKEY instead. False if another app
        /// already holds it.
        /// </summary>
        public static bool ClaimSpace(IntPtr hWnd, int id) => RegisterHotKey(hWnd, id, MOD_NOREPEAT, VK_SPACE);

        public static void ReleaseSpace(IntPtr hWnd, int id) => UnregisterHotKey(hWnd, id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>
        /// Makes hWnd the foreground window even though another app has it.
        /// Windows normally refuses that, but allows it while this thread shares
        /// input with the foreground window's thread. As a last resort it taps
        /// Alt, which also unlocks the foreground; that is only done while
        /// another window is in front, because an Alt tap that reaches our own
        /// window puts it into menu mode and swallows the next key.
        /// </summary>
        public static void ForceForeground(IntPtr hWnd)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == hWnd) return;

            uint theirs = GetWindowThreadProcessId(foreground, IntPtr.Zero), ours = GetCurrentThreadId();
            bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ours, theirs, false);
            }

            if (GetForegroundWindow() != hWnd)
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(hWnd);
            }
        }
    }
}
