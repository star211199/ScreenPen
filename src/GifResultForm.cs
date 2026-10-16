using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Tells the user where a finished GIF went, and gives them a way to act on
    /// it.
    ///
    /// A balloon notification was not enough: it disappears after a couple of
    /// seconds, cannot be selected, and gives no way to reach the file. The
    /// recording then felt like it had produced nothing at all - the reported
    /// complaint was literally "不知道生成的动图在哪里". So the path is shown in
    /// a selectable box together with 复制路径 / 在文件夹中显示 / 打开动图.
    ///
    /// Layout is explicit (no Dock) on purpose: docked children are resolved in
    /// reverse z-order, which makes "which one ends up on top" depend on the
    /// order of the Add calls. Explicit rectangles behave the same everywhere.
    /// </summary>
    sealed class GifResultForm : Form
    {
        readonly TextBox _pathBox;
        readonly Label _head, _info, _pathHint, _status;
        readonly Button _copyPath, _reveal, _open, _close;

        const int Pad = 14;
        const int HeadH = 34;
        const int InfoH = 24;
        const int HintH = 22;
        const int BoxH = 28;
        const int BtnH = 30;
        const int StatusH = 22;

        GifResultForm(string path, int frames, int seconds)
        {
            Text = "GIF 已保存 - ScreenPen";
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(620, 250);
            MinimumSize = new Size(460, 250);

            string name = "";
            try { name = Path.GetFileName(path); }
            catch { }

            _head = new Label();
            _head.AutoSize = false;
            _head.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            _head.Text = "已保存：" + name;

            _info = new Label();
            _info.AutoSize = false;
            _info.ForeColor = Color.FromArgb(90, 90, 96);
            _info.Text = Describe(frames, seconds, path);

            _pathHint = new Label();
            _pathHint.AutoSize = false;
            _pathHint.ForeColor = Color.FromArgb(90, 90, 96);
            _pathHint.Text = "保存位置（可直接选中复制）：";

            // A read-only box rather than a label so the path can be selected,
            // copied with Ctrl+C and scrolled when the folder is deep.
            _pathBox = new TextBox();
            _pathBox.ReadOnly = true;
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            _pathBox.BackColor = Color.White;
            _pathBox.Font = new Font("Consolas", 9.5f);
            _pathBox.Text = path;
            _pathBox.Enter += delegate { _pathBox.SelectAll(); };

            _status = new Label();
            _status.AutoSize = false;
            _status.ForeColor = Color.FromArgb(90, 90, 96);

            _copyPath = new Button();
            _copyPath.Text = "复制路径";
            _copyPath.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_pathBox.Text);
                    _status.Text = "路径已复制到剪贴板。";
                }
                catch (Exception ex)
                {
                    _status.Text = "复制失败：" + ex.Message;
                }
            };

            _reveal = new Button();
            _reveal.Text = "在文件夹中显示";
            _reveal.Click += delegate
            {
                // explorer /select highlights the file itself, which is far more
                // useful than merely opening the containing folder.
                if (!Run("explorer.exe", "/select,\"" + _pathBox.Text + "\""))
                    _status.Text = "无法打开资源管理器。";
            };

            _open = new Button();
            _open.Text = "打开动图";
            _open.Click += delegate
            {
                if (!Run(_pathBox.Text, null))
                    _status.Text = "无法打开文件，可能已被移动或删除。";
            };

            _close = new Button();
            _close.Text = "关闭";
            _close.Click += delegate { Close(); };

            Controls.Add(_head);
            Controls.Add(_info);
            Controls.Add(_pathHint);
            Controls.Add(_pathBox);
            Controls.Add(_status);
            Controls.Add(_copyPath);
            Controls.Add(_reveal);
            Controls.Add(_open);
            Controls.Add(_close);

            LayoutChildren();
            CancelButton = _close;
            _pathBox.Focus();
            _pathBox.SelectAll();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        void LayoutChildren()
        {
            // Setting ClientSize in the constructor raises OnResize, which lands
            // here before a single child has been created. Without this guard the
            // form throws NullReferenceException during construction - and a
            // crash on the one screen that exists to tell the user where their
            // recording went is worse than the balloon it replaced.
            if (_head == null || _close == null) return;

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int inner = Math.Max(40, w - Pad * 2);

            int y = Pad;
            _head.SetBounds(Pad, y, inner, HeadH);
            y += HeadH;
            _info.SetBounds(Pad, y, inner, InfoH);
            y += InfoH;
            _pathHint.SetBounds(Pad, y, inner, HintH);
            y += HintH;
            _pathBox.SetBounds(Pad, y, inner, BoxH);

            // The button row sits above the status line, both pinned to the
            // bottom, so the path box keeps whatever vertical room is left.
            int statusTop = h - Pad - StatusH;
            int btnTop = statusTop - 6 - BtnH;

            _status.SetBounds(Pad, statusTop, inner, StatusH);

            int right = w - Pad;
            _close.SetBounds(right - _close.PreferredSize.Width - 16, btnTop,
                             _close.PreferredSize.Width + 16, BtnH);
            _open.SetBounds(_close.Left - 8 - (_open.PreferredSize.Width + 16), btnTop,
                            _open.PreferredSize.Width + 16, BtnH);
            _reveal.SetBounds(_open.Left - 8 - (_reveal.PreferredSize.Width + 16), btnTop,
                              _reveal.PreferredSize.Width + 16, BtnH);
            _copyPath.SetBounds(_reveal.Left - 8 - (_copyPath.PreferredSize.Width + 16), btnTop,
                                _copyPath.PreferredSize.Width + 16, BtnH);
        }

        static bool Run(string file, string args)
        {
            try
            {
                if (args == null) Process.Start(file);
                else Process.Start(file, args);
                return true;
            }
            catch { return false; }
        }

        static string Describe(int frames, int seconds, string path)
        {
            string size = "";
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) size = "   " + FormatSize(fi.Length);
            }
            catch { }

            return string.Format(CultureInfo.InvariantCulture,
                "{0} 帧   {1} 秒{2}", frames, seconds, size);
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        }

        /// <summary>
        /// Shows the result. Single entry point, so the caller never owns the
        /// form's lifetime and cannot leak it.
        /// </summary>
        public static void ShowResult(string path, int frames, int seconds)
        {
            if (string.IsNullOrEmpty(path)) return;
            using (GifResultForm f = new GifResultForm(path, frames, seconds))
                f.ShowDialog();
        }

        /// <summary>Test seam: the path currently displayed.</summary>
        internal string PathForTest { get { return _pathBox.Text; } }

        /// <summary>Test seam: the summary line currently displayed.</summary>
        internal string InfoForTest { get { return _info.Text; } }

        /// <summary>Test seam: the button captions, left to right.</summary>
        internal string[] ButtonTextsForTest
        {
            get { return new string[] { _copyPath.Text, _reveal.Text, _open.Text, _close.Text }; }
        }

        /// <summary>Test seam: the path box, for geometry assertions.</summary>
        internal Rectangle PathBoxBoundsForTest { get { return _pathBox.Bounds; } }

        /// <summary>Test seam: build without showing, so probes can inspect it.</summary>
        internal static GifResultForm CreateForTest(string path, int frames, int seconds)
        {
            return new GifResultForm(path, frames, seconds);
        }
    }
}
