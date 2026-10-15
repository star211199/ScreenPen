using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// 贴图窗口：把一张截图“钉”在桌面上的独立置顶小窗口（Snipaste 风格的贴图）。
    ///
    /// 注意与 <c>Config.Pin</c> / <c>OverlayForm._pins</c> 的区别：那一套是画布内
    /// 的一次性冻结快照，随画布生命周期结束；本窗体是一个**独立的、无边框的桌面
    /// 窗口**，不属于任何父窗口。可以同时打开多个，各自拖动、缩放、关闭，互不影响。
    ///
    /// 缩放策略：**按基准尺寸重算**，不使用 Form.Scale。
    /// 原因是 Form.Scale 会把比例累乘到窗口及其子项上，重复调用会因取整而逐次漂移，
    /// 而且缩放结果无法精确回退。这里只保存原始位图尺寸 _baseSize，每次缩放都把
    /// ClientSize 重算为 _baseSize * _scale，并在 OnPaint 里按目标矩形重绘位图，
    /// 因此缩放比例永远精确、可逆，也不会累积误差。
    /// </summary>
    public sealed class PinForm : Form
    {
        /// <summary>缩放下限：再小就看不清内容了。</summary>
        public const float MinScale = 0.1f;
        /// <summary>缩放上限：避免一张小图被放大到整屏都是马赛克。</summary>
        public const float MaxScale = 8.0f;

        const int CloseSize = 22;       // 关闭按钮边长（约 22 × 22）
        const int CloseMargin = 2;      // 距窗体右上角的内边距
        const int MinVisible = 40;      // 至少要有这么多像素留在工作区内

        /// <summary>
        /// 被贴的位图。本窗体**拥有**它：由构造方移交，Dispose 时释放。
        /// 非只读，因为 Dispose 后要置空以避免重复释放。
        /// </summary>
        Bitmap _image;

        /// <summary>原始（未缩放）尺寸，即 100% 比例下的客户区大小。</summary>
        readonly Size _baseSize;

        /// <summary>当前缩放比例，初始为 1.0（原始大小）。</summary>
        float _scale = 1.0f;

        bool _dragging;
        Point _dragCursorScreen;        // 上一次拖动时的屏幕光标位置
        bool _mouseInside;              // 鼠标是否在窗体内，决定是否画关闭按钮
        Point _cursorClient;
        bool _closed;

        ToolTip _tip;                   // 懒创建：非模态提示，替代 MessageBox

        static readonly Color BorderColor = Color.FromArgb(170, 205, 214, 228);
        static readonly Color BackColorDark = Color.FromArgb(255, 24, 26, 32);

        // =====================================================================
        //  构造
        // =====================================================================

        /// <summary>
        /// 主构造函数：image 的所有权立即移交本窗体（Dispose 时释放），
        /// screenRect 是期望的屏幕位置（尺寸仍由图片决定）。
        /// </summary>
        public PinForm(Bitmap image, Rectangle screenRect)
        {
            _image = image;
            // 允许 image 为 null（探针会这么用）：没有图也要能画出一块底板，
            // 绝不能因为空引用把异常抛出构造函数之外。
            _baseSize = image != null ? image.Size : new Size(240, 160);
            _scale = 1.0f;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;                  // Esc / Ctrl+C 必须由窗体自己收到
            BackColor = BackColorDark;
            Text = "ScreenPen 贴图";
            Cursor = Cursors.SizeAll;
            MinimizeBox = false;
            MaximizeBox = false;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            Size sz = ScaledSizeFor(_scale, WorkingAreaFor(screenRect));
            ClientSize = sz;
            Location = ClampLocation(new Point(screenRect.Left, screenRect.Top), sz);
        }

        /// <summary>
        /// 探针用的简化构造：不给矩形，自己挑一个位置（主屏工作区居中）。
        /// 两参数版本才是正式入口。
        /// </summary>
        public PinForm(Bitmap image)
            : this(image, DefaultScreenRect(image))
        {
        }

        static Rectangle DefaultScreenRect(Bitmap image)
        {
            Size sz = image != null ? image.Size : new Size(240, 160);
            Rectangle wa = WorkingAreaFor(Rectangle.Empty);
            return new Rectangle(
                wa.Left + Math.Max(0, (wa.Width - sz.Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - sz.Height) / 2),
                sz.Width, sz.Height);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 无边框窗口在 Show() 之后未必拿到焦点，而 Esc / Ctrl+C 依赖焦点。
            try { Activate(); }
            catch (Exception) { }
        }

        // =====================================================================
        //  几何：尺寸、工作区、位置
        // =====================================================================

        /// <summary>
        /// 找到矩形（或点）所在显示器的工作区。任何异常都不能让调用方崩溃，
        /// 最差也退回到主屏工作区。
        /// </summary>
        static Rectangle WorkingAreaFor(Rectangle r)
        {
            try { return Screen.FromRectangle(r).WorkingArea; }
            catch (Exception) { }
            try { return SystemInformation.WorkingArea; }
            catch (Exception) { }
            return new Rectangle(0, 0, 1024, 768);
        }

        /// <summary>
        /// 由基准尺寸和比例算出客户区大小：先按比例重算（保证不累积取整误差），
        /// 再限制到工作区内，避免放大后的窗口比屏幕还大而无法操作。
        /// </summary>
        Size ScaledSizeFor(float scale, Rectangle workArea)
        {
            int w = (int)Math.Round(_baseSize.Width * scale);
            int h = (int)Math.Round(_baseSize.Height * scale);
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            if (workArea.Width > 0 && w > workArea.Width) w = workArea.Width;
            if (workArea.Height > 0 && h > workArea.Height) h = workArea.Height;
            return new Size(w, h);
        }

        /// <summary>
        /// 把位置夹回“至少有一部分留在工作区里”。窗口一旦被拖出屏幕就再也点不到，
        /// 对无边框窗口来说这是不可恢复的状态，所以位置必须收敛。
        /// </summary>
        static Point ClampLocation(Point p, Size sz)
        {
            Rectangle wa = WorkingAreaFor(new Rectangle(p, sz));
            int keepX = Math.Min(MinVisible, Math.Max(1, sz.Width));
            int keepY = Math.Min(MinVisible, Math.Max(1, sz.Height));

            int x = p.X, y = p.Y;
            if (x + sz.Width < wa.Left + keepX) x = wa.Left + keepX - sz.Width;
            if (x > wa.Right - keepX) x = wa.Right - keepX;
            if (y + sz.Height < wa.Top + keepY) y = wa.Top + keepY - sz.Height;
            if (y > wa.Bottom - keepY) y = wa.Bottom - keepY;
            return new Point(x, y);
        }

        /// <summary>关闭按钮的矩形；窗口很小时按比例收窄，始终留在窗体内。</summary>
        public Rectangle CloseButtonBounds
        {
            get
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w <= 2 || h <= 2) return Rectangle.Empty;

                int cw = Math.Min(CloseSize, Math.Max(1, w - CloseMargin * 2));
                int ch = Math.Min(CloseSize, Math.Max(1, h - CloseMargin * 2));
                return new Rectangle(w - cw - CloseMargin, CloseMargin, cw, ch);
            }
        }

        // =====================================================================
        //  绘制
        // =====================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            // 先铺底色：位图可能被释放或从未提供，OnPaint 绝不允许抛异常。
            using (SolidBrush back = new SolidBrush(BackColor))
                g.FillRectangle(back, 0, 0, w, h);

            if (_image != null)
            {
                try
                {
                    if (w == _baseSize.Width && h == _baseSize.Height)
                    {
                        // 1:1 时用最近邻 + Half 偏移，得到逐像素精确的拷贝。
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                    }
                    else
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    }
                    g.DrawImage(_image, new Rectangle(0, 0, w, h));
                }
                catch (Exception)
                {
                    // 位图在使用中被释放等情况：保持底色即可，绝不冒泡。
                }
            }

            // 1 像素描边：让图像边缘在同色桌面上也能看出来。
            if (w > 1 && h > 1)
            {
                using (Pen p = new Pen(BorderColor, 1f))
                    g.DrawRectangle(p, 0, 0, w - 1, h - 1);
            }

            DrawCloseButton(g);
        }

        /// <summary>
        /// 关闭按钮只在鼠标位于窗体内时出现：贴图大部分时间应该是纯图像，
        /// 常驻的按钮会挡住内容。
        /// </summary>
        void DrawCloseButton(Graphics g)
        {
            if (!_mouseInside) return;

            Rectangle r = CloseButtonBounds;
            if (r.Width <= 0 || r.Height <= 0) return;

            bool hot = r.Contains(_cursorClient);
            using (GraphicsPath path = RoundedRect(
                       new RectangleF(r.X, r.Y, r.Width, r.Height), 4f))
            {
                using (SolidBrush b = new SolidBrush(hot
                           ? Color.FromArgb(235, 206, 60, 52)
                           : Color.FromArgb(185, 30, 33, 40)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(190, 235, 238, 242), 1f))
                    g.DrawPath(p, path);
            }

            using (Font f = new Font("Segoe UI", r.Height >= 18 ? 9f : 7f, FontStyle.Bold))
            using (StringFormat sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 245, 246, 248)))
                g.DrawString("✕", f, b, r, sf);
        }

        /// <summary>
        /// 本文件自带的圆角矩形，刻意不依赖 OverlayForm.RoundRect：贴图窗口是
        /// 新功能，尽量少牵连正在被其它改动触碰的文件。
        /// </summary>
        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // =====================================================================
        //  鼠标：拖动 / 滚轮缩放 / 关闭
        // =====================================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // 右键直接关闭；必须在左键拖动逻辑之前返回，否则会被拖动分支吃掉。
            if (e.Button == MouseButtons.Right)
            {
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 点在关闭按钮上：关闭，且**不**进入拖动状态。
            Rectangle close = CloseButtonBounds;
            if (close.Width > 0 && close.Contains(e.Location))
            {
                Close();
                return;
            }

            _dragging = true;
            _dragCursorScreen = Cursor.Position;
            try { Capture = true; }
            catch (Exception) { }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            _cursorClient = e.Location;
            _mouseInside = true;

            if (_dragging)
            {
                // 用光标的屏幕坐标增量移动窗口。若改用 e.Location 相对按下点的
                // 差值，窗口自己在动，差值会随之归零，拖动会中途卡死。
                Point cur = Cursor.Position;
                int dx = cur.X - _dragCursorScreen.X;
                int dy = cur.Y - _dragCursorScreen.Y;
                if (dx != 0 || dy != 0)
                {
                    _dragCursorScreen = cur;
                    DragBy(dx, dy);
                }
                return;
            }

            Invalidate();   // 关闭按钮的高亮状态
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            _dragging = false;
            try { Capture = false; }
            catch (Exception) { }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _mouseInside = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // 拖动中（鼠标被捕获）离开窗体不算“不悬停”，否则按钮会闪。
            if (!_dragging) _mouseInside = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta == 0) return;

            // 每格 120：1.12 的幂，滚轮连滚时是平滑的等比缩放。
            float factor = (float)Math.Pow(1.12, e.Delta / 120.0);
            ApplyScale(_scale * factor, e.Location);
        }

        /// <summary>拖动与测试共用的唯一移动实现。</summary>
        void DragBy(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;      // 只点击不移动：窗口原地不动
            Location = new Point(Left + dx, Top + dy);
        }

        // =====================================================================
        //  缩放
        // =====================================================================

        /// <summary>
        /// 以 anchorClient（客户区坐标）为锚点缩放到 newScale。
        ///
        /// 采用“重算 ClientSize”而不是 Form.Scale：比例只乘在基准尺寸上，
        /// 不会随缩放次数漂移。锚点按“光标下的图像比例保持不变”换算，因此滚轮
        /// 缩放时光标下的那一点基本不动（贴边被夹回工作区时会略有偏差）。
        /// </summary>
        void ApplyScale(float newScale, Point anchorClient)
        {
            if (newScale < MinScale) newScale = MinScale;
            if (newScale > MaxScale) newScale = MaxScale;

            Size oldSize = ClientSize;
            if (Math.Abs(newScale - _scale) < 0.0005f && oldSize.Width > 0)
                return;      // 比例没变（例如已到上下限），不做无谓的重排

            float fx = oldSize.Width > 0 ? (float)anchorClient.X / oldSize.Width : 0.5f;
            float fy = oldSize.Height > 0 ? (float)anchorClient.Y / oldSize.Height : 0.5f;
            if (fx < 0f) fx = 0f; if (fx > 1f) fx = 1f;
            if (fy < 0f) fy = 0f; if (fy > 1f) fy = 1f;

            int anchorScreenX = Left + anchorClient.X;
            int anchorScreenY = Top + anchorClient.Y;

            Size sz = ScaledSizeFor(newScale, WorkingAreaFor(new Rectangle(Location, oldSize)));

            try
            {
                _scale = newScale;
                ClientSize = sz;
                // 让锚点在屏幕上原地不动：新的左上角 = 锚点屏幕位置 - 比例 × 新尺寸。
                Point moved = new Point(
                    anchorScreenX - (int)Math.Round(fx * sz.Width),
                    anchorScreenY - (int)Math.Round(fy * sz.Height));
                Location = ClampLocation(moved, sz);
                Invalidate();
            }
            catch (Exception)
            {
                // 尺寸/位置设置失败也不能把异常抛给输入事件处理链。
            }
        }

        // =====================================================================
        //  键盘
        // =====================================================================

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopyImageToClipboard();
                e.Handled = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.S)
            {
                SaveImage();
                e.Handled = true;
                return;
            }
        }

        /// <summary>
        /// 复制到剪贴板。这里只做“尽力而为”：无边框窗口弹模态框会把整个程序卡住，
        /// 所以失败时只用 ToolTip 提示。
        /// </summary>
        void CopyImageToClipboard()
        {
            if (_image == null) return;

            try
            {
                // using：SetImage/SetDataObject 都是复制像素，放完即可释放。
                using (Bitmap copy = new Bitmap(_image.Width, _image.Height,
                                                PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(_image, 0, 0);

                    try { Clipboard.SetImage(copy); return; }
                    catch (Exception) { }
                    try { Clipboard.SetDataObject(copy, true); return; }
                    catch (Exception) { }
                }
                ShowTip("复制失败：剪贴板可能被其它程序占用。");
            }
            catch (Exception)
            {
                // 位图已释放等情况：静默失败，绝不抛出。
            }
        }

        /// <summary>Ctrl+S：保存当前贴图。这是用户主动发起的对话框，可以正常模态。</summary>
        void SaveImage()
        {
            if (_image == null) return;

            try
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "保存贴图";
                    sfd.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|BMP 图片 (*.bmp)|*.bmp";
                    sfd.FileName = "ScreenPen_Pin_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    sfd.DefaultExt = "png";
                    sfd.AddExtension = true;
                    sfd.OverwritePrompt = true;

                    if (sfd.ShowDialog(this) != DialogResult.OK) return;

                    string ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                    ImageFormat fmt = ImageFormat.Png;
                    if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                    else if (ext == ".bmp") fmt = ImageFormat.Bmp;

                    // JPEG/BMP 没有 alpha，先压到实色底上，避免出现黑块。
                    using (Bitmap flat = new Bitmap(_image.Width, _image.Height,
                                                    PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(flat))
                        {
                            g.Clear(Color.Black);
                            g.DrawImageUnscaled(_image, 0, 0);
                        }
                        flat.Save(sfd.FileName, fmt);
                    }
                    ShowTip("已保存：" + sfd.FileName);
                }
            }
            catch (Exception)
            {
                ShowTip("保存失败：无法写入该位置。");
            }
        }

        /// <summary>非模态提示，用来替代 MessageBox。</summary>
        void ShowTip(string text)
        {
            try
            {
                if (_tip == null) _tip = new ToolTip();
                _tip.Show(text, this, 8, 8, 1800);
            }
            catch (Exception) { }
        }

        // =====================================================================
        //  生命周期
        // =====================================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closed = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dragging = false;

                // 只在这里释放 _image，而且只在 disposing == true 时：
                // 这样本方法就是**唯一**的释放点。若构造函数在中途抛异常，所有权仍在
                // 调用方手上，而 Component 的终结器会用 disposing == false 再走一遍
                // 本方法，那时释放就会和调用方重复释放同一张位图。重复调用 Dispose()
                // 也是安全的：第二遍 _image 已经是 null。
                if (_image != null)
                {
                    try { _image.Dispose(); }
                    catch (Exception) { }
                    _image = null;
                }
                if (_tip != null)
                {
                    try { _tip.Dispose(); }
                    catch (Exception) { }
                    _tip = null;
                }
            }
            base.Dispose(disposing);
        }

        // =====================================================================
        //  Test seams（自动化探针用；探针不得释放 TestImage）
        // =====================================================================

        /// <summary>当前缩放比例。</summary>
        public float TestScale { get { return _scale; } }

        /// <summary>原始（未缩放）尺寸。</summary>
        public Size TestBaseSize { get { return _baseSize; } }

        /// <summary>本窗体拥有的原始位图。探针只读，不要释放。</summary>
        public Bitmap TestImage { get { return _image; } }

        /// <summary>窗口是否已经关闭（关闭中或被释放都算）。</summary>
        public bool TestIsClosed { get { return _closed || IsDisposed; } }

        /// <summary>与关闭状态同义的公开属性。</summary>
        public bool IsClosed { get { return TestIsClosed; } }

        /// <summary>
        /// 按滚轮/快捷键同一条路径设置比例，锚点取客户区中心。
        /// 锚点与滚轮不同（滚轮跟随光标），但缩放实现完全一致。
        /// </summary>
        public void TestSetScale(float s)
        {
            Point anchor = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            ApplyScale(s, anchor);
        }

        /// <summary>模拟滚轮：delta 与 MouseEventArgs.Delta 同义（每格 120）。</summary>
        public void TestWheelZoom(int delta, Point anchorClient)
        {
            if (delta == 0) return;
            float factor = (float)Math.Pow(1.12, delta / 120.0);
            ApplyScale(_scale * factor, anchorClient);
        }

        /// <summary>执行与拖动处理完全相同的位移。</summary>
        public void TestDragBy(int dx, int dy)
        {
            DragBy(dx, dy);
        }
    }
}
