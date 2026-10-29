using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
                Check(release.GetName().Version.ToString() == "1.0.1.0", "release version is 1.0.1.0");
                Check(release.GetType("BlackScreenGuard.SelfTests") == null, "release excludes self-test implementation");
                var native = release.GetType("BlackScreenGuard.Native", true);
                Check(native.GetField("RestoreFailuresRemaining", BindingFlags.Static | BindingFlags.NonPublic) == null, "release excludes fault injection");
                var guard = release.GetType("BlackScreenGuard.GuardContext", true);
                Check(guard.GetMethod("InjectCombination", BindingFlags.Static | BindingFlags.NonPublic) == null, "release excludes test key injection");
                Check(guard.GetField("testMode", BindingFlags.Instance | BindingFlags.NonPublic) == null, "release excludes automatic test unlock state");
                foreach (string argument in new string[] { "--self-test unused", "--test-parent-crash unused unused", "--test-focus-target unused", "--preview unused", "--guard Default unused 0 unused 0 auto unused" })
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
                policy.Block(0x5B, false);
                Check(policy.Block(0x12, true) && policy.Block(9, true) && policy.Block(0x73, true), "Alt+Tab and Alt+F4 blocked");
                policy.Block(0x12, false);
                Check(policy.Block(0x11, true) && policy.Block(27, true), "Ctrl+Esc blocked");
                policy.Block(0x11, false);
                Check(policy.Block(0xB3, true) && policy.Block(0x5F, true) && policy.Block(0x2C, true), "media, sleep and screenshot keys blocked");
                Check(!policy.Block(0x41, true) && !policy.Block(0xA0, true) && !policy.Block(13, true), "password text, Shift and Enter allowed");
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
                        while ((!work.IsCompleted || !form.TestComplete) && clock.ElapsedMilliseconds < 20000) Pump();
                        work.GetAwaiter().GetResult();
                        Check(form.TestComplete, "first-use UI: set password, countdown, lock, unlock and restore");
                        Check(Credentials.Load(uiCredentials).Verify("TestOnly-42!"), "first-use UI: saved password verifies");
                        Check(Native.InputDesktopName() == original, "first-use UI: original input desktop restored");
                        form.SavePreview(Path.Combine(directory, "saved-password-window.png"));
                    }
                }
                finally { if (File.Exists(uiCredentials)) File.Delete(uiCredentials); }
                RunSession(directory, temporary, original, "auto", 0);
                RunSession(directory, temporary, original, "unlock", 0);
                Check(File.Exists(Path.Combine(directory, "unlock", "wrong-password-blocked.txt")), "wrong password keeps guard active");
                RunSession(directory, temporary, original, "child-crash", 37);
                RunSession(directory, temporary, original, "restore-retry", 0);
                Check(File.ReadAllText(Path.Combine(directory, "restore-retry", "restore-retries.txt")) == "2", "guard retries transient desktop restoration failures");
                RunSession(directory, temporary, original, "foreign-focus", 0);
                Check(File.Exists(Path.Combine(directory, "foreign-focus", "focus-input-blocked.txt")), "foreign process receives no password characters after stealing focus");
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
                while (!session.Exited && clock.ElapsedMilliseconds < 18000) Pump();
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
