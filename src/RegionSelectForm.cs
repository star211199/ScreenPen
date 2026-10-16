using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>What the user chose in the region selector.</summary>
    public enum RegionAction
    {
        Cancelled = 0,
        SaveToFile = 1,
        CopyToClipboard = 2,
        /// <summary>把选区作为一张置顶贴图钉在桌面上（Snipaste 风格）。</summary>
        PinToDesktop = 3,
        /// <summary>识别选区里的文字（Windows 内置 OCR）。</summary>
        Ocr = 4,
        /// <summary>把选区录制成 GIF 动图。</summary>
        RecordGif = 5
    }

    /// <summary>
    /// Full-screen region picker for screenshots.
    ///
    /// The screen is frozen once into a backdrop, dimmed, and the user drags a
    /// rectangle over it. A toolbar appears next to the selection offering
    /// 保存 / 复制 / 贴图 / 识别文字 / 录制 GIF / 取消, and the same actions are
    /// available on the keyboard: Enter commits the default action, Ctrl+C
    /// copies, P pins to the desktop, O recognises text, G records a GIF and Esc
    /// cancels.
    ///
    /// The selected pixels are cropped from the *frozen backdrop*, not from the
    /// live screen, so the selector's own dimming and toolbar can never end up
    /// inside the screenshot.
    /// </summary>
    public sealed class RegionSelectForm : Form
    {
        readonly Settings _cfg;
        readonly Bitmap _backdrop;          // frozen screen, virtual-desktop sized
        readonly Rectangle _desktop;        // virtual-desktop bounds in screen coords

        bool _dragging;
        Point _anchorClient;
        Point _cursorClient;
        Rectangle _selection;               // client coords, normalised
        bool _hasSelection;

        RegionAction _action = RegionAction.Cancelled;
        Rectangle _chosenScreenRect;

        /// <summary>
        /// What Enter and double-click mean. The caller decides, because the same
        /// selector is used for "save", "copy", "pin to desktop", OCR and GIF
        /// recording; saving stays the default so existing callers behave exactly
        /// as before.
        /// </summary>
        readonly RegionAction _defaultAction;

        /// <summary>
        /// The cropped result, taken the moment the user commits so it survives
        /// this form being disposed.
        /// </summary>
        Bitmap _cropped;

        // ---- toolbar geometry -------------------------------------------------
        // The row is laid out generically over BtnCount actions. Adding an action
        // means adding one label pair and nothing else: the natural width, the
        // short-label fallback, the proportional shrink and the last-resort equal
        // split all work off BtnCount rather than off a hard-coded 4.
        //
        // This replaced a fixed four-button row. The old design deliberately kept
        // OCR and GIF keyboard-only ("a fifth button would not fit"), but the
        // buttons are the discoverable path and the user could not find the
        // features at all - so the row now grows and relies on the shrinking
        // ladder that was already there for narrow desktops.
        const int BtnCount = 6;

        // Order is the paint order and the hit-test order. 取消 stays last so it
        // never sits directly beside the button the cursor rests on.
        const int BtnSave = 0, BtnCopy = 1, BtnPin = 2, BtnOcr = 3, BtnGif = 4, BtnCancel = 5;

        Rectangle[] _btn = new Rectangle[BtnCount];

        // Authored at 100% scaling. A fixed 84 px button cannot hold
        // "复制 (Ctrl+C)" once the font renders at 200%, so every one of these is
        // multiplied by the window DPI and the width is additionally floored at
        // the measured text width. Without that the toolbar silently clipped its
        // own labels on any high-DPI display.
        const int BtnW = 84, BtnH = 32, BtnPad = 8;
        const int BtnInnerPad = 10;
        const int BtnMinWidth = 46;

        Font _fontBtn;
        Font _fontBtnBold;

        // The labels the toolbar is currently painting. Recorded by
        // LayoutToolbar so a test can measure the real strings and widths
        // instead of re-deriving (and possibly mis-deriving) the same decision.
        string[] _btnLabels = new string[BtnCount];
        bool _btnShrunk;

        static readonly Color Accent = Color.FromArgb(255, 232, 160, 0);
        static readonly Color PanelBack = Color.FromArgb(238, 24, 26, 32);
        static readonly Color PanelBorder = Color.FromArgb(255, 90, 97, 112);
        static readonly Color TextMain = Color.FromArgb(255, 240, 242, 245);

        // ---- selection editing ------------------------------------------------
        // Once a region exists the user almost always wants to nudge it, not
        // start over. Dragging inside the region moves it and the eight handles
        // resize it; before this, the only way to fix a region that was a few
        // pixels off was to draw it again from scratch.
        enum SelDrag
        {
            None = 0,
            New,        // drawing a fresh region
            Move,
            N, S, W, E, NW, NE, SW, SE
        }

        SelDrag _selDrag = SelDrag.None;
        Rectangle _selDragOrigin;       // selection as it was when the gesture began
        Point _selDragStart;            // pointer position when the gesture began

        /// <summary>Handle side, authored at 96 DPI; scaled by <see cref="DpiScale"/>.</summary>
        const int HandleSize = 9;

        // Cached soft shadow behind the action toolbar. Built once per panel
        // size; the toolbar only changes size when the window or DPI does.
        Bitmap _toolbarShadow;
        Size _toolbarShadowKey = Size.Empty;
        int _toolbarShadowRadius = -1;

        /// <summary>
        /// Draws the toolbar's cached shadow. The toolbar floats over a frozen
        /// screenshot, so without this it reads as pasted-on rather than lifted
        /// above the content - and on a light screenshot the dark panel has no
        /// perceptible edge at all.
        /// </summary>
        void DrawToolbarShadow(Graphics g, Rectangle bounds, float radius)
        {
            Size key = new Size(bounds.Width, bounds.Height);
            if (_toolbarShadow == null || _toolbarShadowKey != key ||
                _toolbarShadowRadius != (int)radius)
            {
                if (_toolbarShadow != null) { _toolbarShadow.Dispose(); _toolbarShadow = null; }
                _toolbarShadow = OverlayForm.BuildPanelShadow(bounds.Width, bounds.Height, radius);
                if (_toolbarShadow != null)
                {
                    _toolbarShadowKey = key;
                    _toolbarShadowRadius = (int)radius;
                }
            }
            if (_toolbarShadow == null) return;

            g.DrawImageUnscaled(_toolbarShadow,
                bounds.X - OverlayForm.PanelShadowSpread,
                bounds.Y - OverlayForm.PanelShadowSpread);
        }

        public RegionAction ActionTaken { get { return _action; } }

        /// <summary>The chosen region in screen coordinates.</summary>
        public Rectangle ChosenScreenRect { get { return _chosenScreenRect; } }

        /// <summary>
        /// Backwards-compatible constructor: Enter and double-click save to file,
        /// exactly as before this overload existed.
        /// </summary>
        public RegionSelectForm(Settings cfg, Rectangle desktop, Bitmap backdrop)
            : this(cfg, desktop, backdrop, RegionAction.SaveToFile)
        {
        }

        /// <summary>
        /// Primary constructor. <paramref name="defaultAction"/> is what Enter and
        /// double-click commit; the explicit toolbar buttons always win.
        /// </summary>
        public RegionSelectForm(Settings cfg, Rectangle desktop, Bitmap backdrop,
                                RegionAction defaultAction)
        {
            _cfg = cfg;
            _desktop = desktop;
            _backdrop = backdrop;
            // Cancelled would make Enter a no-op, which would look like a broken
            // selector; fall back to saving in that case.
            _defaultAction = defaultAction == RegionAction.Cancelled
                ? RegionAction.SaveToFile : defaultAction;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Bounds = desktop;
            Text = "ScreenPen Region Select";
            Cursor = Cursors.Cross;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TopMost = false;
            TopMost = true;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            Native.SetForegroundWindow(Handle);
            Activate();
            Focus();
        }

        // =====================================================================
        //  Painting
        // =====================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // 1. The frozen screen.
            if (_backdrop != null)
                g.DrawImageUnscaled(_backdrop, 0, 0);

            if (_hasSelection && _selection.Width > 0 && _selection.Height > 0)
            {
                // 2. Punch the selection back to full brightness and dim the rest.
                if (_cfg.RegionDimOutside)
                {
                    Region outside = new Region(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                    outside.Exclude(_selection);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                        g.FillRegion(b, outside);
                    outside.Dispose();
                }

                // 3. Selection border + corner ticks.
                using (Pen p = new Pen(Accent, 1.6f))
                    g.DrawRectangle(p, _selection.X, _selection.Y, _selection.Width, _selection.Height);

                using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 2.4f))
                {
                    int t = 14;
                    g.DrawLine(p, _selection.Left, _selection.Top, _selection.Left + t, _selection.Top);
                    g.DrawLine(p, _selection.Left, _selection.Top, _selection.Left, _selection.Top + t);
                    g.DrawLine(p, _selection.Right, _selection.Top, _selection.Right - t, _selection.Top);
                    g.DrawLine(p, _selection.Right, _selection.Top, _selection.Right, _selection.Top + t);
                    g.DrawLine(p, _selection.Left, _selection.Bottom, _selection.Left + t, _selection.Bottom);
                    g.DrawLine(p, _selection.Left, _selection.Bottom, _selection.Left, _selection.Bottom - t);
                    g.DrawLine(p, _selection.Right, _selection.Bottom, _selection.Right - t, _selection.Bottom);
                    g.DrawLine(p, _selection.Right, _selection.Bottom, _selection.Right, _selection.Bottom - t);
                }

                // 4. Size readout just outside the selection.
                if (_cfg.RegionShowSize)
                    DrawSizeBadge(g);

                // 5. Resize handles, last so they sit above border and ticks.
                DrawSelectionHandles(g);

                LayoutToolbar();
                DrawToolbar(g);
            }
            else
            {
                // Nothing selected yet: dim everything and show the hint.
                if (_cfg.RegionDimOutside)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                        g.FillRectangle(b, 0, 0, ClientSize.Width, ClientSize.Height);
                }
                DrawHint(g);
            }

            DrawCrosshair(g);
        }

        void DrawHint(Graphics g)
        {
            string text = "拖动鼠标框选截图区域    ·    Enter 执行高亮动作    ·    " +
                          "框选后可拖动选区或拉伸控制点调整    ·    Esc 取消";
            using (Font f = new Font("Microsoft YaHei UI", 11f))
            {
                SizeF sz = g.MeasureString(text, f);
                float w = sz.Width + 40, h = sz.Height + 24;
                RectangleF box = new RectangleF((ClientSize.Width - w) / 2f, 40, w, h);
                using (GraphicsPath path = OverlayForm.RoundRect(box, 10))
                {
                    using (SolidBrush b = new SolidBrush(PanelBack)) g.FillPath(b, path);
                    using (Pen p = new Pen(PanelBorder)) g.DrawPath(p, path);
                }
                using (SolidBrush b = new SolidBrush(TextMain))
                    g.DrawString(text, f, b, box.X + 20, box.Y + 12);
            }
        }

        void DrawSizeBadge(Graphics g)
        {
            int w = _selection.Width, h = _selection.Height;
            string text = w + " × " + h;
            using (Font f = new Font("Consolas", 10.5f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(text, f);
                float bw = sz.Width + 20, bh = sz.Height + 12;

                float bx = _selection.Left;
                float by = _selection.Top - bh - 8;
                if (by < 4) by = _selection.Top + 8;             // no room above
                if (bx + bw > ClientSize.Width - 4) bx = ClientSize.Width - bw - 4;
                if (bx < 4) bx = 4;

                RectangleF box = new RectangleF(bx, by, bw, bh);
                using (GraphicsPath path = OverlayForm.RoundRect(box, 7))
                {
                    using (SolidBrush b = new SolidBrush(PanelBack)) g.FillPath(b, path);
                    using (Pen p = new Pen(PanelBorder)) g.DrawPath(p, path);
                }
                using (SolidBrush b = new SolidBrush(Accent))
                    g.DrawString(text, f, b, box.X + 10, box.Y + 6);
            }
        }

        /// <summary>
        /// Current device-pixels-per-96-DPI for this window, computed from the
        /// live handle so it is correct on a per-monitor-DPI setup and does not
        /// depend on the process awareness context having been set.
        /// </summary>
        float DpiScale()
        {
            float s = 1f;
            try
            {
                using (Graphics g = CreateGraphics())
                    s = g.DpiX / 96f;
            }
            catch
            {
            }
            if (s <= 0.1f || s > 8f) s = 1f;
            return s;
        }

        Font BtnFont(bool bold)
        {
            // Point sizes already scale with the monitor DPI once the process is
            // DPI-aware, so no manual multiplication is applied here: only the
            // pixel geometry in LayoutToolbar needs the DpiScale factor.
            if (_fontBtn == null)
            {
                _fontBtn = new Font("Microsoft YaHei UI", 8.6f);
                _fontBtnBold = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold);
            }
            return bold ? _fontBtnBold : _fontBtn;
        }

        int MeasureLabel(string text, bool bold)
        {
            int w = TextRenderer.MeasureText(text, BtnFont(bold),
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            return w + BtnInnerPad * 2;
        }

        // ---- labels ------------------------------------------------------------

        int PrimaryIndex()
        {
            if (_defaultAction == RegionAction.CopyToClipboard) return BtnCopy;
            if (_defaultAction == RegionAction.PinToDesktop) return BtnPin;
            if (_defaultAction == RegionAction.Ocr) return BtnOcr;
            if (_defaultAction == RegionAction.RecordGif) return BtnGif;
            return BtnSave;
        }

        string SaveLabel()
        {
            return _defaultAction == RegionAction.SaveToFile ? "保存 (Enter)" : "保存";
        }

        string CopyLabel()
        {
            return _defaultAction == RegionAction.CopyToClipboard ? "复制 (Enter)" : "复制 (Ctrl+C)";
        }

        string PinLabel()
        {
            return _defaultAction == RegionAction.PinToDesktop ? "贴图 (Enter)" : "贴图 (P)";
        }

        string OcrLabel()
        {
            return _defaultAction == RegionAction.Ocr ? "识别文字 (Enter)" : "识别文字 (O)";
        }

        string GifLabel()
        {
            return _defaultAction == RegionAction.RecordGif ? "录制GIF (Enter)" : "录制GIF (G)";
        }

        string[] LabelsForDefault()
        {
            return new string[] {
                SaveLabel(), CopyLabel(), PinLabel(), OcrLabel(), GifLabel(), "取消 (Esc)"
            };
        }

        static string[] ShortLabels()
        {
            return new string[] { "保存", "复制", "贴图", "识别", "GIF", "取消" };
        }

        /// <summary>The action a toolbar button commits.</summary>
        static RegionAction ActionForButton(int index)
        {
            switch (index)
            {
                case BtnSave: return RegionAction.SaveToFile;
                case BtnCopy: return RegionAction.CopyToClipboard;
                case BtnPin: return RegionAction.PinToDesktop;
                case BtnOcr: return RegionAction.Ocr;
                case BtnGif: return RegionAction.RecordGif;
                default: return RegionAction.Cancelled;
            }
        }

        void LayoutToolbar()
        {
            // Place the toolbar below-right of the selection, flipping when it
            // would fall off-screen. The absolute positions are needed both for
            // painting and hit-testing, so they are computed once here.
            //
            // Every button is wide enough for its own label, so the same row
            // works at 100% and at 200% scaling. On a narrow desktop the row
            // degrades in four documented steps: full labels -> short labels ->
            // every button scaled down together -> every button equal width.
            int pad = Math.Max(4, (int)Math.Round(BtnPad * DpiScale()));
            int btnH = Math.Max(18, (int)Math.Round(BtnH * DpiScale()));
            int primary = PrimaryIndex();

            string[] labels = LabelsForDefault();
            string[] shorts = ShortLabels();

            int maxRow = ClientSize.Width - 12 - pad * 2;
            int[] widths = new int[BtnCount];
            int widest = 0;
            for (int i = 0; i < BtnCount; i++)
            {
                widths[i] = Math.Max(BtnMinWidth, MeasureLabel(labels[i], i == primary));
                if (widths[i] > widest) widest = widths[i];
            }

            // Step 1: if the row does not fit, retry with the short labels.
            bool shrunk = false;
            if (SumWidths(widths) + pad * BtnCount > maxRow)
            {
                for (int i = 0; i < BtnCount; i++)
                    widths[i] = Math.Max(BtnMinWidth, MeasureLabel(shorts[i], i == primary));
                labels = shorts;
                shrunk = true;
            }

            // Step 2: scale every button down together so the row still fits.
            int total = SumWidths(widths);
            int avail = maxRow - pad * BtnCount;
            if (total > avail && total > 0 && avail > 0)
            {
                int shrink = Math.Max(BtnMinWidth,
                    (int)Math.Floor(widest * (double)avail / total));
                for (int i = 0; i < BtnCount; i++) widths[i] = Math.Min(widths[i], shrink);
                shrunk = true;
            }

            // Step 3: even that can overflow on a tiny window; as a last resort
            // make every button equal so the arithmetic still lands on screen.
            if (SumWidths(widths) + pad * BtnCount > maxRow)
            {
                int each = Math.Max(1, (maxRow - pad * BtnCount) / BtnCount);
                for (int i = 0; i < BtnCount; i++) widths[i] = each;
            }

            int totalW = SumWidths(widths) + pad * BtnCount;
            int totalH = btnH + pad * 2;

            int x = _selection.Right - totalW;
            int y = _selection.Bottom + 10;
            if (y + totalH > ClientSize.Height - 6) y = _selection.Top - totalH - 10;
            if (y < 6) y = Math.Min(ClientSize.Height - totalH - 6, _selection.Bottom + 10);
            if (y < 6) y = 6;                       // window shorter than the toolbar
            if (x + totalW > ClientSize.Width - 6) x = ClientSize.Width - totalW - 6;
            if (x < 6) x = 6;                       // even the shrunk row may not fit

            int cx = x + pad;
            for (int i = 0; i < BtnCount; i++)
            {
                _btn[i] = new Rectangle(cx, y + pad, widths[i], btnH);
                cx += widths[i] + pad;
            }

            _btnLabels = labels;
            _btnShrunk = shrunk;
        }

        static int SumWidths(int[] widths)
        {
            int t = 0;
            for (int i = 0; i < widths.Length; i++) t += widths[i];
            return t;
        }

        void DrawToolbar(Graphics g)
        {
            Rectangle bounds = _btn[0];
            for (int i = 1; i < BtnCount; i++) bounds = Rectangle.Union(bounds, _btn[i]);
            bounds.Inflate(ToolbarPad(), ToolbarPad());

            DrawToolbarShadow(g, bounds, 9);

            using (GraphicsPath path = OverlayForm.RoundRect(
                       new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 9))
            {
                using (SolidBrush b = new SolidBrush(PanelBack)) g.FillPath(b, path);
                using (Pen p = new Pen(PanelBorder)) g.DrawPath(p, path);
            }

            // The accent marks whichever action Enter commits, so the highlighted
            // button always describes what the keyboard shortcut will do.
            // _btnLabels is exactly what LayoutToolbar sized each button for, so
            // paint and measurement can never disagree.
            string[] text = _btnLabels != null && _btnLabels.Length == BtnCount
                ? _btnLabels
                : LabelsForDefault();

            int primary = PrimaryIndex();
            for (int i = 0; i < BtnCount; i++)
                DrawToolbarButton(g, _btn[i], text[i], i == primary);
        }

        void DrawToolbarButton(Graphics g, Rectangle r, string text, bool primary)
        {
            using (GraphicsPath path = OverlayForm.RoundRect(
                       new RectangleF(r.X, r.Y, r.Width, r.Height), 6))
            {
                Color back = primary ? Accent : Color.FromArgb(255, 41, 45, 54);
                if (primary && r.Contains(_cursorClient))
                    back = Color.FromArgb(255, 245, 178, 20);
                using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, path);
                if (!primary)
                    using (Pen p = new Pen(PanelBorder)) g.DrawPath(p, path);
            }

            Color fg = primary ? Color.FromArgb(255, 30, 30, 30) : TextMain;
            Font f = BtnFont(primary);

            // Centre the glyphs and clip to the button. LayoutToolbar already
            // sizes every button for its label; the clip is the backstop that
            // keeps a pathological font from spilling over a neighbour.
            Rectangle inner = new Rectangle(r.X + 2, r.Y, Math.Max(1, r.Width - 4), r.Height);
            using (StringFormat sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (SolidBrush b = new SolidBrush(fg))
            {
                Region old = g.Clip;
                try
                {
                    g.SetClip(inner);
                    g.DrawString(text, f, b, inner, sf);
                }
                finally
                {
                    g.Clip = old;
                    old.Dispose();
                }
            }
        }

        void DrawCrosshair(Graphics g)
        {
            if (_dragging) return;
            int x = _cursorClient.X, y = _cursorClient.Y;
            if (x <= 0 && y <= 0) return;
            using (Pen p = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
            {
                g.DrawLine(p, 0, y, ClientSize.Width, y);
                g.DrawLine(p, x, 0, x, ClientSize.Height);
            }
        }

        // =====================================================================
        //  Selection handles
        // =====================================================================

        int HandleSizePx()
        {
            return Math.Max(6, (int)Math.Round(HandleSize * DpiScale()));
        }

        /// <summary>
        /// Handle rectangles in a fixed order: 0=NW 1=W 2=SW 3=N 4=NE 5=E 6=SE 7=S.
        /// </summary>
        Rectangle HandleRect(int index)
        {
            int s = HandleSizePx();
            int half = s / 2;
            Rectangle r = _selection;
            int cx, cy;
            switch (index)
            {
                case 0: cx = r.Left; cy = r.Top; break;
                case 1: cx = r.Left; cy = r.Top + r.Height / 2; break;
                case 2: cx = r.Left; cy = r.Bottom; break;
                case 3: cx = r.Left + r.Width / 2; cy = r.Top; break;
                case 4: cx = r.Right; cy = r.Top; break;
                case 5: cx = r.Right; cy = r.Top + r.Height / 2; break;
                case 6: cx = r.Right; cy = r.Bottom; break;
                default: cx = r.Left + r.Width / 2; cy = r.Bottom; break;
            }
            return new Rectangle(cx - half, cy - half, s, s);
        }

        static SelDrag HandleToDrag(int index)
        {
            switch (index)
            {
                case 0: return SelDrag.NW;
                case 1: return SelDrag.W;
                case 2: return SelDrag.SW;
                case 3: return SelDrag.N;
                case 4: return SelDrag.NE;
                case 5: return SelDrag.E;
                case 6: return SelDrag.SE;
                default: return SelDrag.S;
            }
        }

        /// <summary>
        /// Finds the handle under <paramref name="p"/>. Corners are tested before
        /// edges so that on a small selection - where the handles overlap - the
        /// corner still wins, which is what a user aiming at a corner expects.
        /// </summary>
        bool HitHandle(Point p, out SelDrag which)
        {
            which = SelDrag.None;
            if (!_hasSelection) return false;
            int[] order = new int[] { 0, 2, 4, 6, 1, 3, 5, 7 };
            for (int i = 0; i < order.Length; i++)
            {
                if (HandleRect(order[i]).Contains(p))
                {
                    which = HandleToDrag(order[i]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the resize handles are shown. Below twice the handle size they
        /// would cover the entire region and make it impossible to grab the body
        /// to move it, so they are hidden and only Move stays available.
        /// </summary>
        bool HandlesVisible()
        {
            if (!_hasSelection) return false;
            int s = HandleSizePx();
            return _selection.Width >= s * 2 && _selection.Height >= s * 2;
        }

        void DrawSelectionHandles(Graphics g)
        {
            if (!HandlesVisible()) return;
            int s = HandleSizePx();
            for (int i = 0; i < 8; i++)
            {
                Rectangle r = HandleRect(i);
                using (SolidBrush b = new SolidBrush(Accent)) g.FillRectangle(b, r);
                using (Pen p = new Pen(Color.FromArgb(220, 20, 20, 24), 1f))
                    g.DrawRectangle(p, r);
            }
        }

        /// <summary>Keeps a moved region fully on screen without resizing it.</summary>
        static Rectangle ClampToClient(Rectangle r, Size client)
        {
            if (r.Width > client.Width) r.Width = client.Width;
            if (r.Height > client.Height) r.Height = client.Height;
            if (r.X < 0) r.X = 0;
            if (r.Y < 0) r.Y = 0;
            if (r.Right > client.Width) r.X = client.Width - r.Width;
            if (r.Bottom > client.Height) r.Y = client.Height - r.Height;
            return r;
        }

        /// <summary>
        /// Applies a resize gesture. Edges are clamped individually (rather than
        /// clamping the finished rectangle) so the edge the user is *not* holding
        /// stays put, and so dragging an edge past its opposite simply flips the
        /// region instead of collapsing it.
        /// </summary>
        static Rectangle ResizeFrom(Rectangle origin, SelDrag kind, int dx, int dy, Size client)
        {
            int left = origin.Left, top = origin.Top, right = origin.Right, bottom = origin.Bottom;

            if (kind == SelDrag.W || kind == SelDrag.NW || kind == SelDrag.SW) left = origin.Left + dx;
            if (kind == SelDrag.E || kind == SelDrag.NE || kind == SelDrag.SE) right = origin.Right + dx;
            if (kind == SelDrag.N || kind == SelDrag.NW || kind == SelDrag.NE) top = origin.Top + dy;
            if (kind == SelDrag.S || kind == SelDrag.SW || kind == SelDrag.SE) bottom = origin.Bottom + dy;

            left = Math.Max(0, Math.Min(client.Width, left));
            right = Math.Max(0, Math.Min(client.Width, right));
            top = Math.Max(0, Math.Min(client.Height, top));
            bottom = Math.Max(0, Math.Min(client.Height, bottom));

            return Rectangle.FromLTRB(Math.Min(left, right), Math.Min(top, bottom),
                                      Math.Max(left, right), Math.Max(top, bottom));
        }

        static Cursor CursorForDrag(SelDrag kind)
        {
            switch (kind)
            {
                case SelDrag.NW:
                case SelDrag.SE: return Cursors.SizeNWSE;
                case SelDrag.NE:
                case SelDrag.SW: return Cursors.SizeNESW;
                case SelDrag.N:
                case SelDrag.S: return Cursors.SizeNS;
                case SelDrag.W:
                case SelDrag.E: return Cursors.SizeWE;
                case SelDrag.Move: return Cursors.SizeAll;
                default: return Cursors.Cross;
            }
        }

        void UpdateSelectionCursor(Point p)
        {
            if (_selDrag != SelDrag.None)
            {
                Cursor = CursorForDrag(_selDrag);
                return;
            }
            if (_hasSelection)
            {
                SelDrag hit;
                if (HitHandle(p, out hit)) { Cursor = CursorForDrag(hit); return; }
                if (_selection.Contains(p)) { Cursor = Cursors.SizeAll; return; }
            }
            Cursor = Cursors.Cross;
        }

        // =====================================================================
        //  Mouse
        // =====================================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            HandleLeftDown(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            HandleMouseMove(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            HandleLeftUp(e.Location);
        }

        /// <summary>
        /// Left-button press. Split out of <see cref="OnMouseDown"/> so the
        /// automated probes can drive the real gesture path instead of a
        /// parallel reimplementation of it.
        /// </summary>
        internal void HandleLeftDown(Point p)
        {
            if (_hasSelection)
            {
                // 1. A toolbar button acts immediately.
                for (int i = 0; i < BtnCount; i++)
                {
                    if (_btn[i].Contains(p)) { Finish(ActionForButton(i)); return; }
                }

                // 2. A handle resizes, the body moves. Both take priority over
                //    starting a new selection, so a region that is nearly right
                //    can be corrected instead of drawn again.
                SelDrag hit;
                if (HitHandle(p, out hit)) { BeginGesture(hit, p); return; }
                if (_selection.Contains(p)) { BeginGesture(SelDrag.Move, p); return; }
            }

            // 3. Otherwise start a brand-new selection.
            BeginGesture(SelDrag.New, p);
            _selection = new Rectangle(p, Size.Empty);
            _hasSelection = false;
            Invalidate();
        }

        internal void HandleMouseMove(Point p)
        {
            _cursorClient = p;

            if (_selDrag == SelDrag.None)
            {
                UpdateSelectionCursor(p);
                Invalidate();
                return;
            }

            ApplyGesture(p);
            Invalidate();
        }

        internal void HandleLeftUp(Point p)
        {
            if (_selDrag == SelDrag.None) return;

            ApplyGesture(p);
            _selDrag = SelDrag.None;
            _dragging = false;
            if (_hasSelection) LayoutToolbar();
            UpdateSelectionCursor(p);
            Invalidate();
        }

        void BeginGesture(SelDrag kind, Point p)
        {
            _selDrag = kind;
            _selDragStart = p;
            _selDragOrigin = _selection;
            _anchorClient = p;
            _dragging = true;
            Cursor = CursorForDrag(kind);
        }

        /// <summary>
        /// Recomputes the selection for the gesture in progress. Shared by the
        /// move and the release paths so both land on exactly the same geometry.
        /// </summary>
        void ApplyGesture(Point p)
        {
            if (_selDrag == SelDrag.New)
            {
                _selection = Normalise(_anchorClient, p);
            }
            else if (_selDrag == SelDrag.Move)
            {
                Rectangle r = _selDragOrigin;
                r.Offset(p.X - _selDragStart.X, p.Y - _selDragStart.Y);
                _selection = ClampToClient(r, ClientSize);
            }
            else
            {
                _selection = ResizeFrom(_selDragOrigin, _selDrag,
                    p.X - _selDragStart.X, p.Y - _selDragStart.Y, ClientSize);
            }

            _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // Double-click inside the selection is the fastest path to whatever
            // the caller nominated as its default action.
            if (_hasSelection && _selection.Contains(e.Location))
                Finish(_defaultAction);
        }

        static Rectangle Normalise(Point a, Point b)
        {
            return new Rectangle(
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        // =====================================================================
        //  Keyboard
        // =====================================================================

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Finish(RegionAction.Cancelled); e.Handled = true; return; }
            if (e.KeyCode == Keys.Enter) { Finish(_defaultAction); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.C) { Finish(RegionAction.CopyToClipboard); e.Handled = true; return; }
            // Plain P pins the selection to the desktop (Snipaste's 贴图). The
            // selector only binds Esc, Enter, Ctrl+C, Ctrl+A and the arrows, so a
            // bare P is free; it is checked *after* the Ctrl branches so it can
            // never shadow a future Ctrl+P.
            if (!e.Control && !e.Alt && e.KeyCode == Keys.P) { Finish(RegionAction.PinToDesktop); e.Handled = true; return; }

            // Bare O recognises the text and bare G records a GIF. Both now also
            // have toolbar buttons; the keys stay as the fast path and are
            // checked after the Ctrl branches, so Ctrl+O / Ctrl+G remain free.
            if (!e.Control && !e.Alt && e.KeyCode == Keys.O) { Finish(RegionAction.Ocr); e.Handled = true; return; }
            if (!e.Control && !e.Alt && e.KeyCode == Keys.G) { Finish(RegionAction.RecordGif); e.Handled = true; return; }

            // Arrow keys nudge the pending selection, which makes pixel-exact
            // regions possible without a steady hand. Shift+arrows resize instead
            // of moving, so the same keys cover both adjustments.
            int step = (ModifierKeys & Keys.Shift) == Keys.Shift ? 10 : 1;
            if (_hasSelection)
            {
                bool handled = true;
                bool resize = (ModifierKeys & Keys.Shift) == Keys.Shift;
                if (e.KeyCode == Keys.Left)
                {
                    if (resize) _selection.Width = Math.Max(1, _selection.Width - step);
                    else _selection.Offset(-step, 0);
                }
                else if (e.KeyCode == Keys.Right)
                {
                    if (resize) _selection.Width += step;
                    else _selection.Offset(step, 0);
                }
                else if (e.KeyCode == Keys.Up)
                {
                    if (resize) _selection.Height = Math.Max(1, _selection.Height - step);
                    else _selection.Offset(0, -step);
                }
                else if (e.KeyCode == Keys.Down)
                {
                    if (resize) _selection.Height += step;
                    else _selection.Offset(0, step);
                }
                else handled = false;

                if (handled)
                {
                    // Keep the selection on screen; for a resize this pulls the
                    // anchor edge back rather than letting the region run off.
                    _selection = ClampToClient(_selection, ClientSize);
                    LayoutToolbar();
                    Invalidate();
                    e.Handled = true;
                }
            }

            if (e.KeyCode == Keys.A && e.Control)
            {
                _selection = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                _hasSelection = true;
                LayoutToolbar();
                Invalidate();
                e.Handled = true;
            }
        }

        // =====================================================================
        //  Finish
        // =====================================================================

        void Finish(RegionAction action)
        {
            if (action != RegionAction.Cancelled && (!_hasSelection || _selection.Width < 1))
                action = RegionAction.Cancelled;

            _action = action;

            if (action != RegionAction.Cancelled)
            {
                // Convert client coords back to screen coords. The window covers
                // the whole virtual desktop, so this is a simple offset.
                _chosenScreenRect = new Rectangle(
                    _desktop.Left + _selection.Left,
                    _desktop.Top + _selection.Top,
                    _selection.Width, _selection.Height);

                // Crop *now*, while the form is still alive. The caller reads the
                // result after ShowDialog returns, at which point this window has
                // already been disposed and touching its state is unsafe.
                _cropped = CropRegion(_chosenScreenRect);
            }

            DialogResult = action == RegionAction.Cancelled
                ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Crops the chosen region out of the frozen backdrop, so the selector
        /// chrome is guaranteed not to appear in the result.
        ///
        /// Prefers the copy taken when the action was committed. Falls back to
        /// the live selection so the method is also usable before
        /// <see cref="Finish"/> runs (which is what the tests exercise).
        /// </summary>
        public Bitmap CropSelection()
        {
            if (_cropped != null) return _cropped;

            Rectangle screenRect = _chosenScreenRect;
            if (screenRect.Width <= 0 || screenRect.Height <= 0)
            {
                if (!_hasSelection || _selection.Width < 1 || _selection.Height < 1)
                    return null;
                screenRect = new Rectangle(
                    _desktop.Left + _selection.Left,
                    _desktop.Top + _selection.Top,
                    _selection.Width, _selection.Height);
            }
            return CropRegion(screenRect);
        }

        /// <summary>
        /// Copies a screen-space rectangle out of the frozen backdrop 1:1. The
        /// backdrop holds only real screen pixels, so none of this form's own
        /// dimming, border or toolbar can appear in the result.
        /// </summary>
        Bitmap CropRegion(Rectangle screenRect)
        {
            if (_backdrop == null) return null;

            Rectangle src = screenRect;
            src.Offset(-_desktop.Left, -_desktop.Top);
            src.Intersect(new Rectangle(0, 0, _backdrop.Width, _backdrop.Height));
            if (src.Width <= 0 || src.Height <= 0) return null;

            Bitmap outBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(outBmp))
            {
                // 1:1 crop: nearest neighbour + no pixel offset gives an exact
                // copy with no resampling blur.
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_backdrop, new Rectangle(0, 0, src.Width, src.Height),
                            src, GraphicsUnit.Pixel);
            }
            return outBmp;
        }

        // =====================================================================
        //  Test hooks
        // =====================================================================

        /// <summary>Simulates a drag selection (used by the automated tests).</summary>
        public void SimulateDrag(Point fromClient, Point toClient)
        {
            _anchorClient = fromClient;
            _dragging = true;
            _selection = Normalise(fromClient, toClient);
            _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
            _dragging = false;
            _selDrag = SelDrag.None;
            if (_hasSelection) LayoutToolbar();
            Invalidate();
        }

        /// <summary>Submits an action as if the user picked it (test use).</summary>
        public void SimulateFinish(RegionAction action)
        {
            Finish(action);
        }

        /// <summary>
        /// Clicks a toolbar button by index through the real mouse path, so a
        /// probe exercises the same hit-test the user does rather than calling
        /// Finish directly. Returns false when the index has no button.
        /// </summary>
        public bool ClickToolbarButtonForTest(int index)
        {
            if (index < 0 || index >= BtnCount) return false;
            Rectangle r = _btn[index];
            if (r.Width <= 0 || r.Height <= 0) return false;
            Point c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            HandleLeftDown(c);
            HandleLeftUp(c);
            return true;
        }

        // =====================================================================
        //  Selection geometry (test seams)
        // =====================================================================

        /// <summary>
        /// Presses the left button at a point and moves it, without releasing -
        /// the state a resize or a move is in halfway through.
        /// </summary>
        public void SimulatePressAndDrag(Point fromClient, Point toClient)
        {
            HandleLeftDown(fromClient);
            HandleMouseMove(toClient);
        }

        /// <summary>Releases the left button at a point.</summary>
        public void SimulateRelease(Point atClient)
        {
            HandleLeftUp(atClient);
        }

        /// <summary>What the current gesture would do at this point (test use).</summary>
        public string CursorNameAtForTest(Point p)
        {
            Cursor c = CursorAtForTest(p);
            return c == null ? "(none)" : c.ToString();
        }

        public Rectangle SelectionForTest { get { return _selection; } }
        public bool HasSelectionForTest { get { return _hasSelection; } }

        /// <summary>How many actions the toolbar paints (test use).</summary>
        public int ToolbarButtonCountForTest { get { return BtnCount; } }

        /// <summary>Client rectangle of a toolbar button, for hit-testing tests.</summary>
        public Rectangle ToolbarButtonBoundsForTest(int index)
        {
            if (index < 0 || index >= BtnCount) return Rectangle.Empty;
            return _btn[index];
        }

        public Rectangle SaveButtonBounds { get { return _btn[BtnSave]; } }
        public Rectangle CopyButtonBounds { get { return _btn[BtnCopy]; } }
        /// <summary>Client rectangle of the 贴图 button, for hit-testing tests.</summary>
        public Rectangle PinButtonBounds { get { return _btn[BtnPin]; } }
        /// <summary>Client rectangle of the 识别文字 button.</summary>
        public Rectangle OcrButtonBounds { get { return _btn[BtnOcr]; } }
        /// <summary>Client rectangle of the 录制GIF button.</summary>
        public Rectangle GifButtonBounds { get { return _btn[BtnGif]; } }
        /// <summary>Client rectangle of the cancel button, for hit-testing tests.</summary>
        public Rectangle CancelButtonBounds { get { return _btn[BtnCancel]; } }

        /// <summary>Which button a click at this client point would activate.</summary>
        public int ButtonIndexAtForTest(Point p)
        {
            for (int i = 0; i < BtnCount; i++)
                if (_btn[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>The action Enter and double-click commit, for tests.</summary>
        public RegionAction DefaultActionForTest { get { return _defaultAction; } }

        // The exact strings the toolbar paints, so a test can measure text
        // against the laid-out button width instead of duplicating the labels
        // (a copy would silently drift from the product).
        public string SaveLabelForTest { get { return SaveLabel(); } }
        public string CopyLabelForTest { get { return CopyLabel(); } }
        public string PinLabelForTest { get { return PinLabel(); } }
        public string OcrLabelForTest { get { return OcrLabel(); } }
        public string GifLabelForTest { get { return GifLabel(); } }
        public string CancelLabelForTest { get { return "取消 (Esc)"; } }

        /// <summary>
        /// The labels LayoutToolbar actually sized the buttons for, in
        /// save/copy/pin/ocr/gif/cancel order. Null before the first layout.
        /// </summary>
        public string[] ToolbarLabelsForTest
        {
            get
            {
                return _btnLabels == null ? null : (string[])_btnLabels.Clone();
            }
        }

        /// <summary>
        /// True when the row did not fit at its natural width and was shrunk or
        /// shortened, which is the documented small-desktop fallback. Tests only
        /// require the labels to fit when this is false.
        /// </summary>
        public bool ToolbarShrunkForTest { get { return _btnShrunk; } }

        /// <summary>Toolbar padding in device pixels at the current DPI.</summary>
        int ToolbarPad()
        {
            return Math.Max(4, (int)Math.Round(BtnPad * DpiScale()));
        }

        /// <summary>Whole toolbar row, so tests can assert it stays on screen.</summary>
        public Rectangle ToolbarBoundsForTest
        {
            get
            {
                Rectangle r = _btn[0];
                for (int i = 1; i < BtnCount; i++) r = Rectangle.Union(r, _btn[i]);
                r.Inflate(ToolbarPad(), ToolbarPad());
                return r;
            }
        }

        /// <summary>Number of resize handles the selector exposes (test use).</summary>
        public int HandleCountForTest { get { return 8; } }

        /// <summary>Client rectangle of a resize handle (test use).</summary>
        public Rectangle HandleBoundsForTest(int index)
        {
            if (index < 0 || index >= 8) return Rectangle.Empty;
            return HandleRect(index);
        }

        /// <summary>Handle side in device pixels at the current DPI (test use).</summary>
        /// <summary>Test seam: whether the resize handles are currently painted.</summary>
        public bool HandlesVisibleForTest { get { return HandlesVisible(); } }

        /// <summary>Test seam: the resize handles' size in device pixels.</summary>
        public int HandleSizeForTest { get { return HandleSizePx(); } }

        /// <summary>The cursor the selector would show at this point (test use).</summary>
        public Cursor CursorAtForTest(Point p)
        {
            if (_hasSelection)
            {
                SelDrag hit;
                if (HitHandle(p, out hit)) return CursorForDrag(hit);
                if (_selection.Contains(p)) return Cursors.SizeAll;
            }
            return Cursors.Cross;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fontBtn != null) { _fontBtn.Dispose(); _fontBtn = null; }
                if (_fontBtnBold != null) { _fontBtnBold.Dispose(); _fontBtnBold = null; }
                if (_toolbarShadow != null) { _toolbarShadow.Dispose(); _toolbarShadow = null; }
            }
            base.Dispose(disposing);
        }
    }
}
