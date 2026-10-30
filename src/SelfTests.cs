using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BlackScreenGuard
{
    internal static class SelfTests
    {
        private static readonly List<string> lines = new List<string>();
        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            lines.Add("PASS: " + label);
        }
        internal static int VerifyRelease(string executable, string directory)
        {
            Directory.CreateDirectory(directory);
            executable = Path.GetFullPath(executable);
            try
            {
                Assembly release = Assembly.LoadFile(executable);
                Check(release.GetName().Version.ToString() == "1.0.3.0", "release version is 1.0.3.0");
                Check(release.GetType("BlackScreenGuard.SelfTests") == null, "release excludes self-test implementation");
                var native = release.GetType("BlackScreenGuard.Native", true);
                Check(native.GetField("RestoreFailuresRemaining", BindingFlags.Static | BindingFlags.NonPublic) == null, "release excludes fault injection");
                var guard = release.GetType("BlackScreenGuard.GuardContext", true);
                Check(guard.GetMethod("InjectCombination", BindingFlags.Static | BindingFlags.NonPublic) == null, "release excludes test key injection");
                Check(guard.GetMethod("RunWakeTests", BindingFlags.Instance | BindingFlags.NonPublic) == null, "release excludes display-wake test injection");
                Check(guard.GetField("testMode", BindingFlags.Instance | BindingFlags.NonPublic) == null, "release excludes automatic test unlock state");
                foreach (string argument in new string[] { "--self-test unused", "--measure unused unused", "--check-display-power unused unused", "--test-parent-crash unused unused", "--test-focus-target unused", "--preview unused", "--guard Default unused 0 unused 0 auto unused" })
                    using (var process = Process.Start(new ProcessStartInfo(executable, argument) { UseShellExecute = false, CreateNoWindow = true }))
                        Check(process.WaitForExit(5000) && process.ExitCode == 1, "release rejects " + argument.Split(' ')[0]);
                var formType = release.GetType("BlackScreenGuard.MainForm", true);
                string unusedCredentials = Path.Combine(directory, "never-saved.dat");
                using (var form = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { unusedCredentials, null }, null))
                {
                    form.Show(); form.Update();
                    Check(form.Icon != null && form.Icon.Width > 0, "release setup window has an application icon");
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(Path.Combine(directory, "release-window.png"));
                    }
                    form.Close();
                }
                Check(!File.Exists(unusedCredentials), "release preview does not save credentials");
                File.WriteAllLines(Path.Combine(directory, "release-tests.txt"), lines.ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString()); File.WriteAllLines(Path.Combine(directory, "release-tests.txt"), lines.ToArray()); return 1;
            }
        }
        internal static int Run(string directory)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "test-password-" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                Check(Credentials.Validate("123", "123") != null, "short password rejected");
                Check(Credentials.Validate("      ", "      ") != null, "blank password rejected");
                Check(Credentials.Validate("abcdef", "abcdeg") != null, "confirmation mismatch rejected");
                Check(Credentials.Validate("中文密码测试", "中文密码测试") != null, "unsupported IME password rejected");
                Check(Credentials.Validate("TestOnly-42!", "TestOnly-42!") == null, "valid password accepted");
                Credentials record = Credentials.Create("TestOnly-42!");
                Check(record.Verify("TestOnly-42!") && !record.Verify("wrong"), "password verification");
                record.Save(temporary);
                Check(Credentials.Load(temporary).Verify("TestOnly-42!"), "DPAPI protected password round trip");
                record.Save(temporary);
                Check(Credentials.Load(temporary).Verify("TestOnly-42!"), "atomic password replacement");
                byte[] damaged = File.ReadAllBytes(temporary); damaged[damaged.Length / 2] ^= 1;
                File.WriteAllBytes(temporary + ".damaged", damaged);
                bool rejected = false;
                try { Credentials.Load(temporary + ".damaged"); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
                Check(rejected, "tampered password file rejected");

                var policy = new KeyboardPolicy();
                Check(policy.Block(0x5B, true) && policy.Block(0x44, true), "Win+D blocked");
                Check(Native.ExecutionFlags(true, true) == (Native.Continuous | Native.SystemRequired), "monitor-off prevents system sleep without keeping display on");
                Check(Native.ExecutionFlags(true, false) == (Native.Continuous | Native.SystemRequired | Native.DisplayRequired), "black-only preserves display-on behavior");
                policy.Block(0x5B, false);
                Check(policy.Block(0x12, true) && policy.Block(9, true) && policy.Block(0x73, true), "Alt+Tab and Alt+F4 blocked");
                policy.Block(0x12, false);
                Check(policy.Block(0x11, true) && policy.Block(27, true), "Ctrl+Esc blocked");
                policy.Block(0x11, false);
                Check(policy.Block(0xB3, true) && policy.Block(0x5F, true) && policy.Block(0x2C, true), "media, sleep and screenshot keys blocked");
                Check(!policy.Block(0x41, true) && !policy.Block(0xA0, true) && !policy.Block(13, true), "password text, Shift and Enter allowed");
                Check(policy.Block(0xE7, true) && policy.Block(0xE7, false), "original keyboard policy retained without additional packet input allowance");
                policy.Block(0x11, true); policy.Reset();
                Check(!policy.Block(0x41, true), "modifier reset permits typing after desktop transition");

                string original = Native.DesktopName();
                string uiCredentials = temporary + ".ui";
                try
                {
                    using (var form = new MainForm(uiCredentials, Path.Combine(directory, "ui-flow")))
                    {
                        form.Show();
                        var work = form.StartTestFlow();
                        var clock = Stopwatch.StartNew();
                        while (!work.IsCompleted && clock.ElapsedMilliseconds < 10000) Pump();
                        work.GetAwaiter().GetResult();
                        Check(form.DialogResult == DialogResult.OK, "first-use UI: save password and request immediate lock without countdown");
                        Check(Credentials.Load(uiCredentials).Verify("TestOnly-42!"), "first-use UI: saved password verifies");
                    }
                    using (var settings = new MainForm(uiCredentials, Path.Combine(directory, "ui-flow")))
                        settings.SavePreview(Path.Combine(directory, "saved-password-window.png"));
                    using (var settings = new MainForm(uiCredentials, Path.Combine(directory, "ui-flow")))
                    {
                        settings.Show(); settings.StartSavedTestFlow().GetAwaiter().GetResult();
                        Check(settings.DialogResult == DialogResult.OK, "saved password starts lock without re-entering password");
                    }
                    using (var settings = new MainForm(uiCredentials, Path.Combine(directory, "ui-flow")))
                    {
                        settings.Show();
                        var change = settings.ChangeTestPassword("wrong-old-password", "Replacement-42!");
                        while (!change.IsCompleted) Pump(); change.GetAwaiter().GetResult();
                        Check(settings.TestStatus == "当前密码不对。" && Credentials.Load(uiCredentials).Verify("TestOnly-42!"), "password change rejects incorrect current password");
                        change = settings.ChangeTestPassword("TestOnly-42!", "Replacement-42!");
                        while (!change.IsCompleted) Pump(); change.GetAwaiter().GetResult();
                        Check(settings.DialogResult == DialogResult.OK && Credentials.Load(uiCredentials).Verify("Replacement-42!"), "password change verifies old password and saves replacement");
                        record.Save(uiCredentials);
                    }
                    RunQuickLock(directory, uiCredentials, original, "unlock");
                    RunQuickLock(directory, uiCredentials, original, "child-crash");
                    RunQuickLock(directory, uiCredentials, original, "monitor-off");
                    Check(File.Exists(Path.Combine(directory, "quick-monitor-off", "monitor-off-command.txt")), "monitor power-off command reaches real guard window");
                    RunQuickLock(directory, uiCredentials, original, "display-wake");
                    Check(File.Exists(Path.Combine(directory, "quick-display-wake", "display-wake.txt")), "wake automatically reveals and focuses password, preserves input on duplicate events, and survives desktop switch");
                    RunQuickLock(directory, uiCredentials, original, "display-wake-physical");
                    Check(File.Exists(Path.Combine(directory, "quick-display-wake-physical", "physical-display-wake.txt")), "actual Windows display off/on notifications reveal focused password UI before password unlock");
                }
                finally { if (File.Exists(uiCredentials)) File.Delete(uiCredentials); }
                RunSession(directory, temporary, original, "auto", 0);
                Check(File.ReadAllText(Path.Combine(directory, "auto", "guard-ready.txt")).Contains("idleTimer=False"), "guard has no running idle polling timer");
                RunSession(directory, temporary, original, "unlock", 0);
                Check(File.Exists(Path.Combine(directory, "unlock", "wrong-password-blocked.txt")), "wrong password keeps guard active");
                RunSession(directory, temporary, original, "child-crash", 37);
                RunSession(directory, temporary, original, "restore-retry", 0);
                Check(File.ReadAllText(Path.Combine(directory, "restore-retry", "restore-retries.txt")) == "2", "guard retries transient desktop restoration failures");
                RunSession(directory, temporary, original, "foreign-focus", 0);
                Check(File.Exists(Path.Combine(directory, "foreign-focus", "focus-input-blocked.txt")), "foreign process receives no password characters after stealing focus");
                RunSession(directory, temporary, original, "prompt-timeout", 0);
                Check(File.Exists(Path.Combine(directory, "prompt-timeout", "prompt-timeout.txt")), "prompt expiry hides password UI and stops timer");
                using (var launcher = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "--test-parent-crash \"" + temporary + "\" \"" + directory + "\"") { UseShellExecute = false }))
                {
                    Check(launcher.WaitForExit(10000) && launcher.ExitCode == 38, "launcher crash simulated");
                    var clock = Stopwatch.StartNew();
                    while (Native.InputDesktopName() != original && clock.ElapsedMilliseconds < 5000) Pump();
                    Check(Native.InputDesktopName() == original, "guard restores desktop after launcher crash");
                }
                Check(Native.DesktopName() == original, "launcher stays attached to original desktop");
                lines.Add("NOTE: physical multi-monitor, Ctrl+Alt+Del, hardware buttons and real course-player continuity require manual acceptance.");
                File.WriteAllLines(Path.Combine(directory, "tests.txt"), lines.ToArray());
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString()); File.WriteAllLines(Path.Combine(directory, "tests.txt"), lines.ToArray()); return 1;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(temporary + ".damaged")) File.Delete(temporary + ".damaged");
            }
        }
        private static void RunSession(string directory, string credentials, string original, string mode, int expectedCode)
        {
            int ticks = 0;
            using (var heartbeat = new System.Windows.Forms.Timer { Interval = 50 })
            using (var session = new GuardSession(credentials, true, mode, Path.Combine(directory, mode)))
            {
                heartbeat.Tick += delegate { ticks++; };
                heartbeat.Start();
                var clock = Stopwatch.StartNew();
                while (!session.Ready && !session.Exited && clock.ElapsedMilliseconds < 10000) Pump();
                Check(session.Ready && !session.Exited, mode + ": isolated desktop windows ready");
                session.Activate();
                Check(Native.InputDesktopName() == session.Desktop, mode + ": input desktop actually switched");
                int before = ticks;
                while (!session.Exited && clock.ElapsedMilliseconds < (mode == "prompt-timeout" ? 27000 : 18000)) Pump();
                Check(session.Exited, mode + ": guard exited within timeout");
                Check(session.Child.ExitCode == expectedCode, mode + ": expected exit code " + expectedCode);
                Native.RestoreFailuresRemaining = 1;
                Check(!session.TryRestore(), mode + ": transient restore failure remains retryable");
                Check(session.TryRestore(), mode + ": launcher restoration succeeds on retry");
                Check(Native.InputDesktopName() == original, mode + ": input desktop actually restored");
                Check(ticks - before > 10, mode + ": original desktop timer continues while black");
                Check(File.Exists(Path.Combine(directory, mode, "guard-ready.txt")), mode + ": guard reports active isolated desktop");
                Check(File.Exists(Path.Combine(directory, mode, "keyboard-blocked.txt")), mode + ": real injected switching and media shortcuts blocked");
            }
        }
        private static void Pump() { Application.DoEvents(); Thread.Sleep(15); }
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        internal static void SendDisplayState(IntPtr window, int state)
        {
            IntPtr setting = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(Native.SessionDisplayStatus, setting, false);
                Marshal.WriteInt32(setting, 16, 4);
                Marshal.WriteInt32(setting, 20, state);
                SendMessage(window, Native.WmPowerBroadcast, new IntPtr(Native.PowerSettingChange), setting);
            }
            finally { Marshal.FreeHGlobal(setting); }
        }
        private sealed class DisplayObserver : NativeWindow, IDisposable
        {
            private readonly IntPtr registration;
            private readonly Guid setting = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
            internal readonly List<int> States = new List<int>();
            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
            [DllImport("user32.dll")]
            private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
            internal DisplayObserver()
            {
                CreateHandle(new CreateParams { Caption = "BlackScreenGuard display test", Parent = new IntPtr(-3) });
                Guid value = setting;
                registration = RegisterPowerSettingNotification(Handle, ref value, 0);
                if (registration == IntPtr.Zero) { DestroyHandle(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x218 && message.WParam.ToInt64() == 0x8013 && message.LParam != IntPtr.Zero &&
                    (Guid)Marshal.PtrToStructure(message.LParam, typeof(Guid)) == setting && Marshal.ReadInt32(message.LParam, 16) == 4)
                    States.Add(Marshal.ReadInt32(message.LParam, 20));
                base.WndProc(ref message);
            }
            public void Dispose() { UnregisterPowerSettingNotification(registration); DestroyHandle(); }
        }
        private static void PumpFor(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds) Pump();
        }
        internal static int Measure(string executable, string directory) { return Measure(executable, directory, false); }
        internal static int Measure(string executable, string directory, bool displayOff)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string original = Native.DesktopName(), token = Guid.NewGuid().ToString("N");
            string credentials = Path.Combine(directory, "temporary-password.dat");
            IntPtr desktop = IntPtr.Zero;
            Process child = null;
            try
            {
                Credentials.Create("TestOnly-42!").Save(credentials);
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + token + "_ready"))
                using (var active = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + token + "_active"))
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\BSG_" + token + "_stop"))
                using (var display = new DisplayObserver())
                {
                    desktop = Native.CreateDesktop("BlackScreenGuard_" + token, IntPtr.Zero, IntPtr.Zero, 0, Native.DesktopAccess, IntPtr.Zero);
                    if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var startup = new Native.StartupInfo(); startup.Size = Marshal.SizeOf(startup); startup.Desktop = "WinSta0\\BlackScreenGuard_" + token;
                    var command = new StringBuilder("\"" + executable + "\" --guard \"" + original + "\" " + token + " " + Process.GetCurrentProcess().Id + " \"" + credentials + "\" " + (displayOff ? "3" : "1") + " \"\" \"\"");
                    Native.ProcessInfo info;
                    var clock = Stopwatch.StartNew();
                    if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out info))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    try { child = Process.GetProcessById(info.ProcessId); if (child.Handle == IntPtr.Zero) throw new InvalidOperationException(); }
                    finally { Native.CloseHandle(info.Thread); Native.CloseHandle(info.Process); }
                    if (!ready.WaitOne(10000)) throw new TimeoutException();
                    if (!Native.SwitchDesktop(desktop)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    active.Set();
                    long startupMs = clock.ElapsedMilliseconds;
                    // Warm up runtime/event threads, then sample the actual production guard without input.
                    PumpFor(2500); child.Refresh();
                    TimeSpan before = child.TotalProcessorTime;
                    clock.Restart(); PumpFor(displayOff ? 5000 : 15000); child.Refresh();
                    double duration = clock.Elapsed.TotalMilliseconds, cpuMs = (child.TotalProcessorTime - before).TotalMilliseconds;
                    File.WriteAllLines(Path.Combine(directory, "performance.txt"), new string[] {
                        "executable=" + executable, "startupToBlackMs=" + startupMs,
                        "sampleDurationMs=" + duration.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                        "cpuTimeMs=" + cpuMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        "cpuPercentOfOneCore=" + (100 * cpuMs / duration).ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                        "workingSetBytes=" + child.WorkingSet64, "privateBytes=" + child.PrivateMemorySize64,
                        "handles=" + child.HandleCount, "threads=" + child.Threads.Count,
                        "displayStates=" + String.Join(",", display.States) });
                    stop.Set();
                    if (!child.WaitForExit(5000) || child.ExitCode != 2) throw new InvalidOperationException("Stop event did not recover the original desktop.");
                    if (Native.InputDesktopName() != original) throw new InvalidOperationException("Desktop was not restored.");
                    PumpFor(1000);
                    File.WriteAllText(Path.Combine(directory, "display-power.txt"), "states=" + String.Join(",", display.States) + "\r\n0=off; 1=on; 2=dim");
                    if (displayOff && !display.States.Contains(0)) throw new InvalidOperationException("Windows 未报告显示器已息屏，请检查驱动或其他软件的亮屏请求。");
                }
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); return 1; }
            finally
            {
                Native.TryRestore(original);
                if (displayOff) Native.SetThreadExecutionState(Native.DisplayRequired);
                if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
                if (desktop != IntPtr.Zero) Native.CloseDesktop(desktop);
                if (File.Exists(credentials)) File.Delete(credentials);
            }
        }
        private static void RunQuickLock(string directory, string credentials, string original, string mode)
        {
            var clock = Stopwatch.StartNew();
            using (var context = new QuickLockContext(credentials, true, mode == "monitor-off" || mode.StartsWith("display-wake"), mode, Path.Combine(directory, "quick-" + mode)))
            {
                while (!context.Activated && !context.Completed && clock.ElapsedMilliseconds < 8000) Pump();
                long activation = clock.ElapsedMilliseconds;
                Check(context.Activated && Native.InputDesktopName() != original, "quick launch switches directly to black desktop: " + mode);
                Check(activation < 2500, "quick launch has no three-second countdown: " + activation + " ms");
                Check(!context.IdleTimerEnabled, "quick launcher has no idle polling timer: " + mode);
                if (mode == "child-crash") Native.RestoreFailuresRemaining = 1;
                while (!context.Completed && clock.ElapsedMilliseconds < 18000) Pump();
                Check(context.Completed && context.Result == (mode == "child-crash" ? 37 : 0), "quick launcher exits after unlock or child exit: " + mode);
                Check(Native.InputDesktopName() == original, "quick launcher restores original desktop: " + mode);
            }
        }
        internal static int FocusTarget(string directory)
        {
            using (var form = new Form { Text = "BlackScreenGuard test focus target", Width = 320, Height = 150 })
            using (var lifetime = new System.Windows.Forms.Timer { Interval = 8000 })
            {
                var input = new TextBox { Dock = DockStyle.Top };
                string captured = Path.Combine(directory, "focus-target-keys.txt");
                File.WriteAllText(captured, "");
                input.KeyPress += delegate(object sender, KeyPressEventArgs e) { File.AppendAllText(captured, e.KeyChar.ToString()); };
                form.Controls.Add(input);
                form.Shown += delegate
                {
                    form.Activate(); input.Focus();
                    File.WriteAllText(Path.Combine(directory, "focus-target-ready.txt"), "ready");
                };
                lifetime.Tick += delegate { form.Close(); };
                lifetime.Start(); Application.Run(form);
            }
            return 0;
        }
        internal static int CrashLauncher(string credentials, string directory)
        {
            using (var session = new GuardSession(credentials, true, "parent-crash", Path.Combine(directory, "parent-crash")))
            {
                var clock = Stopwatch.StartNew();
                while (!session.Ready && !session.Exited && clock.ElapsedMilliseconds < 8000) Pump();
                if (!session.Ready || session.Exited) return 1;
                session.Activate();
                clock.Restart();
                while (clock.ElapsedMilliseconds < 1400) Pump();
                // Abrupt exit deliberately skips using/finally, exercising the child's independent recovery.
                Environment.Exit(38);
                return 38;
            }
        }
    }
}
