using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BlackScreenGuard
{
    // A stable message-only window, independent of monitor windows that may be rebuilt.
    // Native hooks and wait callbacks only enqueue a coalesced message; UI work stays here.
    internal sealed class EventSignal : NativeWindow, IDisposable
    {
        private const int WakeMessage = 0x8001;
        private readonly Action action;
        private readonly Action<int> displayChanged;
        private readonly object gate = new object();
        private IntPtr powerRegistration;
        private int pending;
        private bool disposed;
        internal EventSignal(Action action) : this(action, null) { }
        internal EventSignal(Action action, Action<int> displayChanged)
        {
            this.action = action;
            this.displayChanged = displayChanged;
            CreateHandle(new CreateParams { Caption = "BlackScreenGuard events", Parent = new IntPtr(-3) });
            if (displayChanged != null)
            {
                Guid setting = Native.SessionDisplayStatus;
                powerRegistration = Native.RegisterPowerSettingNotification(Handle, ref setting, 0);
                if (powerRegistration == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    DestroyHandle();
                    throw new System.ComponentModel.Win32Exception(error);
                }
            }
        }
        internal void Signal()
        {
            lock (gate)
            {
                if (disposed || Interlocked.Exchange(ref pending, 1) != 0) return;
                if (!Native.PostMessage(Handle, WakeMessage, IntPtr.Zero, IntPtr.Zero)) Interlocked.Exchange(ref pending, 0);
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (!disposed && displayChanged != null && m.Msg == Native.WmPowerBroadcast &&
                m.WParam.ToInt64() == Native.PowerSettingChange && m.LParam != IntPtr.Zero &&
                (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid)) == Native.SessionDisplayStatus &&
                Marshal.ReadInt32(m.LParam, 16) == 4)
            {
                int state = Marshal.ReadInt32(m.LParam, 20);
                if (state >= 0 && state <= 2) displayChanged(state);
                m.Result = new IntPtr(1);
                return;
            }
            if (m.Msg == WakeMessage)
            {
                Interlocked.Exchange(ref pending, 0);
                if (!disposed) action();
                return;
            }
            base.WndProc(ref m);
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (powerRegistration != IntPtr.Zero)
                {
                    Native.UnregisterPowerSettingNotification(powerRegistration);
                    powerRegistration = IntPtr.Zero;
                }
                DestroyHandle();
            }
        }
    }
}
