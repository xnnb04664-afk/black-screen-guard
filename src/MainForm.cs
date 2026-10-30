using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BlackScreenGuard
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(26, 38, 57), Blue = Color.FromArgb(36, 81, 185);
        private readonly TextBox current, next, confirm;
        private readonly Label status, heading, currentLabel;
        private readonly CheckBox awake;
        private readonly Button start, change;
        private readonly Control newFields;
        private readonly TableLayoutPanel layout;
        private Credentials stored;
        private readonly string credentialPath, testDirectory;
        private readonly bool displayOff;
        private bool changing, busy;
        internal bool KeepAwake { get { return awake.Checked; } }

        internal MainForm() : this(Credentials.FilePath, null, true) { }
        internal MainForm(string credentialPath, string testDirectory) : this(credentialPath, testDirectory, true) { }
        internal MainForm(string credentialPath, string testDirectory, bool displayOff)
        {
            this.credentialPath = credentialPath; this.testDirectory = testDirectory;
            this.displayOff = displayOff;
            SuspendLayout();
            Text = "黑屏守护";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 10);
            ForeColor = Ink; BackColor = Color.FromArgb(245, 247, 251);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            ClientSize = new Size(548, 692);
            Padding = new Padding(30, 24, 30, 24);

            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, BackColor = BackColor };
            layout = stack;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 79));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 157));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(stack);
            stack.Controls.Add(new Label { Text = "黑屏守护", Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            stack.Controls.Add(new PreviewPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 10) }, 0, 1);
            stack.Controls.Add(new Label { Text = "隔离键鼠操作   /   多屏黑屏   /   后台任务继续", Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9), ForeColor = Color.FromArgb(76, 91, 113), TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            heading = new Label { Text = "第一次使用，先设置密码", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            stack.Controls.Add(heading, 0, 3);
            var first = Field("解锁密码（6–128 位英文、数字或符号）", out current);
            currentLabel = (Label)first.Controls[0];
            stack.Controls.Add(first, 0, 4);
            var newStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            newStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); newStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            newStack.Controls.Add(Field("新密码", out next), 0, 0);
            newStack.Controls.Add(Field("再输一次密码", out confirm), 0, 1);
            newFields = newStack; stack.Controls.Add(newFields, 0, 5);
            awake = new CheckBox { Text = displayOff ? "防止电脑自动睡眠（显示器仍可息屏）" : "黑屏期间防止电脑自动睡眠", Checked = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            stack.Controls.Add(awake, 0, 6);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            start = MakeButton(displayOff ? "立即息屏" : "立即黑屏", true); start.Dock = DockStyle.Fill; start.Margin = new Padding(0, 3, 10, 3);
            change = MakeButton("修改密码", false); change.Dock = DockStyle.Fill; change.Margin = new Padding(0, 3, 0, 3);
            start.Click += async delegate { await BeginLock(); };
            change.Click += delegate { changing = !changing; UpdateMode(); };
            actions.Controls.Add(start, 0, 0); actions.Controls.Add(change, 1, 0); stack.Controls.Add(actions, 0, 7);
            status = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(163, 52, 52), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Microsoft YaHei UI", 9) };
            stack.Controls.Add(status, 0, 8);
            stack.Controls.Add(new Label { Text = "按任意键或单击后输入密码解开。\r\n系统安全界面、关机及强制重启仍可使用。",
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(98, 111, 132), Font = new Font("Microsoft YaHei UI", 8.5f) }, 0, 9);
            AcceptButton = start;
            try { stored = Credentials.Load(credentialPath); }
            catch (Exception)
            {
                start.Enabled = false; change.Enabled = false;
                status.Text = "密码文件无法读取，请查看使用说明中的恢复方法。";
            }
            UpdateMode();
            ResumeLayout(true);
        }

        internal static Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.FromArgb(226, 232, 243),
                ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }
        private static Panel Field(string caption, out TextBox input)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var label = new Label { Text = caption, Dock = DockStyle.Top, Height = 25, Font = new Font("Microsoft YaHei UI", 9) };
            input = new TextBox { UseSystemPasswordChar = true, MaxLength = 128, ImeMode = ImeMode.Disable, Dock = DockStyle.Bottom,
                Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 13) };
            panel.Padding = new Padding(0, 0, 0, 15);
            panel.Controls.Add(label); panel.Controls.Add(input);
            return panel;
        }
        private void UpdateMode()
        {
            bool exists = stored != null;
            heading.Text = exists ? (changing ? "验证原密码，再设置新密码" : (displayOff ? "密码已保存，下次双击图标立即息屏" : "密码已保存，点击按钮立即黑屏")) : "第一次使用，先设置密码";
            currentLabel.Text = exists ? "当前密码" : "解锁密码（6–128 位英文、数字或符号）";
            current.Parent.Visible = !exists || changing;
            next.Parent.Visible = exists && changing;
            confirm.Parent.Visible = !exists || changing;
            newFields.Visible = !exists || changing;
            ((TableLayoutPanel)newFields).RowStyles[0] = new RowStyle(SizeType.Absolute, exists && changing ? 78 : 0);
            ((TableLayoutPanel)newFields).RowStyles[1] = new RowStyle(SizeType.Percent, 100);
            int fieldsHeight = changing && exists ? 157 : (exists ? 0 : 79);
            float scale = AutoScaleDimensions.Width / 96f;
            layout.RowStyles[5].Height = fieldsHeight * scale;
            layout.RowStyles[4].Height = (!exists || changing ? 79 : 0) * scale;
            ((TableLayoutPanel)newFields).RowStyles[0].Height = (exists && changing ? 78 : 0) * scale;
            ClientSize = new Size((int)(548 * scale), (int)((565 + fieldsHeight - (exists && !changing ? 79 : 0)) * scale));
            change.Visible = exists; change.Text = changing ? "取消修改" : "修改密码";
            current.Clear(); next.Clear(); confirm.Clear();
        }
        private async Task BeginLock()
        {
            if (busy) return;
            status.Text = "";
            string old = current.Text, fresh = next.Text, repeated = confirm.Text;
            if (stored == null || changing)
            {
                string validation = Credentials.Validate(stored == null ? old : fresh, repeated);
                if (validation != null) { status.Text = validation; return; }
            }
            busy = true; SetInputs(false); start.Enabled = false;
            try
            {
                Credentials replacement = null;
                bool valid = true;
                if (stored == null || changing) valid = await Task.Run(delegate
                {
                    if (stored != null && !stored.Verify(old)) return false;
                    if (stored == null || changing) replacement = Credentials.Create(stored == null ? old : fresh);
                    return true;
                });
                if (IsDisposed || Disposing) return;
                if (!valid) { status.Text = "当前密码不对。"; busy = false; SetInputs(true); return; }
                if (replacement != null) { replacement.Save(credentialPath); stored = replacement; }
                changing = false; SetInputs(false);
                current.Clear(); next.Clear(); confirm.Clear();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { status.Text = "无法开始：" + ex.Message; busy = false; SetInputs(true); }
            finally { old = null; fresh = null; repeated = null; }
        }
        private void SetInputs(bool enabled)
        {
            current.Enabled = enabled; next.Enabled = enabled; confirm.Enabled = enabled; awake.Enabled = enabled; change.Enabled = enabled; start.Enabled = enabled;
        }
#if TEST_BUILD
        internal void SavePreview(string path)
        {
            Show(); Update();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); }
            File.WriteAllText(path + ".txt", "DPI=" + DeviceDpi + "; size=" + ClientSize + "; font=" + Font + "; scale=" + AutoScaleDimensions);
            Close();
        }
        internal Task StartTestFlow()
        {
            if (testDirectory == null) throw new InvalidOperationException();
            current.Text = "TestOnly-42!"; confirm.Text = "TestOnly-42!";
            return BeginLock();
        }
        internal Task ChangeTestPassword(string old, string fresh)
        {
            changing = true; UpdateMode(); current.Text = old; next.Text = fresh; confirm.Text = fresh;
            return BeginLock();
        }
        internal Task StartSavedTestFlow() { return BeginLock(); }
        internal string TestStatus { get { return status.Text; } }
#endif
    }

    internal sealed class PreviewPanel : Panel
    {
        internal PreviewPanel() { DoubleBuffered = true; BackColor = Color.FromArgb(11, 15, 23); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f, mid = Height / 2f;
            using (var pen = new Pen(Color.FromArgb(139, 171, 237), 2 * scale))
            {
                g.DrawRectangle(pen, 27 * scale, mid - 27 * scale, 56 * scale, 37 * scale);
                g.DrawLine(pen, 55 * scale, mid + 11 * scale, 55 * scale, mid + 21 * scale);
                g.DrawLine(pen, 41 * scale, mid + 22 * scale, 69 * scale, mid + 22 * scale);
            }
            using (var title = new Font("Microsoft YaHei UI", 17, FontStyle.Bold))
            using (var body = new Font("Microsoft YaHei UI", 9))
            using (var muted = new SolidBrush(Color.FromArgb(160, 173, 196)))
            {
                g.DrawString("屏幕黑下来，任务继续。", title, Brushes.White, 106 * scale, mid - 28 * scale);
                g.DrawString("BLACK SCREEN / KEEP RUNNING", body, muted, 109 * scale, mid + 12 * scale);
            }
        }
    }
}
