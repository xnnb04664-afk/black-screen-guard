using System;
using System.Threading;
using System.Windows.Forms;

namespace BlackScreenGuard
{
    // No setup form or recurring polling while locked. Ready/exit notifications wake the UI.
    internal sealed class QuickLockContext : ApplicationContext
    {
        private readonly EventSignal signal;
        private readonly System.Windows.Forms.Timer deadline;
        private readonly RegisteredWaitHandle readyWait;
        private readonly GuardSession session;
        private bool activated, closing, disposed;
        private Exception failure;
        internal int Result { get; private set; }
#if TEST_BUILD
        internal bool Activated { get { return activated; } }
        internal bool Completed { get { return closing; } }
        internal bool IdleTimerEnabled { get { return deadline.Enabled; } }
#endif
        internal QuickLockContext(string credentialPath, bool awake, string testMode, string testDirectory)
            : this(credentialPath, awake, true, testMode, testDirectory) { }
        internal QuickLockContext(string credentialPath, bool awake, bool displayOff, string testMode, string testDirectory)
        {
            signal = new EventSignal(Poll);
            deadline = new System.Windows.Forms.Timer { Interval = 12000 };
            deadline.Tick += delegate
            {
                deadline.Stop();
                if (!activated && failure == null) failure = new TimeoutException("黑屏窗口启动超时。");
                Poll();
            };
            try
            {
                session = new GuardSession(credentialPath, awake, displayOff, testMode, testDirectory);
                readyWait = ThreadPool.RegisterWaitForSingleObject(session.ReadySignal, delegate { signal.Signal(); }, null, Timeout.Infinite, true);
                session.Child.Exited += ChildExited;
                session.Child.EnableRaisingEvents = true;
                deadline.Start();
                signal.Signal();
            }
            catch { Dispose(); throw; }
        }
        private void ChildExited(object sender, EventArgs e) { signal.Signal(); }
        private void Poll()
        {
            if (closing || disposed) return;
            try
            {
                if (failure != null || session.Exited)
                {
                    if (!session.TryRestore()) { deadline.Interval = 500; deadline.Start(); return; }
                    Result = failure != null ? 1 : session.Child.ExitCode;
                    closing = true; deadline.Stop();
                    if (failure != null) MessageBox.Show("无法开始黑屏：" + failure.Message, "黑屏守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ExitThread();
                }
                else if (!activated && session.Ready)
                {
                    session.Activate(); activated = true; deadline.Stop();
                }
            }
            catch (Exception ex) { failure = ex; deadline.Interval = 500; deadline.Start(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                if (readyWait != null) readyWait.Unregister(null);
                if (session != null && session.Child != null) session.Child.Exited -= ChildExited;
                deadline.Dispose(); signal.Dispose();
                if (session != null) session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
