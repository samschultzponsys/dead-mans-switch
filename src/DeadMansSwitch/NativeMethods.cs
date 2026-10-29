using System;
using System.Runtime.InteropServices;

namespace DeadMansSwitch
{
    static class NativeMethods
    {
        public const byte VK_MENU = 0x12;   // Alt
        public const uint KEYEVENTF_KEYUP = 0x2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
