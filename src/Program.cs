using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("黑屏守护")]
[assembly: AssemblyDescription("独立黑屏桌面与密码解锁")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace BlackScreenGuard
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--guard") return RunGuard(args);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
#if TEST_BUILD
            if (args.Length == 2 && args[0] == "--test-focus-target") return SelfTests.FocusTarget(args[1]);
            if (args.Length == 3 && args[0] == "--verify-release") return SelfTests.VerifyRelease(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length == 3 && args[0] == "--measure") return SelfTests.Measure(args[1], args[2]);
            if (args.Length == 3 && args[0] == "--check-display-power") return SelfTests.Measure(args[1], args[2], true);
            if (args.Length == 3 && args[0] == "--test-parent-crash") return SelfTests.CrashLauncher(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--preview")
            {
                using (var form = new MainForm()) form.SavePreview(Path.GetFullPath(args[1]));
                return 0;
            }
#endif
            bool settings = false, displayOff = true;
            foreach (string argument in args)
            {
                if (argument == "--settings") settings = true;
                else if (argument == "--black-only") displayOff = false;
                else return 1;
            }
            settings |= (Control.ModifierKeys & Keys.Shift) != 0;
            bool created;
            using (var mutex = new Mutex(true, "Local\\BlackScreenGuard_Launcher", out created))
            {
                if (!created) { MessageBox.Show("黑屏守护已在运行，请使用已打开的窗口。", "黑屏守护"); return 0; }
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    bool awake = true;
                    if (settings || Credentials.Load(Credentials.FilePath) == null)
                    {
                        using (var form = new MainForm(Credentials.FilePath, null, displayOff))
                        {
                            if (form.ShowDialog() != DialogResult.OK) return 0;
                            awake = form.KeepAwake;
                        }
                    }
                    using (var context = new QuickLockContext(Credentials.FilePath, awake, displayOff, null, null))
                    {
                        Application.Run(context);
                        return context.Result;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("工具已退出。\r\n" + ex.Message, "黑屏守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
        private static int RunGuard(string[] args)
        {
            if (args.Length != 8) return 1;
            int power;
            if (!Int32.TryParse(args[5], out power) || power < 0 || power > 3) return 1;
#if !TEST_BUILD
            if (args[6].Length != 0 || args[7].Length != 0) return 1;
#endif
            string original = args[1];
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
#if TEST_BUILD
                Control.CheckForIllegalCrossThreadCalls = true;
#endif
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using (var context = new GuardContext(original, args[2], Int32.Parse(args[3]), args[4], (power & 1) != 0, (power & 2) != 0, args[6], args[7]))
                    Application.Run(context);
                return Environment.ExitCode;
            }
            catch (Exception ex)
            {
                try { Native.Restore(original); } catch { }
                // Log diagnostics without passwords or credential material.
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlackScreenGuard");
#if TEST_BUILD
                if (!String.IsNullOrEmpty(args[7])) directory = args[7];
#endif
                try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "last-error.txt"), DateTime.Now.ToString("s") + "\r\n" + ex); } catch { }
                return 1;
            }
        }
    }
}
