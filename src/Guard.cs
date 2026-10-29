using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BlackScreenGuard
{
    internal sealed class KeyboardPolicy
    {
        private readonly HashSet<int> modifiers = new HashSet<int>();
        internal bool Block(int key, bool down)
        {
            if (key == 0x11 || key == 0xA2 || key == 0xA3 || key == 0x12 || key == 0xA4 || key == 0xA5 || key == 0x5B || key == 0x5C)
            {
                if (down) modifiers.Add(key); else modifiers.Remove(key);
                return true;
            }
            if (modifiers.Count > 0) return true;
            // Only text entry and movement within the password UI are passed through.
            return !((key >= 0x30 && key <= 0x39) || (key >= 0x41 && key <= 0x5A) ||
                (key >= 0x60 && key <= 0x6F) || (key >= 0xBA && key <= 0xE2) ||
                key == 8 || key == 9 || key == 13 || key == 16 || key == 20 || key == 27 || key == 32 ||
                (key >= 0x23 && key <= 0x28) || key == 46 || key == 144 || key == 0xA0 || key == 0xA1);
        }
        internal void Reset() { modifiers.Clear(); }
    }

    internal sealed class BlackForm : Form
    {
        internal bool AllowClose;
        private readonly GuardContext owner;
        internal BlackForm(GuardContext owner, Rectangle bounds)
        {
            this.owner = owner;
            Text = "黑屏守护 · 输入密码解开";
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            MouseDown += delegate { owner.Reveal(this); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                owner.Touch();
                if (e.KeyCode == Keys.Escape) { owner.HidePrompt(); e.SuppressKeyPress = true; }
                else if (!owner.PromptVisible) { owner.Reveal(this); e.SuppressKeyPress = true; }
            };
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }
        protected override void WndProc(ref Message m)
        {
            // Suppress screen savers and monitor-power requests on this desktop only.
            if (m.Msg == 0x112 && ((m.WParam.ToInt64() & 0xFFF0) == 0xF140 || (m.WParam.ToInt64() & 0xFFF0) == 0xF170)) return;
            base.WndProc(ref m);
        }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        private readonly List<BlackForm> forms = new List<BlackForm>();
        private readonly Credentials credentials;
        private readonly string original;
#if TEST_BUILD
        private readonly string testMode, testDirectory;
#endif
        private readonly Process parent;
        private readonly EventWaitHandle ready, active, stop;
        private readonly KeyboardPolicy policy = new KeyboardPolicy();
        private readonly Native.HookProc callback;
        private readonly Native.HookProc mouseCallback;
        private readonly uint ownProcessId = (uint)Process.GetCurrentProcess().Id;
        private IntPtr hook, mouseHook;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Panel prompt;
        private readonly TextBox password;
        private readonly Label error;
        private readonly Button unlock;
        private readonly Stopwatch inactivity = Stopwatch.StartNew(), retryClock = new Stopwatch();
        private bool cursorHidden, checking, closing, wasActive, desktopWasVisible, disposed;
        private int attempts, retryDelay, displaysChanged, revealPending, pendingExit = -1;
#if TEST_BUILD
        private bool tested;
        private int blockedKeys, restoreRetries;
        private bool promptCaptured, keyboardChecked;
        private Stopwatch testClock;
        private Process focusTarget;
        private bool focusInjected, focusChecked;
#endif
        internal bool PromptVisible { get { return prompt.Visible; } }

        internal GuardContext(string original, string token, int parentId, string credentialPath, bool awake, string testMode, string testDirectory)
        {
            this.original = original;
#if TEST_BUILD
            this.testMode = testMode; this.testDirectory = testDirectory;
#endif
            credentials = Credentials.Load(credentialPath);
            if (credentials == null) throw new InvalidOperationException("没有设置密码。");
            parent = Process.GetProcessById(parentId);
            if (parent.Handle == IntPtr.Zero) throw new InvalidOperationException("无法监测启动进程。");
            ready = EventWaitHandle.OpenExisting("Local\\BSG_" + token + "_ready");
            active = EventWaitHandle.OpenExisting("Local\\BSG_" + token + "_active");
            stop = EventWaitHandle.OpenExisting("Local\\BSG_" + token + "_stop");
            callback = Keyboard;
            mouseCallback = Mouse;

            prompt = new Panel { BackColor = Color.FromArgb(18, 20, 24), Size = new Size(400, 236), Visible = false };
            var title = new Label { Text = "输入密码，回到桌面", ForeColor = Color.White, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), Bounds = new Rectangle(26, 22, 350, 34) };
            password = new TextBox { UseSystemPasswordChar = true, MaxLength = 128, ShortcutsEnabled = false,
                Font = new Font("Microsoft YaHei UI", 15), Bounds = new Rectangle(28, 72, 344, 36), ImeMode = ImeMode.Disable };
            error = new Label { ForeColor = Color.FromArgb(255, 157, 157), Bounds = new Rectangle(27, 112, 348, 32), AutoSize = false };
            unlock = BlackScreenGuard.MainForm.MakeButton("解开黑屏", true);
            unlock.Bounds = new Rectangle(28, 157, 218, 44);
            var back = BlackScreenGuard.MainForm.MakeButton("继续黑屏", false);
            back.Bounds = new Rectangle(256, 157, 116, 44);
            back.BackColor = Color.FromArgb(43, 47, 54); back.ForeColor = Color.White;
            unlock.Click += async delegate { await TryUnlock(); };
            back.Click += delegate { HidePrompt(); };
            password.KeyDown += async delegate(object s, KeyEventArgs e)
            {
                Touch();
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await TryUnlock(); }
            };
            password.TextChanged += delegate { Touch(); };
            prompt.Controls.AddRange(new Control[] { title, password, error, unlock, back });
            foreach (Control control in prompt.Controls) control.MouseDown += delegate { Touch(); };
            BuildScreens();
            float scale = forms[0].DeviceDpi / 96f;
            prompt.Scale(new SizeF(scale, scale));
            hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (awake && Native.SetThreadExecutionState(Native.Continuous | Native.SystemRequired | Native.DisplayRequired) == 0)
                throw new InvalidOperationException("无法启用防自动睡眠，已取消黑屏。");
            HideCursor();
            SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += Tick;
            timer.Start();
            ready.Set();
        }

        private void BuildScreens()
        {
            prompt.Parent = null;
            foreach (var form in forms) { form.AllowClose = true; form.Close(); form.Dispose(); }
            forms.Clear();
            foreach (var screen in Screen.AllScreens)
            {
                var form = new BlackForm(this, screen.Bounds);
                forms.Add(form); form.Show();
                // Reapply physical monitor bounds after WinForms DPI initialization.
                form.Bounds = screen.Bounds;
            }
            if (forms.Count == 0) throw new InvalidOperationException("未找到可用显示器。");
            forms[0].Activate();
        }

        private void DisplaysChanged(object sender, EventArgs e)
        {
            // SystemEvents may run on another thread. The timer owns all window rebuilds.
            Interlocked.Exchange(ref displaysChanged, 1);
        }

        internal void Touch() { inactivity.Restart(); }
        internal void Reveal(BlackForm form)
        {
            if (closing) return;
            Touch();
            prompt.Parent = form;
            prompt.Location = new Point(Math.Max(0, (form.ClientSize.Width - prompt.Width) / 2), Math.Max(0, (form.ClientSize.Height - prompt.Height) / 2));
            prompt.Visible = true;
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            form.Activate(); password.Focus();
        }
        private void HideCursor() { if (!cursorHidden) { Cursor.Hide(); cursorHidden = true; } }
        internal void HidePrompt()
        {
            if (checking) return;
            password.Clear(); error.Text = ""; prompt.Visible = false; HideCursor();
        }
        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !closing)
            {
                int kind = message.ToInt32();
                bool down = kind == Native.WmKeyDown || kind == Native.WmSysKeyDown;
                bool up = kind == Native.WmKeyUp || kind == Native.WmSysKeyUp;
                if (down || up)
                {
                    var input = (Native.KeyboardData)Marshal.PtrToStructure(data, typeof(Native.KeyboardData));
                    if (policy.Block((int)input.Key, down))
                    {
#if TEST_BUILD
                        blockedKeys++;
#endif
                        return new IntPtr(1);
                    }
                    uint foregroundProcess;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foregroundProcess);
                    if (foregroundProcess != ownProcessId)
                    {
                        if (down) Interlocked.Exchange(ref revealPending, 1);
                        return new IntPtr(1);
                    }
                    // No password hashing, UI rendering, I/O or waits inside this callback.
                }
            }
            return Native.CallNextHookEx(hook, code, message, data);
        }
        private IntPtr Mouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !closing)
            {
                // MSLLHOOKSTRUCT starts with POINT. Pass mouse input only to this process's windows.
                var point = (Point)Marshal.PtrToStructure(data, typeof(Point));
                uint processId;
                Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out processId);
                if (processId != ownProcessId)
                {
                    if (message.ToInt32() == 0x201) Interlocked.Exchange(ref revealPending, 1);
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(mouseHook, code, message, data);
        }
        private async Task TryUnlock()
        {
            if (checking) return;
            if (retryClock.IsRunning && retryClock.ElapsedMilliseconds < retryDelay) { error.Text = "请稍等几秒再试。"; return; }
            checking = true; unlock.Enabled = false; password.Enabled = false;
            string candidate = password.Text;
            password.Clear();
            bool correct;
            try { correct = await Task.Run(delegate { return credentials.Verify(candidate); }); }
            finally
            {
                candidate = null; checking = false;
                if (!closing) { unlock.Enabled = true; password.Enabled = true; }
            }
            if (closing) return;
            if (correct) { Finish(0); return; }
            attempts++;
            int delay = Math.Min(15, attempts);
            retryDelay = delay * 1000; retryClock.Restart();
            error.Text = "密码不对，请 " + delay + " 秒后重试。";
            Touch(); password.Focus();
        }

        private void Tick(object sender, EventArgs e)
        {
            if (closing) return;
            if (pendingExit >= 0) { Finish(pendingExit); return; }
            if (stop.WaitOne(0) || parent.HasExited) { Finish(2); return; }
            if (Interlocked.Exchange(ref displaysChanged, 0) != 0) { HidePrompt(); BuildScreens(); }
            if (active.WaitOne(0) && !wasActive)
            {
                wasActive = true; policy.Reset();
                forms[0].Activate(); forms[0].Focus();
#if TEST_BUILD
                testClock = Stopwatch.StartNew();
#endif
            }
            if (wasActive)
            {
                bool visible = false;
                try { visible = Native.InputDesktopName() == Native.DesktopName(); }
                catch (System.ComponentModel.Win32Exception) { /* Windows owns the secure desktop. */ }
                if (visible != desktopWasVisible)
                {
                    // Key-up events can be consumed by the Windows security desktop; never strand modifiers.
                    policy.Reset(); desktopWasVisible = visible;
                }
            }
            if (desktopWasVisible && Interlocked.Exchange(ref revealPending, 0) != 0) Reveal(forms[0]);
            if (PromptVisible && !checking && inactivity.Elapsed.TotalSeconds > 12) HidePrompt();
#if TEST_BUILD
            if (!String.IsNullOrEmpty(testMode) && wasActive)
            {
                if (!tested && testClock.ElapsedMilliseconds > 600)
                {
                    tested = true;
                    Directory.CreateDirectory(testDirectory);
                    foreach (var form in forms)
                    {
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            using (var graphics = Graphics.FromImage(bitmap))
                                graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
                            bitmap.Save(Path.Combine(testDirectory, "black-" + forms.IndexOf(form) + ".png"));
                        }
                    }
                    if (PromptVisible) throw new InvalidOperationException("进入黑屏时密码框应保持隐藏。");
                    File.WriteAllText(Path.Combine(testDirectory, "guard-ready.txt"), "desktop=" + Native.DesktopName() + "\r\nscreens=" + forms.Count + "\r\nkeyboardHook=" + (hook != IntPtr.Zero) + "\r\nmouseHook=" + (mouseHook != IntPtr.Zero) + "\r\nprompt=" + PromptVisible);
                    InjectCombination(new byte[] { 0x5B, 0x44 });
                    InjectCombination(new byte[] { 0x12, 9 });
                    InjectCombination(new byte[] { 0x12, 0x73 });
                    InjectCombination(new byte[] { 0x11, 0x10, 27 });
                    InjectCombination(new byte[] { 0x2C });
                    InjectCombination(new byte[] { 0xB3 });
                    if (testMode == "foreign-focus")
                        focusTarget = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                            "--test-focus-target \"" + testDirectory + "\"") { UseShellExecute = false });
                    if (testMode == "unlock")
                    {
                        Reveal(forms[0]);
                        password.Text = "DefinitelyWrong";
                        TryUnlock().GetAwaiter();
                    }
                }
                if (testMode == "foreign-focus" && focusTarget != null && !focusInjected && File.Exists(Path.Combine(testDirectory, "focus-target-ready.txt")))
                {
                    uint foreground;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foreground);
                    if (foreground == (uint)focusTarget.Id)
                    {
                        focusInjected = true;
                        InjectCombination(new byte[] { 0x41 });
                    }
                }
                if (testMode == "foreign-focus" && focusInjected && !focusChecked && testClock.ElapsedMilliseconds > 2500)
                {
                    focusChecked = true;
                    if (File.ReadAllText(Path.Combine(testDirectory, "focus-target-keys.txt")).Length != 0)
                        throw new InvalidOperationException("密码字符被传给其他进程。");
                    uint foreground;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foreground);
                    if (foreground != ownProcessId || !PromptVisible) throw new InvalidOperationException("密码窗口未重新获得焦点。");
                    File.WriteAllText(Path.Combine(testDirectory, "focus-input-blocked.txt"), "foreign input blocked; guard regained focus");
                    focusTarget.CloseMainWindow();
                }
                if (tested && !keyboardChecked && testClock.ElapsedMilliseconds > 1200)
                {
                    keyboardChecked = true;
                    if (blockedKeys < 16) throw new InvalidOperationException("快捷键实际拦截验证未通过。");
                    File.WriteAllText(Path.Combine(testDirectory, "keyboard-blocked.txt"), "blocked injected keyboard events=" + blockedKeys);
                }
                if (testMode == "unlock" && !promptCaptured && attempts == 1)
                {
                    promptCaptured = true;
                    using (var bitmap = new Bitmap(forms[0].Width, forms[0].Height))
                    {
                        forms[0].DrawToBitmap(bitmap, forms[0].ClientRectangle);
                        bitmap.Save(Path.Combine(testDirectory, "password-prompt.png"));
                    }
                }
                if (testMode == "unlock" && tested && !checking && attempts == 1 && testClock.ElapsedMilliseconds > 2300)
                {
                    // Exercise the real password textbox and verification path, never a production bypass.
                    File.WriteAllText(Path.Combine(testDirectory, "wrong-password-blocked.txt"), "guard still active; attempts=" + attempts);
                    password.Text = "TestOnly-42!";
                    TryUnlock().GetAwaiter();
                }
                else if ((testMode == "auto" || testMode == "child-crash" || testMode == "restore-retry" || testMode == "foreign-focus") && testClock.ElapsedMilliseconds > 3500)
                {
                    if (testMode == "child-crash") Environment.Exit(37);
                    else
                    {
                        if (testMode == "restore-retry") Native.RestoreFailuresRemaining = 2;
                        Finish(0);
                    }
                }
                else if (testClock.ElapsedMilliseconds > 12000) Finish(3);
            }
#endif
        }

#if TEST_BUILD
        private static void InjectCombination(byte[] keys)
        {
            foreach (byte key in keys) Native.keybd_event(key, 0, 0, UIntPtr.Zero);
            for (int i = keys.Length - 1; i >= 0; i--) Native.keybd_event(keys[i], 0, 2, UIntPtr.Zero);
        }
#endif

        private void Finish(int exitCode)
        {
            if (closing) return;
            // Do not close the visible desktop before restoring the original one.
            pendingExit = exitCode;
            if (!Native.TryRestore(original))
            {
#if TEST_BUILD
                restoreRetries++;
#endif
                return;
            }
#if TEST_BUILD
            if (restoreRetries > 0 && !String.IsNullOrEmpty(testDirectory))
                File.WriteAllText(Path.Combine(testDirectory, "restore-retries.txt"), restoreRetries.ToString());
#endif
            closing = true; Environment.ExitCode = exitCode;
            if (timer != null) timer.Stop();
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            Native.SetThreadExecutionState(Native.Continuous);
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            foreach (var form in forms) { form.AllowClose = true; form.Close(); }
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
                if (timer != null) timer.Dispose();
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                Native.SetThreadExecutionState(Native.Continuous);
                if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
                foreach (var form in forms) form.Dispose();
#if TEST_BUILD
                if (focusTarget != null)
                {
                    if (!focusTarget.HasExited) focusTarget.Kill();
                    focusTarget.Dispose();
                }
#endif
                prompt.Dispose(); ready.Dispose(); active.Dispose(); stop.Dispose(); parent.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
