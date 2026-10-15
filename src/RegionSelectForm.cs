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
        PinToDesktop = 3
    }

    /// <summary>
    /// Full-screen region picker for screenshots.
    ///
    /// The screen is frozen once into a backdrop, dimmed, and the user drags a
    /// rectangle over it. A small toolbar appears next to the selection offering
    /// 保存 / 复制 / 贴图 / 取消, and the same actions are available on the
    /// keyboard: Enter commits the default action, Ctrl+C copies, P pins to the
    /// desktop and Esc cancels.
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
        /// selector is used for "save", "copy" and "pin to desktop"; saving stays
        /// the default so existing callers behave exactly as before.
        /// </summary>
        readonly RegionAction _defaultAction;

        /// <summary>
        /// The cropped result, taken the moment the user commits so it survives
        /// this form being disposed.
        /// </summary>
        Bitmap _cropped;

        // ---- toolbar geometry -------------------------------------------------
        Rectangle _btnSave, _btnCopy, _btnPin, _btnCancel;

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
        string[] _btnLabels = new string[4];
        bool _btnShrunk;

        static readonly Color Accent = Color.FromArgb(255, 232, 160, 0);
        static readonly Color PanelBack = Color.FromArgb(238, 24, 26, 32);
        static readonly Color PanelBorder = Color.FromArgb(255, 90, 97, 112);
        static readonly Color TextMain = Color.FromArgb(255, 240, 242, 245);

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
            string text = "拖动鼠标框选截图区域    ·    Enter 保存/复制/贴图    ·    Ctrl+C 复制    ·    P 贴图    ·    Esc 取消";
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

        void LayoutToolbar()
        {
            // Place the toolbar below-right of the selection, flipping when it
            // would fall off-screen. The absolute positions are needed both for
            // painting and hit-testing, so they are computed once here.
            //
            // The row is right-aligned to the selection and holds four buttons
            // (保存 / 复制 / 贴图 / 取消). Each button is wide enough for its own
            // label, so the same row works at 100% and at 200% scaling, and on a
            // narrow desktop the whole row shrinks by dropping the keyboard hint
            // before it is ever allowed to run off the edge.
            int pad = Math.Max(4, (int)Math.Round(BtnPad * DpiScale()));
            int btnH = Math.Max(18, (int)Math.Round(BtnH * DpiScale()));

            string[] labels = new string[] {
                SaveLabel(), CopyLabel(), PinLabel(), "取消 (Esc)"
            };
            string[] shorts = new string[] { "保存", "复制", "贴图", "取消" };

            int maxRow = ClientSize.Width - 12 - pad * 2;
            int[] widths = new int[4];
            int widest = 0;
            for (int i = 0; i < 4; i++)
            {
                widths[i] = Math.Max(BtnMinWidth, MeasureLabel(labels[i], i == 2));
                if (widths[i] > widest) widest = widths[i];
            }

            int total = 0;
            for (int i = 0; i < 4; i++) total += widths[i];

            // Step 1: if the row does not fit, retry with the short labels.
            bool shrunk = false;
            if (total + pad * 4 > maxRow)
            {
                for (int i = 0; i < 4; i++)
                    widths[i] = Math.Max(BtnMinWidth, MeasureLabel(shorts[i], i == 2));
                labels = shorts;
                shrunk = true;
            }

            // Step 2: scale every button down together so the row still fits.
            total = 0;
            for (int i = 0; i < 4; i++) total += widths[i];
            int avail = maxRow - pad * 4;
            if (total > avail && total > 0 && avail > 0)
            {
                int shrink = Math.Max(BtnMinWidth,
                    (int)Math.Floor(widest * (double)avail / total));
                for (int i = 0; i < 4; i++) widths[i] = Math.Min(widths[i], shrink);
                shrunk = true;
            }

            // Step 3: even that can overflow on a tiny window; as a last resort
            // make every button equal so the arithmetic still lands on screen.
            total = 0;
            for (int i = 0; i < 4; i++) total += widths[i];
            if (total + pad * 4 > maxRow)
            {
                int each = Math.Max(1, (maxRow - pad * 4) / 4);
                for (int i = 0; i < 4; i++) widths[i] = each;
            }

            total = 0;
            for (int i = 0; i < 4; i++) total += widths[i];
            int totalW = total + pad * 4;
            int totalH = btnH + pad * 2;

            int x = _selection.Right - totalW;
            int y = _selection.Bottom + 10;
            if (y + totalH > ClientSize.Height - 6) y = _selection.Top - totalH - 10;
            if (y < 6) y = Math.Min(ClientSize.Height - totalH - 6, _selection.Bottom + 10);
            if (y < 6) y = 6;                       // window shorter than the toolbar
            if (x + totalW > ClientSize.Width - 6) x = ClientSize.Width - totalW - 6;
            if (x < 6) x = 6;                       // even the shrunk row may not fit

            _btnSave = new Rectangle(x + pad, y + pad, widths[0], btnH);
            _btnCopy = new Rectangle(_btnSave.Right + pad, y + pad, widths[1], btnH);
            _btnPin = new Rectangle(_btnCopy.Right + pad, y + pad, widths[2], btnH);
            _btnCancel = new Rectangle(_btnPin.Right + pad, y + pad, widths[3], btnH);

            _btnLabels = labels;
            _btnShrunk = shrunk;
        }

        void DrawToolbar(Graphics g)
        {
            Rectangle bounds = Rectangle.Union(
                Rectangle.Union(_btnSave, _btnCopy),
                Rectangle.Union(_btnPin, _btnCancel));
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
            string[] text = _btnLabels != null && _btnLabels.Length == 4
                ? _btnLabels
                : new string[] { SaveLabel(), CopyLabel(), PinLabel(), "取消 (Esc)" };

            DrawToolbarButton(g, _btnSave, text[0],
                              _defaultAction == RegionAction.SaveToFile);
            DrawToolbarButton(g, _btnCopy, text[1],
                              _defaultAction == RegionAction.CopyToClipboard);
            DrawToolbarButton(g, _btnPin, text[2],
                              _defaultAction == RegionAction.PinToDesktop);
            DrawToolbarButton(g, _btnCancel, text[3], false);
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
        //  Mouse
        // =====================================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            // Clicking a toolbar button acts immediately.
            if (_hasSelection)
            {
                if (_btnSave.Contains(e.Location)) { Finish(RegionAction.SaveToFile); return; }
                if (_btnCopy.Contains(e.Location)) { Finish(RegionAction.CopyToClipboard); return; }
                if (_btnPin.Contains(e.Location)) { Finish(RegionAction.PinToDesktop); return; }
                if (_btnCancel.Contains(e.Location)) { Finish(RegionAction.Cancelled); return; }
            }

            _dragging = true;
            _anchorClient = e.Location;
            _cursorClient = e.Location;
            _selection = new Rectangle(e.Location, Size.Empty);
            _hasSelection = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cursorClient = e.Location;

            if (_dragging)
            {
                _selection = Normalise(_anchorClient, e.Location);
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_dragging)
            {
                _dragging = false;
                _selection = Normalise(_anchorClient, e.Location);
                _hasSelection = _selection.Width >= 2 && _selection.Height >= 2;
                if (_hasSelection) LayoutToolbar();
                Invalidate();
            }
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

            // Arrow keys nudge the pending selection, which makes pixel-exact
            // regions possible without a steady hand.
            int step = (ModifierKeys & Keys.Shift) == Keys.Shift ? 10 : 1;
            if (_hasSelection)
            {
                bool moved = true;
                if (e.KeyCode == Keys.Left) _selection.Offset(-step, 0);
                else if (e.KeyCode == Keys.Right) _selection.Offset(step, 0);
                else if (e.KeyCode == Keys.Up) _selection.Offset(0, -step);
                else if (e.KeyCode == Keys.Down) _selection.Offset(0, step);
                else moved = false;

                if (moved)
                {
                    // Keep the selection on screen.
                    _selection.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
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
            if (_hasSelection) LayoutToolbar();
            Invalidate();
        }

        /// <summary>Submits an action as if the user picked it (test use).</summary>
        public void SimulateFinish(RegionAction action)
        {
            Finish(action);
        }

        public Rectangle SelectionForTest { get { return _selection; } }
        public bool HasSelectionForTest { get { return _hasSelection; } }
        /// <summary>Client rectangle of the save button, for hit-testing tests.</summary>
        public Rectangle SaveButtonBounds { get { return _btnSave; } }
        public Rectangle CopyButtonBounds { get { return _btnCopy; } }
        /// <summary>Client rectangle of the 贴图 button, for hit-testing tests.</summary>
        public Rectangle PinButtonBounds { get { return _btnPin; } }
        /// <summary>Client rectangle of the cancel button, for hit-testing tests.</summary>
        public Rectangle CancelButtonBounds { get { return _btnCancel; } }
        /// <summary>The action Enter and double-click commit, for tests.</summary>
        public RegionAction DefaultActionForTest { get { return _defaultAction; } }

        // The exact strings the toolbar paints, so a test can measure text
        // against the laid-out button width instead of duplicating the labels
        // (a copy would silently drift from the product).
        public string SaveLabelForTest { get { return SaveLabel(); } }
        public string CopyLabelForTest { get { return CopyLabel(); } }
        public string PinLabelForTest { get { return PinLabel(); } }
        public string CancelLabelForTest { get { return "取消 (Esc)"; } }

        /// <summary>
        /// The labels LayoutToolbar actually sized the buttons for, in
        /// save/copy/pin/cancel order. Null before the first layout.
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
                Rectangle r = Rectangle.Union(
                    Rectangle.Union(_btnSave, _btnCopy),
                    Rectangle.Union(_btnPin, _btnCancel));
                r.Inflate(ToolbarPad(), ToolbarPad());
                return r;
            }
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
