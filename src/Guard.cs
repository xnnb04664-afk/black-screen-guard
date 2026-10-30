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
            // Allow Windows monitor-power commands, while keeping screen savers off this desktop.
            if (m.Msg == Native.WmSysCommand && (m.WParam.ToInt64() & 0xFFF0) == 0xF140) return;
#if TEST_BUILD
            if (m.Msg == Native.WmSysCommand && (m.WParam.ToInt64() & 0xFFF0) == Native.MonitorPower && owner.CaptureTestPowerCommand(m.LParam.ToInt64())) return;
#endif
            base.WndProc(ref m);
        }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        private readonly List<BlackForm> forms = new List<BlackForm>();
        private readonly Credentials credentials;
        private readonly string original;
        private readonly bool displayOff;
#if TEST_BUILD
        private readonly string testMode, testDirectory;
#endif
        private readonly Process parent;
        private readonly EventWaitHandle ready, active, stop;
        private readonly KeyboardPolicy policy = new KeyboardPolicy();
        private readonly Native.HookProc callback;
        private readonly Native.HookProc mouseCallback;
        private readonly Native.WinEventProc desktopCallback;
        private readonly EventSignal signal;
        private readonly RegisteredWaitHandle activeWait, stopWait;
        private readonly uint ownProcessId = (uint)Process.GetCurrentProcess().Id;
        private IntPtr hook, mouseHook, desktopHook;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Panel prompt;
        private readonly TextBox password;
        private readonly Label error;
        private readonly Button unlock;
        private readonly Stopwatch inactivity = Stopwatch.StartNew(), retryClock = new Stopwatch();
        private bool cursorHidden, checking, closing, wasActive, desktopWasVisible, disposed;
        private bool displaySleepPending, wakePromptPending;
        private int displayState = -1;
        private int attempts, retryDelay, displaysChanged, revealPending, desktopChanged, pendingExit = -1;
#if TEST_BUILD
        private bool tested;
        private int blockedKeys, restoreRetries;
        private bool promptCaptured, keyboardChecked;
        private Stopwatch testClock;
        private Process focusTarget;
        private bool focusInjected, focusChecked;
        private bool timeoutPromptShown;
        private int wakeTestStage;
        private bool physicalWakeRequested;
        private readonly List<int> testDisplayStates = new List<int>();
        private readonly System.Windows.Forms.Timer testTimer;
#endif
        internal bool PromptVisible { get { return prompt.Visible; } }

        internal GuardContext(string original, string token, int parentId, string credentialPath, bool awake, bool displayOff, string testMode, string testDirectory)
        {
            this.original = original;
            this.displayOff = displayOff;
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
            signal = new EventSignal(delegate { Tick(null, EventArgs.Empty); }, displayOff ? (Action<int>)DisplayStateChanged : null);
            timer = new System.Windows.Forms.Timer();
            timer.Tick += Tick;
            desktopCallback = delegate { Interlocked.Exchange(ref desktopChanged, 1); signal.Signal(); };

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
            if (Native.SetThreadExecutionState(Native.ExecutionFlags(awake, displayOff)) == 0)
                throw new InvalidOperationException("无法启用防自动睡眠，已取消黑屏。");
            HideCursor();
            SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            // Event-driven in steady state. The timer only runs for prompt expiry or recovery.
            desktopHook = Native.SetWinEventHook(0x20, 0x20, IntPtr.Zero, desktopCallback, 0, 0, 0);
            if (desktopHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            activeWait = ThreadPool.RegisterWaitForSingleObject(active, delegate { signal.Signal(); }, null, Timeout.Infinite, true);
            stopWait = ThreadPool.RegisterWaitForSingleObject(stop, delegate { signal.Signal(); }, null, Timeout.Infinite, true);
            parent.Exited += ParentExited;
            parent.EnableRaisingEvents = true;
#if TEST_BUILD
            testTimer = new System.Windows.Forms.Timer { Interval = 150 };
            testTimer.Tick += Tick;
            if (!String.IsNullOrEmpty(testMode)) testTimer.Start();
#endif
            ready.Set();
            signal.Signal();
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
            // SystemEvents may run on another thread. The stable message window owns UI work.
            Interlocked.Exchange(ref displaysChanged, 1);
            signal.Signal();
        }

        private void ParentExited(object sender, EventArgs e) { signal.Signal(); }
        private void DisplayStateChanged(int state)
        {
            // Ignore the initial "on" notification. Only a real off-to-on transition reveals the UI.
            if (state == 0) { displaySleepPending = true; wakePromptPending = false; }
            else if (displayState == 0) wakePromptPending = true;
            displayState = state;
#if TEST_BUILD
            testDisplayStates.Add(state);
#endif
            if (signal != null) signal.Signal();
        }
        private void Schedule(int milliseconds)
        {
            timer.Stop(); timer.Interval = Math.Max(1, milliseconds); timer.Start();
        }
        internal void Touch()
        {
            inactivity.Restart();
            if (PromptVisible && pendingExit < 0) Schedule(12000);
        }
        internal void Reveal(BlackForm form)
        {
            if (closing) return;
            Touch();
            prompt.Parent = form;
            prompt.Location = new Point(Math.Max(0, (form.ClientSize.Width - prompt.Width) / 2), Math.Max(0, (form.ClientSize.Height - prompt.Height) / 2));
            prompt.Visible = true;
            Schedule(12000);
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            form.Activate(); password.Focus();
        }
        private void HideCursor() { if (!cursorHidden) { Cursor.Hide(); cursorHidden = true; } }
        internal void HidePrompt()
        {
            if (checking) return;
            ClearPrompt();
            if (wasActive) RequestDisplayOff();
        }
        private void ClearPrompt()
        {
            password.Clear(); error.Text = ""; prompt.Visible = false; HideCursor();
            if (pendingExit < 0) timer.Stop();
        }
        private void RequestDisplayOff()
        {
            if (displayOff && !closing && pendingExit < 0 && !Native.PostMessage(forms[0].Handle, Native.WmSysCommand, new IntPtr(Native.MonitorPower), new IntPtr(2)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
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
                    // vkCode is the first integer in KBDLLHOOKSTRUCT; avoid boxing per key event.
                    int key = Marshal.ReadInt32(data);
                    if (policy.Block(key, down))
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
                        if (down) { Interlocked.Exchange(ref revealPending, 1); signal.Signal(); }
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
                var point = new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
                uint processId;
                Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out processId);
                if (processId != ownProcessId)
                {
                    if (message.ToInt32() == 0x201) { Interlocked.Exchange(ref revealPending, 1); signal.Signal(); }
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
            timer.Stop();
            if (pendingExit >= 0) { Finish(pendingExit); return; }
            if (stop.WaitOne(0) || parent.HasExited) { Finish(2); return; }
            if (displaySleepPending)
            {
                displaySleepPending = false;
                if (!checking) ClearPrompt();
            }
            if (Interlocked.Exchange(ref displaysChanged, 0) != 0) { HidePrompt(); BuildScreens(); if (wasActive) RequestDisplayOff(); }
            if (active.WaitOne(0) && !wasActive)
            {
                wasActive = true; policy.Reset();
                forms[0].Activate(); forms[0].Focus();
                RequestDisplayOff();
#if TEST_BUILD
                testClock = Stopwatch.StartNew();
#endif
            }
            if (wasActive)
            {
                // Reset even if a quick leave/return was coalesced into one notification.
                if (Interlocked.Exchange(ref desktopChanged, 0) != 0) policy.Reset();
                bool visible = false;
                try { visible = Native.InputDesktopName() == Native.DesktopName(); }
                catch (System.ComponentModel.Win32Exception) { /* Windows owns the secure desktop. */ }
                if (visible != desktopWasVisible)
                {
                    // Key-up events can be consumed by the Windows security desktop; never strand modifiers.
                    policy.Reset(); desktopWasVisible = visible;
                }
            }
            if (desktopWasVisible && (Interlocked.Exchange(ref revealPending, 0) != 0 || wakePromptPending))
            {
                wakePromptPending = false;
                Reveal(forms[0]);
            }
            if (PromptVisible && !checking && inactivity.Elapsed.TotalSeconds > 12) HidePrompt();
            if (PromptVisible && pendingExit < 0) Schedule(Math.Max(50, 12000 - (int)inactivity.ElapsedMilliseconds));
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
                    if (PromptVisible && testMode != "display-wake-physical") throw new InvalidOperationException("进入黑屏时密码框应保持隐藏。");
                    File.WriteAllText(Path.Combine(testDirectory, "guard-ready.txt"), "desktop=" + Native.DesktopName() + "\r\nscreens=" + forms.Count + "\r\nkeyboardHook=" + (hook != IntPtr.Zero) + "\r\nmouseHook=" + (mouseHook != IntPtr.Zero) + "\r\nprompt=" + PromptVisible + "\r\nidleTimer=" + timer.Enabled);
                    if (testMode != "display-wake-physical")
                    {
                        InjectCombination(new byte[] { 0x5B, 0x44 });
                        InjectCombination(new byte[] { 0x12, 9 });
                        InjectCombination(new byte[] { 0x12, 0x73 });
                        InjectCombination(new byte[] { 0x11, 0x10, 27 });
                        InjectCombination(new byte[] { 0x2C });
                        InjectCombination(new byte[] { 0xB3 });
                    }
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
                if (testMode == "prompt-timeout" && tested)
                {
                    if (!timeoutPromptShown) { timeoutPromptShown = true; Reveal(forms[0]); }
                    if (inactivity.ElapsedMilliseconds > 12500)
                    {
                        if (PromptVisible || timer.Enabled) throw new InvalidOperationException("密码框闲置后未隐藏或计时器仍在运行。");
                        File.WriteAllText(Path.Combine(testDirectory, "prompt-timeout.txt"), "prompt hidden after inactivity; production timer stopped");
                        Finish(0); return;
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
                if (tested && !keyboardChecked && testMode != "display-wake-physical" && testClock.ElapsedMilliseconds > 1200)
                {
                    keyboardChecked = true;
                    if (blockedKeys < 16) throw new InvalidOperationException("快捷键实际拦截验证未通过。");
                    File.WriteAllText(Path.Combine(testDirectory, "keyboard-blocked.txt"), "blocked injected keyboard events=" + blockedKeys);
                }
                if (tested && testClock.ElapsedMilliseconds > 1500)
                {
                    if (testMode == "display-wake") RunWakeTests();
                    if (testMode == "display-wake-physical")
                    {
                        if (!physicalWakeRequested && testDisplayStates.Contains(0))
                        {
                            physicalWakeRequested = true;
                            Native.SetThreadExecutionState(Native.DisplayRequired);
                        }
                        if (physicalWakeRequested && displayState == 1 && PromptVisible && password.Focused && !checking)
                        {
                            using (var bitmap = new Bitmap(forms[0].Width, forms[0].Height))
                            {
                                forms[0].DrawToBitmap(bitmap, forms[0].ClientRectangle);
                                bitmap.Save(Path.Combine(testDirectory, "wake-password-prompt.png"));
                            }
                            File.WriteAllText(Path.Combine(testDirectory, "physical-display-wake.txt"),
                                "states=" + String.Join(",", testDisplayStates) + "\r\nautomaticPrompt=True\r\npasswordFocused=True");
                            password.Text = "TestOnly-42!";
                            TryUnlock().GetAwaiter();
                        }
                    }
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
                else if ((testMode == "auto" || testMode == "child-crash" || testMode == "restore-retry" || testMode == "foreign-focus" || testMode == "monitor-off") && testClock.ElapsedMilliseconds > 3500)
                {
                    if (testMode == "child-crash") Environment.Exit(37);
                    else
                    {
                        if (testMode == "restore-retry") Native.RestoreFailuresRemaining = 2;
                        Finish(0);
                    }
                }
                else if (testClock.ElapsedMilliseconds > (testMode == "prompt-timeout" ? 24000 : 12000)) Finish(3);
            }
#endif
        }

#if TEST_BUILD
        internal bool CaptureTestPowerCommand(long state)
        {
            if (testMode != "monitor-off" && testMode != "display-wake") return false;
            if (state != 2) throw new InvalidOperationException("显示器电源命令参数不正确。");
            Directory.CreateDirectory(testDirectory);
            File.WriteAllText(Path.Combine(testDirectory, "monitor-off-command.txt"), "SC_MONITORPOWER=2 received by the black window; intercepted before hardware power change");
            return true;
        }
        private void RunWakeTests()
        {
            if (wakeTestStage == 0)
            {
                SelfTests.SendDisplayState(signal.Handle, 1);
                if (PromptVisible) throw new InvalidOperationException("初始亮屏通知不应提前显示密码框。");
                SelfTests.SendDisplayState(signal.Handle, 0);
                SelfTests.SendDisplayState(signal.Handle, 1);
                wakeTestStage = 1;
            }
            else if (wakeTestStage == 1)
            {
                if (!PromptVisible || !password.Focused) throw new InvalidOperationException("亮屏后密码框未自动显示并获得焦点。");
                password.Text = "Partial-42";
                SelfTests.SendDisplayState(signal.Handle, 1);
                wakeTestStage = 2;
            }
            else if (wakeTestStage == 2)
            {
                if (password.Text != "Partial-42") throw new InvalidOperationException("重复亮屏通知清除了正在输入的密码。");
                HidePrompt();
                SelfTests.SendDisplayState(signal.Handle, 0);
                wakeTestStage = 3;
            }
            else if (wakeTestStage == 3)
            {
                if (PromptVisible || timer.Enabled || password.Text.Length != 0)
                    throw new InvalidOperationException("息屏后密码框未清空，或仍在运行空闲计时器。");
                // Keep a wake pending while another desktop is visible, then reveal on return.
                if (!Native.TryRestore(original)) throw new InvalidOperationException("测试桌面切换失败。");
                SelfTests.SendDisplayState(signal.Handle, 1);
                wakeTestStage = 4;
            }
            else if (wakeTestStage == 4)
            {
                if (PromptVisible || !wakePromptPending) throw new InvalidOperationException("其他桌面可见时未保留亮屏通知。");
                Native.Restore(Native.DesktopName());
                wakeTestStage = 5;
            }
            else if (wakeTestStage == 5)
            {
                if (!PromptVisible || !password.Focused || password.Text.Length != 0)
                    throw new InvalidOperationException("再次亮屏或返回隔离桌面后密码框未恢复。");
                File.WriteAllText(Path.Combine(testDirectory, "display-wake.txt"),
                    "automatic prompt and focus; duplicate-on preserves typing; off clears input and stops timer; wake survives desktop switch");
                wakeTestStage = 6;
                password.Text = "TestOnly-42!";
                TryUnlock().GetAwaiter();
            }
        }
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
                Schedule(500);
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
            if (desktopHook != IntPtr.Zero) { Native.UnhookWinEvent(desktopHook); desktopHook = IntPtr.Zero; }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            Native.SetThreadExecutionState(Native.Continuous);
            if (displayOff) Native.SetThreadExecutionState(Native.DisplayRequired);
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
                parent.Exited -= ParentExited;
                if (activeWait != null) activeWait.Unregister(null);
                if (stopWait != null) stopWait.Unregister(null);
                if (desktopHook != IntPtr.Zero) Native.UnhookWinEvent(desktopHook);
                signal.Dispose();
                if (timer != null) timer.Dispose();
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                Native.SetThreadExecutionState(Native.Continuous);
                if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
                foreach (var form in forms) form.Dispose();
#if TEST_BUILD
                if (testTimer != null) testTimer.Dispose();
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
