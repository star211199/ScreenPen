using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Shows recognised text in a selectable, read-only box.
    ///
    /// A plain modal window rather than a balloon: the point of OCR is to get the
    /// text out, so the user needs to be able to select part of it, copy it again,
    /// and read it at their own pace.
    /// </summary>
    sealed class OcrResultForm : Form
    {
        readonly TextBox _box;

        OcrResultForm(string text, string languageTag, bool copied)
        {
            Text = "识别结果 - ScreenPen";
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 380);
            MinimumSize = new Size(360, 240);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Dock = DockStyle.Top;
            hint.Height = 34;
            hint.Padding = new Padding(10, 8, 10, 0);
            hint.ForeColor = Color.FromArgb(90, 90, 96);
            hint.Text = copied
                ? "已复制到剪贴板。" + LanguageSuffix(languageTag) + "  修改后按 Ctrl+C 可再次复制。"
                : "按 Ctrl+C 复制全部文字。" + LanguageSuffix(languageTag);
            Controls.Add(hint);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.Dock = DockStyle.Fill;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Color.White;
            _box.Font = new Font("Microsoft YaHei UI", 10f);
            _box.Text = text;

            // The text area sits inside a padded panel so the borderless box does
            // not touch the window edge.
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(10, 0, 10, 8);
            body.Controls.Add(_box);
            Controls.Add(body);
            body.BringToFront();

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 46;
            footer.Padding = new Padding(10, 8, 10, 8);

            Button copy = new Button();
            copy.Text = "复制";
            copy.Width = 84;
            copy.Height = 28;
            copy.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            copy.Location = new Point(footer.Width - 94, 9);
            copy.Click += delegate
            {
                try { Clipboard.SetText(_box.Text); }
                catch (Exception) { }
                DialogResult = DialogResult.OK;
                Close();
            };

            Button close = new Button();
            close.Text = "关闭";
            close.Width = 84;
            close.Height = 28;
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            close.Location = new Point(footer.Width - 184, 9);
            close.Click += delegate { Close(); };

            footer.Controls.Add(copy);
            footer.Controls.Add(close);
            footer.Resize += delegate
            {
                copy.Left = footer.ClientSize.Width - 94;
                close.Left = footer.ClientSize.Width - 184;
            };
            Controls.Add(footer);
            footer.BringToFront();

            CancelButton = close;
        }

        static string LanguageSuffix(string tag)
        {
            return string.IsNullOrEmpty(tag) ? "" : "  识别语言：" + tag;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        }

        /// <summary>
        /// Shows the result. Kept as a single entry point so the caller does not
        /// own the form's lifetime and cannot leak it.
        /// </summary>
        public static void ShowResult(string text, string languageTag, bool copied)
        {
            using (OcrResultForm f = new OcrResultForm(text, languageTag, copied))
                f.ShowDialog();
        }

        /// <summary>Test seam: the text currently displayed.</summary>
        internal string TextForTest { get { return _box.Text; } }

        /// <summary>Test seam: build without showing, so probes can inspect it.</summary>
        internal static OcrResultForm CreateForTest(string text, string languageTag, bool copied)
        {
            return new OcrResultForm(text, languageTag, copied);
        }
    }
}
