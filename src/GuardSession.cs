using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BlackScreenGuard
{
    // The launcher remains on the original desktop and recovers it if the guard crashes.
    // The guard independently watches the launcher, so either process can recover the desktop.
    internal sealed class GuardSession : IDisposable
    {
        internal readonly string OriginalDesktop, Desktop, Token;
        internal Process Child { get; private set; }
        private IntPtr desktopHandle;
        private readonly EventWaitHandle ready, activated, stop;
        private bool switched, disposed;

        internal GuardSession(string credentialsPath, bool awake, string testMode, string testDirectory)
            : this(credentialsPath, awake, false, testMode, testDirectory) { }
        internal GuardSession(string credentialsPath, bool awake, bool displayOff, string testMode, string testDirectory)
        {
            OriginalDesktop = Native.DesktopName();
            Token = Guid.NewGuid().ToString("N");
            Desktop = "BlackScreenGuard_" + Token;
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + Token + "_ready");
            activated = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + Token + "_active");
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + Token + "_stop");
            try
            {
                desktopHandle = Native.CreateDesktop(Desktop, IntPtr.Zero, IntPtr.Zero, 0, Native.DesktopAccess, IntPtr.Zero);
                if (desktopHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string[] arguments = { exe, "--guard", OriginalDesktop, Token, Process.GetCurrentProcess().Id.ToString(),
                    credentialsPath, ((awake ? 1 : 0) | (displayOff ? 2 : 0)).ToString(), testMode ?? "", testDirectory ?? "" };
                var command = new StringBuilder();
                foreach (string argument in arguments) command.Append(Quote(argument)).Append(' ');
                var startup = new Native.StartupInfo();
                startup.Size = Marshal.SizeOf(startup);
                startup.Desktop = "WinSta0\\" + Desktop;
                Native.ProcessInfo info;
                if (!Native.CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                    Path.GetDirectoryName(exe), ref startup, out info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Child = Process.GetProcessById(info.ProcessId);
                    // Pin the process handle before exit; PID-only Process instances lose ExitCode after termination.
                    if (Child.Handle == IntPtr.Zero) throw new InvalidOperationException("无法监测黑屏进程。");
                }
                finally { Native.CloseHandle(info.Thread); Native.CloseHandle(info.Process); }
            }
            catch { Dispose(); throw; }
        }

        // Windows command-line quoting; no shell is involved.
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal bool Ready { get { return ready.WaitOne(0); } }
        internal WaitHandle ReadySignal { get { return ready; } }
        internal bool Exited { get { return Child == null || Child.HasExited; } }
        internal void Activate()
        {
            if (!Ready || Exited) throw new InvalidOperationException("黑屏窗口未准备好。");
            if (!Native.SwitchDesktop(desktopHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            switched = true;
            activated.Set();
        }
        internal void Restore()
        {
            if (!switched) return;
            Native.Restore(OriginalDesktop);
            switched = false;
        }
        internal bool TryRestore()
        {
            if (!switched) return true;
            if (!Native.TryRestore(OriginalDesktop)) return false;
            switched = false;
            return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Never terminate the remaining recovery process while Windows rejects desktop switches.
            bool restored = TryRestore();
            try { stop.Set(); }
            finally
            {
                if (Child != null)
                {
                    try { if (restored && !Child.HasExited && !Child.WaitForExit(1500)) Child.Kill(); }
                    catch (InvalidOperationException) { /* Child exited during cleanup. */ }
                    Child.Dispose();
                }
                if (desktopHandle != IntPtr.Zero) Native.CloseDesktop(desktopHandle);
                ready.Dispose(); activated.Dispose(); stop.Dispose();
            }
        }
    }
}
