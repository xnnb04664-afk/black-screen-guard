using System;
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
        private readonly object gate = new object();
        private int pending;
        private bool disposed;
        internal EventSignal(Action action)
        {
            this.action = action;
            CreateHandle(new CreateParams { Caption = "BlackScreenGuard events", Parent = new IntPtr(-3) });
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
            lock (gate) { if (disposed) return; disposed = true; DestroyHandle(); }
        }
    }
}
