using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BlackScreenGuard
{
    internal static class Native
    {
        internal const uint DesktopAccess = 0x01FF;
        internal const uint Continuous = 0x80000000;
        internal const uint SystemRequired = 1;
        internal const uint DisplayRequired = 2;
        internal const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardData
        {
            internal uint Key, Scan, Flags, Time;
            internal UIntPtr Extra;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size;
            internal string Reserved, Desktop, Title;
            internal int X, Y, Width, Height, XChars, YChars, Fill, Flags;
            internal short Show, ReservedSize;
            internal IntPtr ReservedPtr, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInfo
        {
            internal IntPtr Process, Thread;
            internal int ProcessId, ThreadId;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SwitchDesktop(IntPtr desktop);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(System.Drawing.Point point);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]
        internal static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string name);

        internal static string DesktopName()
        {
            return ObjectName(GetThreadDesktop(GetCurrentThreadId()));
        }
        internal static string InputDesktopName()
        {
            IntPtr desktop = OpenInputDesktop(0, false, DesktopAccess);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return ObjectName(desktop); }
            finally { CloseDesktop(desktop); }
        }
        private static string ObjectName(IntPtr handle)
        {
            var buffer = new StringBuilder(256);
            uint needed;
            if (!GetUserObjectInformation(handle, 2, buffer, 512, out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return buffer.ToString();
        }

        internal static void Restore(string name)
        {
#if TEST_BUILD
            if (RestoreFailuresRemaining > 0)
            {
                RestoreFailuresRemaining--;
                throw new Win32Exception(5, "Simulated transient desktop-switch failure.");
            }
#endif
            IntPtr desktop = OpenDesktop(name, 0, false, DesktopAccess);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!SwitchDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseDesktop(desktop); }
        }
        internal static bool TryRestore(string name)
        {
            try { Restore(name); return true; }
            catch (Win32Exception) { return false; }
        }
#if TEST_BUILD
        internal static int RestoreFailuresRemaining;
#endif
    }
}
