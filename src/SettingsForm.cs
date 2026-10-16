using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Visual settings window, styled after the reference desktop app:
    /// dark card layout, a left navigation rail and flat accent buttons.
    ///
    /// It edits a *clone* of the live settings so Cancel is genuinely
    /// non-destructive, and only reports back on OK.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        // ---- palette --------------------------------------------------------
        static readonly Color Bg = Color.FromArgb(255, 24, 26, 32);
        static readonly Color Card = Color.FromArgb(255, 34, 37, 45);
        static readonly Color CardAlt = Color.FromArgb(255, 41, 45, 54);
        static readonly Color Border = Color.FromArgb(255, 56, 61, 72);
        static readonly Color TextMain = Color.FromArgb(255, 236, 238, 242);
        static readonly Color TextSub = Color.FromArgb(255, 148, 156, 172);
        static readonly Color Accent = Color.FromArgb(255, 232, 160, 0);

        readonly Settings _cfg;
        readonly Func<Keys, bool> _hotkeyTester;
        Settings _draft;

        // ---- live controls --------------------------------------------------
        Panel _nav;
        Panel _content;
        readonly List<Button> _navButtons = new List<Button>();
        readonly Dictionary<string, Panel> _pages = new Dictionary<string, Panel>();
        string _currentPage = "快捷键";

        HotkeyBox _hkToggle, _hkLive, _hkShot, _hkCopyShot, _hkUndo, _hkSave, _hkCopy;
        HotkeyBox _hkZoomIn, _hkZoomOut;
        HotkeyBox _hkPen, _hkLine, _hkRect, _hkEllipse, _hkArrow;
        HotkeyBox _hkText, _hkMosaic, _hkSpotlight, _hkClear, _hkFollow;
        HotkeyBox _hkHighlighter, _hkStep, _hkRedo;
        HotkeyBox _hkWindowShot, _hkPicker;
        HotkeyBox _hkOcr, _hkGif;
        // OCR
        CheckBox _chkOcrCopy, _chkOcrLines;
        NumericUpDown _numOcrUpscale;
        ComboBox _cmbOcrLang;
        // GIF
        NumericUpDown _numGifFps, _numGifMax, _numGifCountdown;
        CheckBox _chkGifBorder, _chkGifLoop, _chkGifSkip;

        /// <summary>
        /// Every configurable key in dialog order. It is built while the pages
        /// are constructed and then drives BOTH the live readouts and the
        /// save-time validation, so the two can never disagree. (They used to:
        /// the readout happily said "单键可用" for a key that the save handler
        /// then rejected as a duplicate.)
        /// </summary>
        readonly List<KeyBinding> _bindings = new List<KeyBinding>();
        readonly Dictionary<HotkeyBox, Label> _readouts = new Dictionary<HotkeyBox, Label>();

        /// <summary>
        /// The box the user touched most recently; it wins a clash.
        /// </summary>
        HotkeyBox _lastTouched;
        bool _loading;

        /// <summary>
        /// Test seam. When set, message boxes are answered by this delegate
        /// instead of being shown.
        ///
        /// This exists because an automated probe that drives the real dialog
        /// would otherwise pop modal windows onto the user's desktop and hang
        /// waiting for a click that never comes. Tests must not be able to
        /// disturb the machine they run on.
        /// </summary>
        public Func<string, string, MessageBoxButtons, DialogResult> PromptOverride;

        /// <summary>Raised instead of showing a message box when overridden.</summary>
        DialogResult Prompt(string text, string caption, MessageBoxButtons buttons,
                            MessageBoxIcon icon)
        {
            if (PromptOverride != null) return PromptOverride(text, caption, buttons);
            return MessageBox.Show(this, text, caption, buttons, icon);
        }

        Panel _paletteHost;

        // ---------------------------------------------------------------------
        //  Test seams
        //
        //  The dialog is driven by probes that must not open modal windows or
        //  touch the user's saved settings. These expose just enough to check
        //  that the OCR/GIF controls are bound in both directions.
        // ---------------------------------------------------------------------

        /// <summary>Builds the dialog for a probe without showing it.</summary>
        internal static SettingsForm CreateForTest(Settings cfg)
        {
            return new SettingsForm(cfg, delegate { return true; });
        }

        internal string OcrHotkeyForTest { get { return _hkOcr.Text; } }
        internal string GifHotkeyForTest { get { return _hkGif.Text; } }
        internal int GifFpsForTest { get { return (int)_numGifFps.Value; } }
        internal int OcrUpscaleForTest { get { return (int)_numOcrUpscale.Value; } }
        internal string OcrLanguageForTest { get { return SelectedOcrLanguage(); } }
        internal int OcrLanguageCountForTest { get { return _cmbOcrLang.Items.Count; } }
        internal bool OcrCopyForTest { get { return _chkOcrCopy.Checked; } }

        internal void SetGifFpsForTest(int v) { _numGifFps.Value = Clamp(v, _numGifFps); }
        internal void SetOcrUpscaleForTest(int v) { _numOcrUpscale.Value = Clamp(v, _numOcrUpscale); }
        internal void SetGifCountdownForTest(int v) { _numGifCountdown.Value = Clamp(v, _numGifCountdown); }
        internal void SetOcrCopyForTest(bool v) { _chkOcrCopy.Checked = v; }

        /// <summary>
        /// Runs the dialog's real save path and returns the resulting settings.
        ///
        /// OnSave both validates and persists, so a probe must restore the
        /// settings file afterwards; the wiring probe does exactly that.
        /// </summary>
        internal Settings RunSaveForTest()
        {
            OnSave();
            return _draft;
        }

        // Footer chrome, kept so the buttons can be re-laid-out once the form
        // has its real width.
        Panel _footer;
        Button _btnOk, _btnCancel, _btnReset;

        // Header chrome, kept so the subtitle can be re-placed after the DPI
        // scale pass (the title's rendered height depends on the monitor DPI).
        Panel _header;
        PictureBox _headerLogo;
        Label _headerTitle, _headerSub;
        NumericUpDown _numWidth, _numMin, _numMax, _numStep;
        NumericUpDown _numTextSize, _numMosaic, _numBrushSize;
        Label _widthPreview;
        TrackBar _widthTrack;
        CheckBox _chkSmooth, _chkMagnifier;
        NumericUpDown _numMinZoom, _numMaxZoom;
        CheckBox _chkRegionDim, _chkRegionSize;
        CheckBox _chkModCtrl, _chkModTab, _chkModShift, _chkModCtrlShift;
        CheckBox _chkBrushCursor, _chkScrollFollow, _chkWheelScroll, _chkClickThrough;

        CheckBox _chkStartup, _chkTray, _chkMinimized;
        CheckBox _chkAutoSave, _chkSound;
        TextBox _txtFolder;
        ComboBox _cmbFormat;

        // Tier-1 additions.
        NumericUpDown _numHighlightAlpha, _numStepBadge, _numPickerLoupe, _numPickerZoom;
        ComboBox _cmbArrowStyle;
        CheckBox _chkStepPersistent;
        CheckBox _chkBeautify, _chkBeautifyShadow;
        NumericUpDown _numBeautifyPad, _numBeautifyRadius;
        Button _btnBeautifyColor1, _btnBeautifyColor2;

        Label _status;

        public Settings Result { get; private set; }

        public SettingsForm(Settings cfg, Func<Keys, bool> hotkeyTester)
        {
            _cfg = cfg;
            _hotkeyTester = hotkeyTester;
            _draft = cfg.Clone();

            Text = "ScreenPen 设置 - 屏幕画笔";
            // Sizable since the "panel cannot be resized by hand" fix. The
            // dialog is still placed with explicit pixel bounds and does not
            // reflow (that is what keeps the card column and every measured
            // child stable), so extra width stays background and a narrower
            // window scrolls - but the user can now drag an edge or maximize
            // instead of being locked to one size. MinimumSize, which used to
            // be pointless under FixedSingle, is set from ApplyMinimumSize().
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            // AutoScaleMode.None is deliberate: every control below is placed
            // with explicit pixel bounds, and the default Font-based scaling
            // would rescale those bounds a second time (shrinking the whole
            // layout and clipping Chinese text).
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Bg;
            ForeColor = TextMain;
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = IconLibrary.App;
            DoubleBuffered = true;

            BuildChrome();
            BuildPages();
            ShowPage("快捷键");
            LoadDraftIntoControls();

            // Size is applied *after* the control tree exists. Setting it
            // earlier lets docked/auto-scrolled children reflow the form back
            // down to their preferred size. MinimumSize is deliberately set
            // last (inside ApplyDpiScale) so it can never clamp this ClientSize.
            ClientSize = new Size(DialogWidth, DialogHeight);

            // Now that the real width is known, place the footer buttons. Doing
            // it here (rather than in BuildChrome) is what keeps them on screen
            // and clear of the nav panel.
            LayoutFooter();

            // Last step: reconcile the 96-DPI design above with the DPI the
            // monitor actually runs at. See ApplyDpiScale() for why this cannot
            // be left to AutoScaleMode.
            ApplyDpiScale();

            // Only now - with the final size known - is a floor meaningful.
            ApplyMinimumSize();

            // From here on a resize is a user action, so re-place the anchored
            // footer and re-measure the page extents. Before this point OnResize
            // fires during construction, when the tree is still half-built.
            _ready = true;
        }

        /// <summary>
        /// True once the control tree, the authored ClientSize and the DPI pass
        /// are all done, i.e. once a WM_SIZE can only come from the user.
        /// </summary>
        bool _ready;

        /// <summary>
        /// Authored 96-DPI floor for the client area. Wide enough that the nav
        /// rail (212px), the footer buttons (~322px including the 18px right pad)
        /// and the status text still fit without overlapping.
        /// </summary>
        const int MinClientWidth = 560;

        /// <summary>
        /// Authored 96-DPI floor for the client area: header + a card + footer.
        /// </summary>
        const int MinClientHeight = 460;

        /// <summary>
        /// Applies the resize floor, converted from the authored 96-DPI metrics
        /// to the running DPI, plus the window's own non-client frame.
        ///
        /// MinimumSize is set here rather than in the constructor body on
        /// purpose: it clamps Size, so assigning it before ClientSize would
        /// shrink the freshly authored (and possibly DPI-scaled) layout.
        /// </summary>
        void ApplyMinimumSize()
        {
            Size minClient = ScaleSize(new Size(MinClientWidth, MinClientHeight));
            int frameW = Math.Max(0, Width - ClientSize.Width);
            int frameH = Math.Max(0, Height - ClientSize.Height);
            MinimumSize = new Size(minClient.Width + frameW, minClient.Height + frameH);
        }

        /// <summary>
        /// Converts an authored 96-DPI size to the running DPI. The authored
        /// metrics in this file are pixels at 96 DPI; _dpiScale is 1.0 unless a
        /// high-DPI monitor was detected (see ApplyDpiScale).
        /// </summary>
        Size ScaleSize(Size authored)
        {
            if (_dpiScale <= 1.01f) return authored;
            return new Size(
                (int)Math.Round(authored.Width * _dpiScale),
                (int)Math.Round(authored.Height * _dpiScale));
        }

        /// <summary>
        /// DPI factor the layout was built at; 1.0f until ApplyDpiScale runs.
        /// </summary>
        float _dpiScale = 1f;

        /// <summary>
        /// Keeps the window chrome correct while the user drags an edge or
        /// maximizes.
        ///
        /// The dialog deliberately does NOT reflow: the card column, the header
        /// and every measured child keep the size the DPI pass gave them, so
        /// widening the window adds background rather than stretching the cards
        /// (re-running Scale() here would be wrong twice over - it is not
        /// idempotent, and the card widths are asserted against the DPI factor).
        /// Only the two pieces that are genuinely width-dependent are redone:
        /// the anchored footer buttons and each page's scrollable extent.
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_ready) return;
            LayoutFooter();
            RefreshScrollExtents();
        }

        /// <summary>
        /// Places the logo, title and subtitle in the header.
        ///
        /// Everything is derived from measured text heights rather than guessed
        /// offsets, and it is re-run after the DPI scale pass: at 192 DPI a 13pt
        /// title is ~37px tall and an 8.5pt subtitle ~30px, so a header fixed at
        /// 64px would clip the subtitle. The panel grows to fit instead.
        /// </summary>
        void LayoutHeader()
        {
            if (_header == null || _headerTitle == null) return;

            const int padX = 18;
            int logoSize = 36;
            int titleH = TextRenderer.MeasureText(_headerTitle.Text, _headerTitle.Font).Height;
            int subH = TextRenderer.MeasureText(_headerSub.Text, _headerSub.Font).Height;
            int gap = 2;
            int padY = 8;

            // The logo is square and 36px at the authored scale; after scaling it
            // may be larger, so keep whichever is bigger and re-derive the text
            // column from it.
            if (_headerLogo != null && _headerLogo.Width > logoSize)
                logoSize = _headerLogo.Width;

            int textX = padX + logoSize + 10;

            int blockH = titleH + gap + subH;
            int headerH = Math.Max(blockH + padY * 2, logoSize + padY * 2);

            _header.Height = headerH;

            if (_headerLogo != null)
            {
                _headerLogo.Bounds = new Rectangle(
                    padX, (headerH - logoSize) / 2, logoSize, logoSize);
            }

            int y = (headerH - blockH) / 2;
            _headerTitle.Location = new Point(textX, y);
            _headerSub.Location = new Point(textX + 2, y + titleH + gap);
        }

        /// <summary>
        /// Reconciles the 96 DPI design above with the DPI the monitor actually
        /// runs at (2.0 on a 200% display).
        ///
        /// This dialog deliberately uses <see cref="AutoScaleMode.None"/> and
        /// hand-placed pixel bounds. That is fine for geometry, but it silently
        /// breaks text: WinForms converts a point size to pixels using the monitor
        /// DPI, so a 9pt font renders ~31px tall at 192 DPI while every hard-coded
        /// box stays at its 96 DPI size (a 32px button). The result is exactly the
        /// "文字被遮挡" the user kept reporting - captions clipped through the
        /// middle.
        ///
        /// WinForms' own AutoScaleMode cannot fix it here: measured on a 192 DPI
        /// display, AutoScaleMode.Dpi and .Font both leave ClientSize, control
        /// bounds and font size untouched on this form (they only rescale when
        /// AutoScaleDimensions differs from the design-time value, which the
        /// explicit AutoScaleMode.None suppresses). Scaling explicitly is the only
        /// reliable route.
        /// </summary>
        void ApplyDpiScale()
        {
            float s = SystemDpiScale();
            _dpiScale = s;
            if (s <= 1.01f) return;   // already at the authored scale

            // Pages other than the visible one are deliberately detached from
            // _content (see ShowPage: they are Dock=Fill siblings, so a hidden
            // page left parented would paint over the selected one). Scale()
            // walks the *parented* tree only, so those detached pages would keep
            // their 96 DPI geometry - measured: the 画笔 card stayed 780px wide
            // inside a 1880px page while the visible page doubled correctly.
            //
            // So: detach EVERY page first, let the form scale its own chrome,
            // then scale each page exactly once by hand. Detaching first is what
            // prevents the visible page from being scaled twice.
            foreach (Panel p in _pages.Values)
            {
                if (_content.Controls.Contains(p)) _content.Controls.Remove(p);
            }

            // Control.Scale walks the tree and multiplies Bounds,
            // MinimumSize/MaximumSize, Padding and Margin by the factor. It does
            // NOT touch Font, which is exactly what is wanted: the font is
            // already rendering at the correct physical size for this DPI, so
            // only the boxes around it need to grow.
            Scale(new SizeF(s, s));

            foreach (Panel p in _pages.Values)
            {
                p.Scale(new SizeF(s, s));
            }

            // Re-derive card heights now that the boxes are scaled.
            //
            // FinishPage() measured them at 96 DPI, and Scale() then multiplied
            // those heights verbatim. That is wrong for any card whose text
            // wraps: the description label's MaximumSize *width* is scaled too,
            // so the same sentence needs fewer lines at 192 DPI - while the
            // height reserved for it was based on the 96 DPI line count. The
            // result was a card up to twice as tall as its content, which read
            // as a large empty band under the heading.
            ReflowCardHeights();

            // Re-attach the page that was showing.
            ShowPage(_currentPage);

            // The header's height was authored for 96 DPI and the scale pass
            // cannot know the subtitle needs more room, so re-derive it.
            LayoutHeader();

            // The footer buttons were anchored, and anchoring follows the width
            // change, but their sizes were also scaled; re-place them so the
            // right-hand padding is exact.
            LayoutFooter();

            // Scroll extents are a plain Size property, and whether Scale()
            // propagates into AutoScrollMinSize is an implementation detail not
            // worth depending on. Recompute them from the (now scaled) children
            // so every page still scrolls to its full content.
            RefreshScrollExtents();

            // Keep the window inside the working area if doubling it overflowed.
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (Width > wa.Width || Height > wa.Height)
            {
                int w = Math.Min(Width, wa.Width);
                int h = Math.Min(Height, wa.Height);
                ClientSize = new Size(
                    Math.Max(320, w - (Width - ClientSize.Width)),
                    Math.Max(240, h - (Height - ClientSize.Height)));
                LayoutFooter();
            }
        }

        /// <summary>
        /// Re-measures every page's scrollable extent from its children.
        ///
        /// Safe to call repeatedly; used after the DPI scale pass, which may or
        /// may not have adjusted AutoScrollMinSize on its own.
        /// </summary>
        void RefreshScrollExtents()
        {
            foreach (Panel page in _pages.Values)
            {
                int bottom = 0;
                foreach (Control c in page.Controls)
                    if (c.Bottom > bottom) bottom = c.Bottom;
                page.AutoScrollMinSize = new Size(0, bottom + PagePadY);
            }
        }

        /// <summary>
        /// The primary monitor's DPI divided by 96. Falls back to 1.0 (the
        /// authored scale) if the query is unavailable, so a failure degrades to
        /// today's layout rather than to a broken one.
        /// </summary>
        static float SystemDpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float d = g.DpiX;
                    if (d <= 0) return 1f;
                    return d / 96f;
                }
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>Bottom padding below the last card on a page.</summary>
        const int PagePadY = 8;

        /// <summary>
        /// Places the footer action buttons flush against the right edge.
        ///
        /// Called after ClientSize is applied. The buttons are also Anchored so
        /// a later resize keeps them correct, but the initial position must be
        /// computed here: anchoring only preserves the offset a control already
        /// has, and at build time that offset was measured against the wrong
        /// width.
        /// </summary>
        void LayoutFooter()
        {
            if (_footer == null || _btnOk == null) return;

            const int pad = 18;         // gap from the right edge
            const int gap = 10;         // gap between buttons

            int right = _footer.ClientSize.Width - pad;

            _btnOk.Left = right - _btnOk.Width;
            right = _btnOk.Left - gap;

            _btnCancel.Left = right - _btnCancel.Width;
            right = _btnCancel.Left - gap;

            _btnReset.Left = right - _btnReset.Width;

            _btnOk.Top = _btnCancel.Top = _btnReset.Top = 14;

            // The status text runs from the left; if it would reach the buttons,
            // shorten it rather than letting the two overlap.
            if (_status != null)
            {
                int available = _btnReset.Left - _status.Left - 12;
                _status.AutoSize = false;
                _status.AutoEllipsis = true;
                _status.Width = Math.Max(80, available);
                _status.Height = 22;
                _status.Top = 21;
            }
        }

        // =====================================================================
        //  Dialog metrics
        // =====================================================================

        /// <summary>
        /// Dialog width. Deliberately generous: the previous 880 px window left
        /// the card column too narrow, so Chinese descriptions wrapped into the
        /// controls underneath them.
        /// </summary>
        const int DialogWidth = 1180;

        /// <summary>
        /// Dialog height. Tall enough for the longest page (快捷键) to show every
        /// card without scrolling or clipping.
        /// </summary>
        const int DialogHeight = 830;

        // =====================================================================
        //  Chrome: header, nav rail, footer
        // =====================================================================

        void BuildChrome()
        {
            // ---- header ----
            Panel header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Card };
            header.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Border))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            Controls.Add(header);

            PictureBox logo = new PictureBox
            {
                Image = IconLibrary.Resize(IconLibrary.App.ToBitmap(), 36),
                SizeMode = PictureBoxSizeMode.StretchImage,
                Bounds = new Rectangle(18, 14, 36, 36),
                BackColor = Color.Transparent
            };
            header.Controls.Add(logo);

            Label title = new Label
            {
                Text = "ScreenPen",
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(64, 12),
                BackColor = Color.Transparent
            };
            header.Controls.Add(title);

            Label sub = new Label
            {
                Text = "屏幕画笔 · 实时缩放 · 截图",
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = TextSub,
                AutoSize = true,
                Location = new Point(66, 40),
                BackColor = Color.Transparent
            };
            header.Controls.Add(sub);

            _header = header;
            _headerLogo = logo;
            _headerTitle = title;
            _headerSub = sub;
            LayoutHeader();

            // ---- footer ----
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Card };
            footer.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Border))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };
            Controls.Add(footer);

            _status = new Label
            {
                ForeColor = TextSub,
                AutoSize = true,
                Location = new Point(20, 21),
                BackColor = Color.Transparent,
                Text = "提示：设置保存在 EXE 同目录的 .settings.json，绿色免安装。"
            };
            footer.Controls.Add(_status);

            // The three action buttons are anchored to the RIGHT edge instead of
            // being positioned from footer.Width here. At this point the form
            // still has its default width, because ClientSize is deliberately
            // applied last; computing "footer.Width - 210" now put every button
            // at x=180-384, i.e. underneath the 212px nav panel, where they were
            // clipped out of sight. Anchoring lets the layout engine keep them
            // pinned to the real right edge when the window is finally sized.
            _btnCancel = MakeButton("取消", false);
            _btnCancel.Size = new Size(88, 32);
            _btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(_btnCancel);

            _btnOk = MakeButton("保存设置", true);
            _btnOk.Size = new Size(96, 32);
            _btnOk.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnOk.Click += (s, e) => OnSave();
            footer.Controls.Add(_btnOk);

            _btnReset = MakeButton("恢复默认", false);
            _btnReset.Size = new Size(100, 32);
            _btnReset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnReset.Click += (s, e) => ResetDefaults();
            footer.Controls.Add(_btnReset);

            _footer = footer;

            // ---- left nav ----
            // Buttons are positioned explicitly rather than docked: Dock=Top
            // combined with z-order changes makes the visual order reverse and
            // reliably breaks hit-testing.
            _nav = new Panel { Dock = DockStyle.Left, Width = 212, BackColor = Bg };
            Controls.Add(_nav);

            Label navTitle = new Label
            {
                Text = "设置",
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = TextSub,
                Location = new Point(20, 16),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _nav.Controls.Add(navTitle);

            // ---- content host ----
            // AutoScroll is off here because each page scrolls itself (see
            // NewPage). Leaving it on as well would give the dialog two nested
            // vertical scrollbars for the same content.
            _content = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Bg,
                Padding = new Padding(14, 14, 14, 8),
                AutoScroll = false
            };
            Controls.Add(_content);
            _content.BringToFront();
        }

        /// <summary>
        /// Adds one navigation entry.
        ///
        /// Text-only on purpose. The previous version prefixed each label with an
        /// emoji (🖌 🔍 📷) inside a 166px button; emoji come from a fallback font
        /// whose advance width GDI+ badly overestimates, so the label needed
        /// 159-180px in a 156px box and every entry was clipped down to its first
        /// character ("快" instead of "快捷键"). Rather than tune padding until it
        /// barely fits, the icons are gone: the active page is marked by a card
        /// background, an accent-coloured label, and a painted accent bar. That
        /// removes the whole font-fallback failure mode.
        /// </summary>
        void AddNav(string name)
        {
            int index = _navButtons.Count;
            Button b = new Button
            {
                Text = name,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(22, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(10, 44 + index * 46, NavButtonWidth, 40),
                BackColor = Color.Transparent,
                ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 10f),
                Cursor = Cursors.Hand,
                Tag = name,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = CardAlt;
            b.Click += (s, e) => ShowPage(name);
            b.Paint += NavButtonPaint;
            _nav.Controls.Add(b);
            b.BringToFront();
            _navButtons.Add(b);
        }

        /// <summary>Width of one navigation button.</summary>
        const int NavButtonWidth = 192;

        /// <summary>
        /// Draws the 3px accent bar on the left edge of the selected entry.
        /// Button.Paint fires after the background and text are drawn, so this
        /// only adds the bar.
        /// </summary>
        void NavButtonPaint(object sender, PaintEventArgs e)
        {
            Button b = sender as Button;
            if (b == null) return;
            if ((string)b.Tag != _currentPage) return;

            using (SolidBrush br = new SolidBrush(Accent))
                e.Graphics.FillRectangle(br, 0, 6, 3, b.Height - 12);
        }

        void ShowPage(string name)
        {
            _currentPage = name;
            Panel target;
            bool found = _pages.TryGetValue(name, out target);

            // Pages must not remain parented while hidden: they are all
            // Dock=Fill siblings occupying the same rectangle, so a later-added
            // page paints over the selected one. Detach every other page and
            // attach only the target.
            foreach (KeyValuePair<string, Panel> kv in _pages)
            {
                if (kv.Value != target && _content.Controls.Contains(kv.Value))
                    _content.Controls.Remove(kv.Value);
            }
            if (found && target != null)
            {
                if (!_content.Controls.Contains(target)) _content.Controls.Add(target);
                target.Visible = true;
                target.BringToFront();
            }

            foreach (Button b in _navButtons)
            {
                bool on = (string)b.Tag == name;
                b.BackColor = on ? Card : Color.Transparent;
                b.ForeColor = on ? Accent : TextSub;
                b.Font = new Font("Microsoft YaHei UI", 10f, on ? FontStyle.Bold : FontStyle.Regular);
                b.Invalidate();     // repaint the accent bar
            }
        }

        /// <summary>
        /// Creates a page holding an inner card column.
        ///
        /// AutoScroll is ON. An earlier version had it off, reasoning that the
        /// window was fixed-size and every page fit. Measurement disproved that:
        /// the 快捷键 page needs 1477px of cards in a 684px viewport, and
        /// 画笔/截图/常规 overflowed too, so their last cards were simply
        /// unreachable. Scrolling is the only fix that keeps working as content
        /// grows, and a ScrollableControl also handles the mouse wheel itself.
        ///
        /// The page keeps Dock=Fill and cards keep explicit Y offsets: the card
        /// column (780px + 8px pad) is far narrower than the viewport, so the
        /// scrollbar that appears cannot force a horizontal scrollbar.
        /// </summary>
        Panel NewPage(string name)
        {
            // Cards are parented straight to the page: an extra container Panel
            // adds no value here and brings a class of clipping/docking bugs.
            Panel page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Bg,
                AutoScroll = true,
                Visible = false
            };

            _pages[name] = page;
            _currentBuildingPage = page;
            _cardCursorY = 0;
            _cards = new List<Panel>();
            _cardContentTop = 52;

            // Cards are stacked with explicit Y offsets. Docking inside a
            // Dock=Fill host is fragile: children can land in zero-height slots
            // and never repaint. This dialog is fixed-size, so absolute
            // placement is simpler and reliably painted.
            //
            // Heights are provisional: FinishPage() measures each card's real
            // content afterwards, so a card can never be too short for its text.
            _cardHost = delegate(Panel card)
            {
                card.Dock = DockStyle.None;
                card.Left = PagePadX;
                card.Width = CardWidth;
                card.Top = _cardCursorY;
                page.Controls.Add(card);
                card.BringToFront();
                _cards.Add(card);
            };
            return page;
        }

        /// <summary>Width of the card column, in pixels.</summary>
        const int CardWidth = 780;

        /// <summary>Left padding of the card column inside a page.</summary>
        const int PagePadX = 8;

        /// <summary>Vertical gap between two cards.</summary>
        const int CardGap = 14;

        /// <summary>Next free Y inside the current page's card column.</summary>
        int _cardCursorY;

        /// <summary>Cards of the page currently being built, in creation order.</summary>
        List<Panel> _cards = new List<Panel>();

        /// <summary>
        /// Y offset inside the current card at which its content may start,
        /// derived from the real measured height of the heading and description.
        /// Page builders use this instead of hard-coded offsets so a long
        /// description can never overlap the controls below it.
        /// </summary>
        int _cardContentTop = 52;

        /// <summary>Bottom margin reserved inside a card below its content.</summary>
        const int CardBottomPad = 16;

        /// <summary>
        /// Sizes every card of the page to fit its children and re-stacks them.
        ///
        /// This is what makes the dialog robust: the previous version used
        /// hard-coded card heights, so a description that wrapped to three lines
        /// — or a font that rendered taller than assumed — drew straight over the
        /// controls underneath. Measuring after the fact cannot be wrong.
        /// </summary>
        void FinishPage()
        {
            int y = 0;
            foreach (Panel card in _cards)
            {
                int needed = 0;
                foreach (Control c in card.Controls)
                {
                    int bottom = c.Bottom + c.Margin.Bottom;
                    if (bottom > needed) needed = bottom;
                }
                needed += CardBottomPad;

                // Never shrink below the height the author asked for, so a card
                // that is mostly empty keeps the intended breathing room.
                int authored = card.Tag is int ? (int)card.Tag : 0;
                card.Height = Math.Max(Math.Max(needed, authored), 60);
                card.Top = y;
                y += card.Height + CardGap;
            }
            _cardCursorY = y;

            // Give the page an explicit virtual height so AutoScroll knows how
            // far it can scroll. Without this the page keeps the docked
            // viewport height and the cards past the fold stay unreachable.
            // AutoScrollMinSize rather than Size: the page is Dock=Fill, so its
            // real height is owned by the layout engine; only the *scrollable*
            // extent should be larger.
            Panel page = _currentBuildingPage;
            if (page != null)
                page.AutoScrollMinSize = new Size(0, y);

            _cards = new List<Panel>();
        }

        /// <summary>
        /// Re-measures and re-stacks every card on every page after the DPI
        /// scale pass, and rewrites each page's scroll extent.
        ///
        /// Why this is needed: FinishPage() runs during BuildPages(), i.e.
        /// before ApplyDpiScale(). At that moment a description Label's
        /// MaximumSize width is still the authored 96 DPI value while the text
        /// itself already renders at the monitor's real DPI, so the label wraps
        /// to far more lines than it will actually need. FinishPage() faithfully
        /// reserves room for those lines, and Scale() then multiplies that
        /// generous height verbatim - producing cards with a large empty band
        /// under the heading once the widened label collapsed back to its real
        /// line count.
        ///
        /// Re-measuring here, against the scaled geometry, is the only place the
        /// true wrapped height is knowable.
        /// </summary>
        void ReflowCardHeights()
        {
            foreach (Panel page in _pages.Values)
            {
                // Cards were added Dock=None with explicit Top offsets, so the
                // authored stacking order is recovered by sorting on Top - which
                // Scale() has already multiplied along with everything else.
                List<Panel> cards = new List<Panel>();
                foreach (Control c in page.Controls)
                {
                    Panel card = c as Panel;
                    if (card != null) cards.Add(card);
                }
                cards.Sort(delegate(Panel a, Panel b) { return a.Top.CompareTo(b.Top); });

                int y = 0;
                foreach (Panel card in cards)
                {
                    int needed = 0;
                    foreach (Control c in card.Controls)
                    {
                        int bottom = c.Bottom + c.Margin.Bottom;
                        if (bottom > needed) needed = bottom;
                    }
                    needed += CardBottomPad;

                    // The authored floor is a 96 DPI figure, so it scales too.
                    int authored = card.Tag is int
                        ? (int)Math.Round((int)card.Tag * _dpiScale) : 0;

                    card.Height = Math.Max(Math.Max(needed, authored), 60);
                    card.Top = y;
                    y += card.Height + (int)Math.Round(CardGap * _dpiScale);
                }

                page.AutoScrollMinSize = new Size(0, y + PagePadY);
            }
        }

        /// <summary>The page whose cards are currently being built.</summary>
        Panel _currentBuildingPage;

        // =====================================================================
        //  Reusable widgets
        // =====================================================================

        static Button MakeButton(string text, bool primary)
        {
            Button b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : CardAlt,
                ForeColor = primary ? Color.FromArgb(255, 30, 30, 30) : TextMain,
                Font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary
                ? Color.FromArgb(255, 245, 178, 20) : Border;
            return b;
        }

        /// <summary>
        /// Width a button needs to show <paramref name="text"/> without clipping
        /// its caption, including the border/inset padding WinForms adds.
        /// </summary>
        static int ButtonWidthFor(string text, Font font)
        {
            // FlatStyle buttons inset the caption; 22px covers the standard
            // 3px border plus the ~8px text margin on each side.
            return TextRenderer.MeasureText(text, font).Width + 22;
        }

        /// <summary>
        /// Grows <paramref name="b"/> to fit its caption, never shrinking it
        /// below its current width or <paramref name="minWidth"/>.
        ///
        /// Several buttons were authored with a hand-picked width that was too
        /// small for their Chinese caption ("恢复默认颜色" in 130px), so the text
        /// was cut off mid-character.
        /// </summary>
        static Button FitButton(Button b, int minWidth)
        {
            int need = ButtonWidthFor(b.Text, b.Font);
            b.Width = Math.Max(Math.Max(minWidth, need), 24);
            return b;
        }

        /// <summary>
        /// Lays out a horizontal row of buttons left to right, each sized to its
        /// own caption, with a uniform gap. Replaces hand-picked x offsets and
        /// widths, which is how the palette row ended up with captions wider
        /// than their buttons.
        /// </summary>
        static void LayoutButtonRow(int x, int y, int gap, int height, params Button[] row)
        {
            foreach (Button b in row)
            {
                FitButton(b, 88);
                b.Height = height;
                b.Left = x;
                b.Top = y;
                x = b.Right + gap;
            }
        }

        /// <summary>
        /// Creates a titled card. The heading and description are laid out with
        /// measured positions and <see cref="_cardContentTop"/> is set so page
        /// builders can place their controls below the description without
        /// guessing at its height.
        /// </summary>
        Panel Card2(string heading, string description, int height)
        {
            Panel card = new Panel
            {
                // Remember the authored height as a floor; FinishPage() may grow
                // it, never shrink it.
                Tag = height,
                Height = height,
                BackColor = Card,
                Padding = new Padding(16, 12, 16, 12),
                Margin = new Padding(0, 0, 0, 12)
            };
            // Explicitly fill the rounded rect rather than relying on BackColor:
            // this makes the card self-contained and immune to a sibling or
            // parent painting over the same region.
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath gp = OverlayForm.RoundRect(
                           new RectangleF(0.5f, 0.5f, card.Width - 1, card.Height - 1), 8))
                {
                    using (SolidBrush fill = new SolidBrush(Card))
                        e.Graphics.FillPath(fill, gp);
                    using (Pen p = new Pen(Border))
                        e.Graphics.DrawPath(p, gp);
                }
            };
            _cardHost(card);

            int y = 12;

            Label h = new Label
            {
                Text = heading,
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent
            };
            card.Controls.Add(h);
            // Use the rendered height rather than the font's nominal line height.
            y += h.PreferredHeight + 4;

            if (!string.IsNullOrEmpty(description))
            {
                Label d = new Label
                {
                    Text = description,
                    Font = new Font("Microsoft YaHei UI", 9f),
                    ForeColor = TextSub,
                    AutoSize = true,
                    // 40 px less than the card width: 16 px padding each side
                    // plus a little slack so text never touches the border.
                    MaximumSize = new Size(CardWidth - 48, 0),
                    Location = new Point(16, y),
                    BackColor = Color.Transparent
                };
                card.Controls.Add(d);
                y += d.PreferredHeight + 10;
            }
            else
            {
                y += 6;
            }

            _cardContentTop = y;
            return card;
        }

        /// <summary>
        /// Measures a label the way WinForms will actually render it, including
        /// wrapping. Used for the multi-line hint labels inside cards.
        /// </summary>
        static int MeasureTextHeight(string text, int width, float size, FontStyle style)
        {
            using (Font f = new Font("Microsoft YaHei UI", size, style))
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                SizeF sz = g.MeasureString(text, f, Math.Max(40, width));
                return (int)Math.Ceiling(sz.Height);
            }
        }

        /// <summary>
        /// Adds a wrapping hint label inside a card and returns the Y coordinate
        /// immediately below it, so callers can keep stacking content safely.
        /// </summary>
        int AddHint(Panel card, string text, int x, int y, int width)
        {
            Label l = new Label
            {
                Text = text,
                ForeColor = TextSub,
                Font = new Font("Microsoft YaHei UI", 9f),
                AutoSize = true,
                MaximumSize = new Size(Math.Max(80, width), 0),
                Location = new Point(x, y),
                BackColor = Color.Transparent
            };
            card.Controls.Add(l);
            return y + l.PreferredHeight + 8;
        }

        /// <summary>Set per-page by Card2 so cards land in the right container.</summary>
        Action<Panel> _cardHost = delegate { };

        static Label RowLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(x, y),
                BackColor = Color.Transparent
            };
        }

        /// <summary>A hotkey capture box plus a "test availability" button.</summary>
        sealed class HotkeyBox : TextBox
        {
            public HotkeyBox()
            {
                ReadOnly = true;
                BackColor = CardAlt;
                ForeColor = TextMain;
                BorderStyle = BorderStyle.FixedSingle;
                Font = new Font("Consolas", 10f);
                TextAlign = HorizontalAlignment.Center;
                Cursor = Cursors.Hand;
                ShortcutsEnabled = false;
            }

            /// <summary>
            /// Tab, Enter and the arrow keys are "dialog keys" to a TextBox: the
            /// container intercepts them for focus navigation and OnKeyDown never
            /// fires. That made it impossible to record combos such as Ctrl+Up,
            /// which this app actually uses by default. Claiming them as input
            /// keys routes them here instead.
            /// </summary>
            protected override bool IsInputKey(Keys keyData)
            {
                Keys code = keyData & Keys.KeyCode;
                if (code == Keys.Tab || code == Keys.Enter ||
                    code == Keys.Up || code == Keys.Down ||
                    code == Keys.Left || code == Keys.Right ||
                    code == Keys.Escape)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    Text = "";
                    return;
                }
                Keys code = e.KeyCode;
                // Ignore modifier-only presses so the user can build a combo.
                if (code == Keys.ControlKey || code == Keys.ShiftKey ||
                    code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return;

                // A bare Tab must not navigate away from the box while the user
                // is trying to bind it.
                if (code == Keys.Tab && (ModifierKeys & (Keys.Control | Keys.Alt)) == 0
                                     && (ModifierKeys & Keys.Shift) == 0)
                {
                    Text = "Tab";
                    return;
                }

                HotKey hk = new HotKey
                {
                    Key = code,
                    Ctrl = (ModifierKeys & Keys.Control) == Keys.Control,
                    Alt = (ModifierKeys & Keys.Alt) == Keys.Alt,
                    Shift = (ModifierKeys & Keys.Shift) == Keys.Shift
                };
                Text = hk.ToString();
            }

            protected override void OnEnter(EventArgs e)
            {
                base.OnEnter(e);
                BackColor = Color.FromArgb(255, 48, 53, 64);
            }

            protected override void OnLeave(EventArgs e)
            {
                base.OnLeave(e);
                BackColor = CardAlt;
            }
        }

        // =====================================================================
        //  Pages
        // =====================================================================

        void BuildPages()
        {
            AddNav("快捷键");
            AddNav("画笔");
            AddNav("缩放");
            AddNav("截图");
            AddNav("常规");
            AddNav("关于");

            BuildHotkeyPage();
            BuildBrushPage();
            BuildZoomPage();
            BuildShotPage();
            BuildGeneralPage();
            BuildAboutPage();
        }

        void BuildHotkeyPage()
        {
            NewPage("快捷键");

            Card2("全局快捷键",
                "点击输入框后直接按下想要的组合键即可录制。按 Backspace/Delete 清空。\n" +
                "快捷键为全局生效（软件在后台运行时也能触发）。若某个组合被系统或其它软件占用，" +
                "保存时会明确提示。", 60);

            // ---- global action hotkeys ----------------------------------------
            // The authored height is only a floor for cards that have not been
            // laid out yet; the real height always comes from measuring the rows
            // below, so this stays deliberately small.
            Panel hot = Card2("功能快捷键", "这些组合键在任何程序中都能触发。", 120);

            int y = _cardContentTop;
            const int gap = 38;

            _hkToggle = AddHotkeyRow(hot, "开关画笔（进入绘制模式）", "开关画笔", ref y, gap);
            _hkLive = AddHotkeyRow(hot, "实时缩放（放大镜）", "实时缩放", ref y, gap);
            _hkShot = AddHotkeyRow(hot, "区域截图", "区域截图", ref y, gap);
            _hkCopyShot = AddHotkeyRow(hot, "区域截图并复制", "区域截图并复制", ref y, gap);
            _hkWindowShot = AddHotkeyRow(hot, "窗口截图（整个前台窗口）", "窗口截图", ref y, gap);
            _hkPicker = AddHotkeyRow(hot, "屏幕取色器", "取色器", ref y, gap);
            _hkOcr = AddHotkeyRow(hot, "识别选区文字（OCR）", "文字识别", ref y, gap);
            _hkGif = AddHotkeyRow(hot, "录制选区为 GIF", "录制 GIF", ref y, gap);
            _hkZoomIn = AddHotkeyRow(hot, "放大画面", "放大", ref y, gap);
            _hkZoomOut = AddHotkeyRow(hot, "缩小画面", "缩小", ref y, gap);

            // ---- in-overlay tool keys ----------------------------------------
            Panel tools = Card2("绘制中的工具快捷键",
                "在画笔模式下按键切换工具。这些都是单键，不会被注册为全局热键，" +
                "因此不会影响你在其它程序里打字。", 420);

            y = _cardContentTop;
            _hkPen = AddToolKeyRow(tools, "画笔", "画笔", ref y, gap);
            _hkLine = AddToolKeyRow(tools, "直线", "直线", ref y, gap);
            _hkRect = AddToolKeyRow(tools, "矩形", "矩形", ref y, gap);
            _hkEllipse = AddToolKeyRow(tools, "椭圆", "椭圆", ref y, gap);
            _hkArrow = AddToolKeyRow(tools, "箭头", "箭头", ref y, gap);
            _hkText = AddToolKeyRow(tools, "文字标注", "文字标注", ref y, gap);
            _hkMosaic = AddToolKeyRow(tools, "马赛克", "马赛克", ref y, gap);
            _hkSpotlight = AddToolKeyRow(tools, "聚光灯", "聚光灯", ref y, gap);
            _hkHighlighter = AddToolKeyRow(tools, "荧光笔", "荧光笔", ref y, gap);
            _hkStep = AddToolKeyRow(tools, "序号标记", "序号标记", ref y, gap);

            // ---- other single keys -------------------------------------------
            Panel other = Card2("其它单键操作", "同样可以自定义。", 280);

            y = _cardContentTop;
            _hkClear = AddToolKeyRow(other, "清空所有标注", "清空标注", ref y, gap);
            _hkFollow = AddToolKeyRow(other, "跟随滚动模式开/关", "跟随滚动", ref y, gap);
            _hkUndo = AddToolKeyRow(other, "撤销上一笔", "撤销", ref y, gap);
            _hkRedo = AddToolKeyRow(other, "重做（恢复被撤销的标注）", "重做", ref y, gap);
            _hkSave = AddToolKeyRow(other, "保存标注图", "保存标注图", ref y, gap);
            _hkCopy = AddToolKeyRow(other, "复制到剪贴板", "复制到剪贴板", ref y, gap);

            _cardContentTop = AddHint(other,
                "提示：撤销 / 重做 / 保存 / 复制需要带 Ctrl（例如 Ctrl+Z、Ctrl+Y），这样才不会和打字冲突。",
                16, y + 2, CardWidth - 48);

            FinishPage();
        }

        /// <summary>
        /// One configurable key: which box holds it, what to call it in a
        /// message, and whether it is registered globally with Windows.
        /// </summary>
        sealed class KeyBinding
        {
            public HotkeyBox Box;
            public string Name;
            public bool Global;

            public KeyBinding(HotkeyBox box, string name, bool global)
            {
                Box = box;
                Name = name;
                Global = global;
            }
        }

        /// <summary>
        /// One hotkey row: label, capture box, and a live availability readout.
        /// The label is allowed to wrap into the space left of the box instead of
        /// being clipped.
        /// </summary>
        HotkeyBox AddHotkeyRow(Panel host, string label, string shortName,
                               ref int y, int gap)
        {
            HotkeyBox box = AddKeyRow(host, label, ref y, gap, 370, 180);
            _bindings.Add(new KeyBinding(box, shortName, true));
            return box;
        }

        /// <summary>A row for a single (non-modifier) tool key.</summary>
        HotkeyBox AddToolKeyRow(Panel host, string label, string shortName,
                                ref int y, int gap)
        {
            HotkeyBox box = AddKeyRow(host, label, ref y, gap, 370, 90);
            _bindings.Add(new KeyBinding(box, shortName, false));
            return box;
        }

        /// <summary>
        /// Shared row builder. Both row kinds differ only in how wide the
        /// capture box is, and every row must end up in <see cref="_bindings"/>
        /// or the save-time validation would silently skip it.
        /// </summary>
        HotkeyBox AddKeyRow(Panel host, string label, ref int y, int gap,
                            int boxX, int boxW)
        {
            Label l = new Label
            {
                Text = label,
                ForeColor = TextMain,
                AutoSize = true,
                MaximumSize = new Size(330, 0),
                Location = new Point(16, y + 4),
                BackColor = Color.Transparent
            };
            host.Controls.Add(l);

            HotkeyBox box = new HotkeyBox { Bounds = new Rectangle(boxX, y, boxW, 28) };
            host.Controls.Add(box);

            AttachKeyReadout(host, box, boxX + boxW + 16, y + 4);
            y += gap;
            return box;
        }

        void AttachKeyReadout(Panel host, HotkeyBox box, int x, int y)
        {
            Label state = new Label
            {
                ForeColor = TextSub,
                AutoSize = true,
                Location = new Point(x, y),
                BackColor = Color.Transparent,
                Text = ""
            };
            host.Controls.Add(state);
            _readouts[box] = state;

            box.TextChanged += (s, e) =>
            {
                if (!_loading) _lastTouched = box;
                RefreshReadouts();
            };

            // Remembering which box was edited last is what lets a clash be
            // resolved in the user's favour: the freshly typed key keeps its
            // binding and the *other* row is the one reported as conflicting.
            box.GotFocus += (s, e) => { if (!_loading) _lastTouched = box; };
        }

        /// <summary>
        /// Recomputes every key readout from scratch. Doing all of them at once
        /// (rather than just the edited row) is the whole point: a duplicate is
        /// only detectable by comparing rows, and previously the readout for the
        /// edited row said "单键可用" while the save handler later refused it.
        /// </summary>
        void RefreshReadouts()
        {
            // First pass: normalise and group the current contents.
            var parsed = new Dictionary<HotkeyBox, HotKey>();
            var groups = new Dictionary<string, List<HotkeyBox>>();
            foreach (KeyBinding b in _bindings)
            {
                string txt = (b.Box.Text ?? "").Trim();
                HotKey hk = HotKey.Parse(txt);
                parsed[b.Box] = hk;
                if (hk.IsEmpty || hk.Key == Keys.None) continue;
                string norm = hk.ToString();
                List<HotkeyBox> list;
                if (!groups.TryGetValue(norm, out list))
                {
                    list = new List<HotkeyBox>();
                    groups[norm] = list;
                }
                list.Add(b.Box);
            }

            foreach (KeyBinding b in _bindings)
            {
                Label state;
                if (!_readouts.TryGetValue(b.Box, out state)) continue;

                string txt = (b.Box.Text ?? "").Trim();
                if (txt.Length == 0)
                {
                    state.Text = b.Global ? "✗ 不能为空" : "未设置";
                    state.ForeColor = Color.FromArgb(255, 240, 140, 90);
                    continue;
                }

                HotKey hk = parsed[b.Box];
                if (hk.IsEmpty || hk.Key == Keys.None)
                {
                    state.Text = "✗ 无法识别";
                    state.ForeColor = Color.FromArgb(255, 240, 100, 100);
                    continue;
                }

                // Duplicate keys: the last box the user touched keeps the key,
                // every other holder is flagged so the clash is visible before
                // saving rather than only in a modal box afterwards.
                List<HotkeyBox> same = groups[hk.ToString()];
                if (same.Count > 1)
                {
                    bool mine = b.Box == _lastTouched ||
                                (_lastTouched == null && same[0] == b.Box);
                    if (mine)
                    {
                        state.Text = "⚠ 与其它项重复";
                        state.ForeColor = Color.FromArgb(255, 240, 140, 90);
                    }
                    else
                    {
                        state.Text = "✗ 已被占用";
                        state.ForeColor = Color.FromArgb(255, 240, 100, 100);
                    }
                    continue;
                }

                if (b.Global && hk.Modifiers() == 0 && !ModifierlessAllowed(hk.Key))
                {
                    state.Text = "✗ 全局需加修饰键";
                    state.ForeColor = Color.FromArgb(255, 240, 100, 100);
                    continue;
                }

                if (hk.Modifiers() == 0)
                {
                    state.Text = "✓ 单键可用";
                    state.ForeColor = Color.FromArgb(255, 90, 200, 120);
                    continue;
                }
                bool ok = _hotkeyTester != null && _hotkeyTester(hk.Key);
                state.Text = ok ? "✓ 可用" : "⚠ 可能被占用";
                state.ForeColor = ok ? Color.FromArgb(255, 90, 200, 120)
                                     : Color.FromArgb(255, 240, 140, 90);
            }

            RefreshStatusLine();
        }

        /// <summary>
        /// A one-line summary at the bottom of the hotkey page. Without it the
        /// only feedback was a modal dialog on save, which is why a duplicate
        /// could sit unnoticed in a page the user was not even looking at.
        /// </summary>
        void RefreshStatusLine()
        {
            if (_status == null) return;
            int clashes = 0, blanks = 0;
            foreach (KeyBinding b in _bindings)
            {
                string txt = (b.Box.Text ?? "").Trim();
                if (txt.Length == 0) { blanks++; continue; }
                HotKey hk = HotKey.Parse(txt);
                if (hk.IsEmpty || hk.Key == Keys.None) continue;
                foreach (KeyBinding o in _bindings)
                {
                    if (o == b) continue;
                    if (o.Box.Text != null && o.Box.Text.Trim().Length > 0 &&
                        HotKey.Parse(o.Box.Text).ToString() == hk.ToString())
                    { clashes++; break; }
                }
            }
            if (clashes > 0)
                SetStatus("有 " + clashes + " 个快捷键重复，重复的键只有其中一个能用。", false);
            else if (blanks > 0)
                SetStatus("有 " + blanks + " 项快捷键为空。", false);
            else
                SetStatus("快捷键没有冲突，可以保存。", true);
        }

        void BuildBrushPage()
        {
            NewPage("画笔");

            Panel size = Card2("笔宽与工具尺寸", "滚轮也可在绘制时实时调整笔宽（Ctrl+滚轮）。", 190);

            int y = _cardContentTop;
            size.Controls.Add(RowLabel("当前笔宽", 16, y + 4));
            _numWidth = new NumericUpDown
            {
                Bounds = new Rectangle(190, y, 80, 28),
                Minimum = 1, Maximum = 100,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            size.Controls.Add(_numWidth);

            size.Controls.Add(RowLabel("最小 / 最大", 300, y + 4));
            _numMin = new NumericUpDown
            {
                Bounds = new Rectangle(430, y, 70, 28),
                Minimum = 1, Maximum = 100,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            _numMax = new NumericUpDown
            {
                Bounds = new Rectangle(510, y, 70, 28),
                Minimum = 1, Maximum = 100,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            size.Controls.Add(_numMin);
            size.Controls.Add(_numMax);
            y += 36;

            _widthTrack = new TrackBar
            {
                Bounds = new Rectangle(186, y, 360, 34),
                Minimum = 1, Maximum = 40, Value = 3,
                TickStyle = TickStyle.None, BackColor = Card
            };
            size.Controls.Add(_widthTrack);

            _widthPreview = new Label
            {
                Bounds = new Rectangle(16, y + 2, 160, 40),
                BackColor = Color.Transparent
            };
            size.Controls.Add(_widthPreview);
            y += 44;

            // Text and mosaic sizes are per-tool, so they live here rather than
            // being buried in the tool pages.
            size.Controls.Add(RowLabel("文字大小 (px)", 16, y + 4));
            _numTextSize = new NumericUpDown
            {
                Bounds = new Rectangle(190, y, 80, 28),
                Minimum = 8, Maximum = 200, Increment = 2,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            size.Controls.Add(_numTextSize);

            size.Controls.Add(RowLabel("马赛克块大小 (px)", 300, y + 4));
            _numMosaic = new NumericUpDown
            {
                Bounds = new Rectangle(470, y, 80, 28),
                Minimum = 3, Maximum = 80,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            size.Controls.Add(_numMosaic);

            _numWidth.ValueChanged += (s, e) =>
            {
                _widthTrack.Value = (int)_numWidth.Value;
                UpdateWidthPreview();
            };
            _widthTrack.ValueChanged += (s, e) =>
            {
                _numWidth.Value = _widthTrack.Value;
                UpdateWidthPreview();
            };

            Panel colors = Card2("调色板", "点击色块可修改颜色。第 1 个色块是默认颜色。数字键 1~8 对应顺序。", 230);

            _paletteHost = new Panel
            {
                Bounds = new Rectangle(16, _cardContentTop, 740, 92),
                BackColor = Color.Transparent
            };
            colors.Controls.Add(_paletteHost);

            int by = _cardContentTop + 104;
            Button add = MakeButton("+ 添加颜色", false);
            add.Click += (s, e) =>
            {
                if (_draft.Palette.Count >= 12) { Warn("最多 12 种颜色。"); return; }
                _draft.Palette.Add("#888888");
                RebuildPalette();
            };
            colors.Controls.Add(add);

            Button del = MakeButton("- 删除末位", false);
            del.Click += (s, e) =>
            {
                if (_draft.Palette.Count <= 1) { Warn("至少需要保留 1 种颜色。"); return; }
                _draft.Palette.RemoveAt(_draft.Palette.Count - 1);
                RebuildPalette();
            };
            colors.Controls.Add(del);

            Button resetColors = MakeButton("恢复默认颜色", false);
            resetColors.Click += (s, e) =>
            {
                _draft.Palette = new Settings().Palette;
                RebuildPalette();
            };
            colors.Controls.Add(resetColors);

            // Size each button to its own caption and place them in one row.
            LayoutButtonRow(16, by, 10, 32, add, del, resetColors);

            Panel style = Card2("线条与指针", "", 200);
            int sy = _cardContentTop;

            _chkSmooth = new CheckBox
            {
                Text = "平滑线条（抗锯齿）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, sy),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            style.Controls.Add(_chkSmooth);
            // The hint goes BELOW the checkbox, not beside it. It used to sit at
            // the checkbox's own Y with x=34, which put the hint straight on top
            // of the label the user was meant to read.
            sy += _chkSmooth.PreferredSize.Height + 4;
            sy = AddHint(style, "关闭后线条呈像素感，适合在放大画面中做精确标注。", 34, sy, CardWidth - 90);
            sy += 12;

            _chkBrushCursor = new CheckBox
            {
                Text = "使用画笔图标作为鼠标指针",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, sy),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            style.Controls.Add(_chkBrushCursor);
            sy += 30;

            style.Controls.Add(RowLabel("指针大小", 34, sy + 4));
            _numBrushSize = new NumericUpDown
            {
                Bounds = new Rectangle(190, sy, 80, 28),
                Minimum = 16, Maximum = 128, Increment = 4,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            style.Controls.Add(_numBrushSize);

            sy += 38;
            _chkModCtrl = ModBox(style, "按住 Ctrl 拖动画矩形", 16, sy);
            _chkModTab = ModBox(style, "按住 Tab 拖动画椭圆", 396, sy);
            sy += 28;
            _chkModShift = ModBox(style, "按住 Shift 拖动画直线", 16, sy);
            _chkModCtrlShift = ModBox(style, "按住 Ctrl+Shift 画箭头", 396, sy);

            _cardContentTop = AddHint(style,
                "工具键（默认 P/L/R/O/A/T/M/K/H/N）先选中工具；按住上面的修饰键时，" +
                "本次拖动会临时改用对应形状，松开后恢复原工具。",
                16, sy + 34, CardWidth - 48);

            BuildNewToolsCard();
            FinishPage();
        }

        /// <summary>
        /// Settings for the tools added in the tier-1 pass: the highlighter, the
        /// arrow shaft styles, and the numbered step badges. Grouped into one
        /// card because they are all "how the new tools draw" rather than
        /// "which key selects them".
        /// </summary>
        void BuildNewToolsCard()
        {
            Panel t = Card2("荧光笔与序号",
                "荧光笔用半透明色块压住文字，透明度独立于画笔颜色；" +
                "序号标记按点击顺序自动编号，适合写操作步骤。", 300);

            int y = _cardContentTop;

            t.Controls.Add(RowLabel("荧光笔透明度", 16, y + 4));
            _numHighlightAlpha = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 16, Maximum = 220, Increment = 5,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            t.Controls.Add(_numHighlightAlpha);
            t.Controls.Add(RowLabel("数值越小越透明", 316, y + 4));
            y += 40;

            t.Controls.Add(RowLabel("箭头样式", 16, y + 4));
            _cmbArrowStyle = new ComboBox
            {
                Bounds = new Rectangle(220, y, 160, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CardAlt, ForeColor = TextMain, FlatStyle = FlatStyle.Flat
            };
            _cmbArrowStyle.Items.AddRange(new object[] { "直线箭头", "弧线箭头", "折线箭头" });
            t.Controls.Add(_cmbArrowStyle);
            t.Controls.Add(RowLabel("对新画的箭头生效", 396, y + 4));
            y += 40;

            t.Controls.Add(RowLabel("序号圆点大小 (px)", 16, y + 4));
            _numStepBadge = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 16, Maximum = 120, Increment = 2,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            t.Controls.Add(_numStepBadge);
            y += 40;

            _chkStepPersistent = new CheckBox
            {
                Text = "序号跨截图连续编号（下一张图接着上一张的号码）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            t.Controls.Add(_chkStepPersistent);

            _cardContentTop = AddHint(t,
                "在同一张截图里撤销一个序号后，它的号码会被下一个序号重新使用；" +
                "上面的选项只影响「关掉绘制模式再重新开始」时的起点。",
                16, y + 34, CardWidth - 48);
        }

        CheckBox ModBox(Panel host, string text, int x, int y)
        {
            CheckBox c = new CheckBox
            {
                Text = text,
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(x, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            host.Controls.Add(c);
            return c;
        }

        void BuildZoomPage()
        {
            NewPage("缩放");

            Panel z = Card2("实时缩放",
                "在实时缩放模式下移动鼠标到屏幕边缘即可平移画面；滚轮调整倍率。", 240);

            int y = _cardContentTop;
            z.Controls.Add(RowLabel("每次滚轮步进 (%)", 16, y + 4));
            _numStep = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 5, Maximum = 200, Increment = 5,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            z.Controls.Add(_numStep);
            y += 40;

            z.Controls.Add(RowLabel("最小倍率", 16, y + 4));
            _numMinZoom = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 0.10m, Maximum = 1.00m, DecimalPlaces = 2, Increment = 0.05m,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            z.Controls.Add(_numMinZoom);

            z.Controls.Add(RowLabel("最大倍率", 340, y + 4));
            _numMaxZoom = new NumericUpDown
            {
                Bounds = new Rectangle(450, y, 80, 28),
                Minimum = 1.00m, Maximum = 32.00m, DecimalPlaces = 2, Increment = 0.5m,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            z.Controls.Add(_numMaxZoom);
            y += 42;

            _chkMagnifier = new CheckBox
            {
                Text = "缩放时显示鼠标位置指示圈",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            z.Controls.Add(_chkMagnifier);

            Panel tips = Card2("操作说明", "", 210);
            int ty = _cardContentTop;
            ty = AddHint(tips, "· 鼠标滚轮向上：放大；向下：缩小（以光标位置为锚点，视线不跳）", 16, ty, CardWidth - 48);
            ty = AddHint(tips, "· 光标靠近画面边缘：画面自动跟随滚动（边缘 24 像素内触发）", 16, ty, CardWidth - 48);
            ty = AddHint(tips, "· 按住鼠标右键拖动：自由平移画面", 16, ty, CardWidth - 48);
            ty = AddHint(tips, "· 按 Ctrl+↑ / Ctrl+↓：以外接键盘放大或缩小", 16, ty, CardWidth - 48);
            ty = AddHint(tips, "· 按 0：复位视图（回到 100% 并回到左上角）", 16, ty, CardWidth - 48);
            _cardContentTop = ty;

            FinishPage();
        }

        void BuildShotPage()
        {
            NewPage("截图");

            Panel s = Card2("截图方式",
                "上方热键（默认 Ctrl+Alt+S）会先让你在屏幕上框选区域，再保存。\n" +
                "框选时：拖动选择区域 · Enter 保存 · Ctrl+C 直接复制 · Esc 取消 · " +
                "双击选区内部直接保存 · 方向键微调（按住 Shift 加速）。", 200);

            int y = _cardContentTop;

            _chkRegionDim = new CheckBox
            {
                Text = "框选时压暗选区以外的部分",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            s.Controls.Add(_chkRegionDim);

            _chkRegionSize = new CheckBox
            {
                Text = "显示选区像素尺寸",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(396, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            s.Controls.Add(_chkRegionSize);

            Panel save = Card2("保存位置与格式",
                "默认每次截图都会弹出「另存为」对话框，由你自己选择路径。" +
                "如果勾选下面的选项，则静默保存到指定目录，不再弹窗。", 200);

            y = _cardContentTop;
            _chkAutoSave = new CheckBox
            {
                Text = "直接保存到指定目录（不弹出保存对话框）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            save.Controls.Add(_chkAutoSave);
            y += 36;

            _txtFolder = new TextBox
            {
                Bounds = new Rectangle(16, y, 600, 28),
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            save.Controls.Add(_txtFolder);

            Button browse = MakeButton("浏览…", false);
            browse.Bounds = new Rectangle(626, y - 1, 90, 30);
            FitButton(browse, 90);
            browse.Top = y - 1;
            browse.Height = 30;
            browse.Click += (s2, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择截图保存目录";
                    if (Directory.Exists(_txtFolder.Text)) fbd.SelectedPath = _txtFolder.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK) _txtFolder.Text = fbd.SelectedPath;
                }
            };
            save.Controls.Add(browse);
            y += 42;

            save.Controls.Add(RowLabel("格式", 16, y + 4));
            _cmbFormat = new ComboBox
            {
                Bounds = new Rectangle(90, y, 130, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CardAlt, ForeColor = TextMain, FlatStyle = FlatStyle.Flat
            };
            _cmbFormat.Items.AddRange(new object[] { "png", "jpg", "bmp" });
            save.Controls.Add(_cmbFormat);

            _chkSound = new CheckBox
            {
                Text = "截图后播放提示音",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(260, y + 4),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            save.Controls.Add(_chkSound);

            Panel scope = Card2("截图范围", "", 150);
            int sy = _cardContentTop;
            sy = AddHint(scope, "区域截图只包含你框选的那一块，不会截到整个桌面。", 16, sy, CardWidth - 48);
            sy = AddHint(scope, "如果先进入画笔模式再截图，标注会一并烤进图片里。", 16, sy, CardWidth - 48);
            _cardContentTop = AddHint(scope,
                "托盘菜单里也可以直接选择「整屏截图」或「复制整屏」。", 16, sy, CardWidth - 48);

            BuildBeautifyCard();
            BuildPickerCard();
            BuildOcrCard();
            BuildGifCard();
            FinishPage();
        }

        /// <summary>
        /// The "beautify" card: wraps saved screenshots in a padded gradient card
        /// with rounded corners and a soft shadow, the way CleanShot X / Shottr /
        /// Xnapper do. Off by default because it changes the saved file's
        /// dimensions, which would surprise anyone who just wants the pixels.
        /// </summary>
        void BuildBeautifyCard()
        {
            Panel b = Card2("美化截图",
                "开启后，保存的图片会自动加一圈渐变背景、圆角和柔和阴影，" +
                "适合直接发到聊天窗口或文档里。复制到剪贴板和贴图不受影响。", 120);

            int y = _cardContentTop;

            _chkBeautify = new CheckBox
            {
                Text = "保存时自动美化（图片尺寸会变大）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            b.Controls.Add(_chkBeautify);
            y += 38;

            b.Controls.Add(RowLabel("内边距 (px)", 16, y + 4));
            _numBeautifyPad = new NumericUpDown
            {
                Bounds = new Rectangle(190, y, 80, 28),
                Minimum = 0, Maximum = 400, Increment = 4,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            b.Controls.Add(_numBeautifyPad);

            b.Controls.Add(RowLabel("圆角 (px)", 300, y + 4));
            _numBeautifyRadius = new NumericUpDown
            {
                Bounds = new Rectangle(430, y, 80, 28),
                Minimum = 0, Maximum = 200, Increment = 2,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            b.Controls.Add(_numBeautifyRadius);
            y += 40;

            _chkBeautifyShadow = new CheckBox
            {
                Text = "添加柔和阴影",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            b.Controls.Add(_chkBeautifyShadow);
            y += 38;

            b.Controls.Add(RowLabel("背景渐变", 16, y + 4));
            _btnBeautifyColor1 = MakeButton("#6D5BFF", false);
            _btnBeautifyColor1.Bounds = new Rectangle(120, y - 1, 130, 30);
            _btnBeautifyColor1.Click += (s, e) => PickColorFor(_btnBeautifyColor1);
            b.Controls.Add(_btnBeautifyColor1);

            _btnBeautifyColor2 = MakeButton("#22B8CF", false);
            _btnBeautifyColor2.Bounds = new Rectangle(260, y - 1, 130, 30);
            _btnBeautifyColor2.Click += (s, e) => PickColorFor(_btnBeautifyColor2);
            b.Controls.Add(_btnBeautifyColor2);

            Button swap = MakeButton("对调", false);
            swap.Click += (s, e) =>
            {
                string tmp = _btnBeautifyColor1.Text;
                _btnBeautifyColor1.Text = _btnBeautifyColor2.Text;
                _btnBeautifyColor2.Text = tmp;
            };
            b.Controls.Add(swap);
            LayoutButtonRow(400, y - 1, 10, 30, swap);

            _cardContentTop = AddHint(b,
                "颜色可以填 #RRGGBB 形式，也可以点按钮用屏幕取色器选取。",
                16, y + 34, CardWidth - 48);
        }

        /// <summary>Loupe geometry for the screen colour picker.</summary>
        void BuildPickerCard()
        {
            Panel p = Card2("屏幕取色器",
                "按下取色快捷键后，鼠标旁边会出现放大镜，点击左键取色，" +
                "右键或 Esc 取消。取到的颜色会替换当前调色板中选中的那一格，并复制色号。", 190);

            int y = _cardContentTop;

            p.Controls.Add(RowLabel("放大镜大小 (px)", 16, y + 4));
            _numPickerLoupe = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 60, Maximum = 400, Increment = 10,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numPickerLoupe);
            y += 40;

            p.Controls.Add(RowLabel("放大倍数", 16, y + 4));
            _numPickerZoom = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 2, Maximum = 32, Increment = 1,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numPickerZoom);

            _cardContentTop = AddHint(p,
                "放大镜里每个方块就是屏幕上的一个真实像素，中间十字对准的那个才会被取走。",
                16, y + 34, CardWidth - 48);
        }

        /// <summary>
        /// OCR settings. Recognition uses the OCR engine built into Windows, so
        /// there is nothing to install; the available languages depend on what the
        /// user has added under 设置 → 时间和语言 → 语言.
        /// </summary>
        void BuildOcrCard()
        {
            Panel p = Card2("文字识别（OCR）",
                "在区域选择界面按 O，或按 Ctrl+Alt+O，即可识别选区里的文字。" +
                "识别由 Windows 自带的 OCR 引擎完成，不联网、不上传任何内容。", 120);

            int y = _cardContentTop;

            string detail;
            string[] langs = Ocr.AvailableLanguages();
            bool avail = Ocr.IsAvailable(out detail);

            _chkOcrCopy = new CheckBox
            {
                Text = "识别后自动复制到剪贴板",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            p.Controls.Add(_chkOcrCopy);
            y += 34;

            _chkOcrLines = new CheckBox
            {
                Text = "保留原来的换行",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            p.Controls.Add(_chkOcrLines);
            y += 34;

            p.Controls.Add(RowLabel("小图放大倍数", 16, y + 4));
            _numOcrUpscale = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 0, Maximum = 8, Increment = 1,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numOcrUpscale);
            y += 40;

            p.Controls.Add(RowLabel("识别语言", 16, y + 4));
            _cmbOcrLang = new ComboBox
            {
                Bounds = new Rectangle(220, y, 260, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CardAlt, ForeColor = TextMain, FlatStyle = FlatStyle.Flat
            };
            _cmbOcrLang.Items.Add("跟随系统");
            foreach (string lg in langs) _cmbOcrLang.Items.Add(lg);
            p.Controls.Add(_cmbOcrLang);

            string hint;
            if (!avail)
                hint = "当前系统没有可用的识别语言，请在 Windows 设置里添加语言后重试。" + detail;
            else if (langs.Length == 0)
                hint = "没有检测到已安装的识别语言，请在 Windows 设置 → 时间和语言 → 语言 中添加。";
            else
                hint = "已安装的识别语言：" + string.Join("、", langs) +
                       "。选「跟随系统」时会用 Windows 的默认语言。";
            _cardContentTop = AddHint(p, hint, 16, y + 34, CardWidth - 48);
        }

        /// <summary>GIF recording settings.</summary>
        void BuildGifCard()
        {
            Panel p = Card2("录制 GIF 动图",
                "在区域选择界面按 G，或按 Ctrl+Alt+G，即可把选区录制成 GIF。" +
                "录制时选区周围会出现红框和状态条，按 Esc 结束；红框不会被录进画面。", 120);

            int y = _cardContentTop;

            p.Controls.Add(RowLabel("帧率 (帧/秒)", 16, y + 4));
            _numGifFps = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 2, Maximum = 25, Increment = 1,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numGifFps);
            y += 40;

            p.Controls.Add(RowLabel("最长录制 (秒)", 16, y + 4));
            _numGifMax = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 1, Maximum = 300, Increment = 5,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numGifMax);
            y += 40;

            p.Controls.Add(RowLabel("开始前倒计时 (秒)", 16, y + 4));
            _numGifCountdown = new NumericUpDown
            {
                Bounds = new Rectangle(220, y, 80, 28),
                Minimum = 0, Maximum = 10, Increment = 1,
                BackColor = CardAlt, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle
            };
            p.Controls.Add(_numGifCountdown);
            y += 40;

            _chkGifBorder = new CheckBox
            {
                Text = "录制时显示红框和状态条",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            p.Controls.Add(_chkGifBorder);
            y += 34;

            _chkGifLoop = new CheckBox
            {
                Text = "循环播放",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            p.Controls.Add(_chkGifLoop);
            y += 34;

            _chkGifSkip = new CheckBox
            {
                Text = "画面没变化时合并帧（文件更小）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            p.Controls.Add(_chkGifSkip);

            _cardContentTop = AddHint(p,
                "帧率越高越流畅，文件也越大；10 帧/秒适合大多数演示。" +
                "「合并帧」会把连续相同的画面合成一帧，静态内容占用的空间会小很多。",
                16, y + 34, CardWidth - 48);
        }

        /// <summary>
        /// Selects the saved OCR language, falling back to 跟随系统 when the
        /// language is no longer installed (the user may have removed it since).
        /// </summary>
        void SelectOcrLanguage(string tag)
        {
            int idx = 0;   // "跟随系统"
            if (!string.IsNullOrEmpty(tag))
            {
                for (int i = 0; i < _cmbOcrLang.Items.Count; i++)
                    if (string.Equals(Convert.ToString(_cmbOcrLang.Items[i]), tag,
                                      StringComparison.OrdinalIgnoreCase))
                    { idx = i; break; }
            }
            if (idx < _cmbOcrLang.Items.Count) _cmbOcrLang.SelectedIndex = idx;
        }

        /// <summary>The chosen language, or "" for 跟随系统.</summary>
        string SelectedOcrLanguage()
        {
            int i = _cmbOcrLang.SelectedIndex;
            if (i <= 0) return "";
            return Convert.ToString(_cmbOcrLang.Items[i]);
        }

        /// <summary>
        /// Opens the app's own full-screen picker for a settings swatch. Reusing
        /// the real picker keeps one sampling implementation, and it is far more
        /// usable than typing hex for a colour the user is looking at.
        /// </summary>
        void PickColorFor(Button target)
        {
            Rectangle vs = Native.VirtualScreen();
            Bitmap frozen = Native.CaptureRegion(vs);
            if (frozen == null) { Warn("无法截取屏幕，请手动输入色号。"); return; }

            Color current;
            if (!Beautifier.TryParseColor(target.Text, out current)) current = Color.White;

            Color picked;
            using (ColorPickerForm picker = new ColorPickerForm(_draft, vs, frozen, true))
            {
                picker.ShowDialog(this);
                picked = picker.PickedColor;
            }
            if (!picked.IsEmpty) target.Text = Beautifier.FormatColor(picked);
        }

        void BuildGeneralPage()
        {
            NewPage("常规");

            Panel g = Card2("启动与后台", "", 120);

            int y = _cardContentTop;
            _chkStartup = new CheckBox
            {
                Text = "开机自动启动（登录后自动在后台运行）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            g.Controls.Add(_chkStartup);
            y += 34;

            _chkTray = new CheckBox
            {
                Text = "显示系统托盘图标",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            g.Controls.Add(_chkTray);
            y += 34;

            _chkMinimized = new CheckBox
            {
                Text = "启动时不自动打开本设置窗口",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            g.Controls.Add(_chkMinimized);
            y += 34;

            _cardContentTop = AddHint(g,
                "开机自启写入 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run，" +
                "只影响当前用户，不需要管理员权限，可随时在此关闭。",
                16, y, CardWidth - 48);

            Panel follow = Card2("跟随滚动（在网页 / 文档上标注）",
                "开启后，在画笔模式下按 F 即可打开「跟随滚动」，此时滚动鼠标滚轮会真正滚动" +
                "下方的网页或文档，而你画好的标注会跟着内容一起移动，不会漂在原地。", 250);

            y = _cardContentTop;
            _chkScrollFollow = new CheckBox
            {
                Text = "允许使用跟随滚动模式（在画笔模式下按 F 开关）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            follow.Controls.Add(_chkScrollFollow);
            y += 34;

            _chkWheelScroll = new CheckBox
            {
                Text = "滚轮滚动网页内容（关闭则滚轮改为调整笔宽）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            follow.Controls.Add(_chkWheelScroll);
            y += 34;

            _chkClickThrough = new CheckBox
            {
                Text = "跟随期间让鼠标点击穿透到下方窗口（可滚动、点链接；此时无法再画新标注）",
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, y),
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            follow.Controls.Add(_chkClickThrough);
            y += 36;

            _cardContentTop = AddHint(follow,
                "工作方式：程序会在后台以低分辨率对比前后两帧画面，算出内容实际移动了多少像素，" +
                "再把所有标注平移相同距离。因此只有画面真的滚动了才会跟随，静止时不会漂移。\n" +
                "跟随滚动默认关闭：它需要持续截屏做比对，开启后会略微增加 CPU 占用。" +
                "如果窗口不支持滚轮（例如桌面或全屏视频），滚轮会自动回到调整笔宽。",
                16, y, CardWidth - 48);

            Panel data = Card2("数据与配置", "", 150);
            y = _cardContentTop;
            y = AddHint(data, "配置文件：" + Settings.SettingsPath(), 16, y, CardWidth - 48);

            Button open = MakeButton("打开配置目录", false);
            open.Bounds = new Rectangle(16, y, 140, 32);
            FitButton(open, 140);
            open.Left = 16;
            open.Top = y;
            open.Height = 32;
            open.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "/select,\"" + Settings.SettingsPath() + "\"");
                }
                catch { }
            };
            data.Controls.Add(open);
            _cardContentTop = y + 40;

            FinishPage();
        }

        void BuildAboutPage()
        {
            NewPage("关于");
            Panel a = Card2("ScreenPen 屏幕画笔", "", 420);

            int y = _cardContentTop;
            y = AddHint(a, "版本 1.1.0", 16, y, CardWidth - 48);
            y = AddHint(a,
                "一款轻量的屏幕标注、实时缩放与区域截图工具，交互思路参照 Sysinternals ZoomIt。",
                16, y, CardWidth - 48);
            y += 4;
            y = AddHint(a, "绘图工具：画笔 · 直线 · 矩形 · 椭圆 · 箭头 · 文字标注 · 马赛克 · 聚光灯",
                        16, y, CardWidth - 48);
            y = AddHint(a, "修饰键：Ctrl=矩形 · Tab=椭圆 · Shift=直线 · Ctrl+Shift=箭头",
                        16, y, CardWidth - 48);
            y = AddHint(a, "视图：实时缩放（滚轮 + 边缘自动平移）· 右键拖动平移 · 0 复位",
                        16, y, CardWidth - 48);
            y = AddHint(a, "截图：区域框选，可保存到文件或直接复制到剪贴板",
                        16, y, CardWidth - 48);
            y = AddHint(a, "跟随滚动：滚轮滚动下方网页，标注随内容一起移动",
                        16, y, CardWidth - 48);
            y += 6;

            Label hot = new Label
            {
                Text = "当前快捷键：" + _cfg.ToggleKey + " 开关画笔 · " +
                       _cfg.LiveZoomKey + " 实时缩放 · " + _cfg.ScreenshotKey + " 区域截图",
                ForeColor = Accent,
                AutoSize = true,
                MaximumSize = new Size(CardWidth - 48, 0),
                Location = new Point(16, y),
                BackColor = Color.Transparent
            };
            a.Controls.Add(hot);
            y += hot.PreferredHeight + 10;

            _cardContentTop = AddHint(a,
                "绿色免安装：不写注册表（除可选的开机自启项），配置保存在 EXE 同目录的 .settings.json。",
                16, y, CardWidth - 48);

            FinishPage();
        }

        // =====================================================================
        //  Palette editor
        // =====================================================================

        void RebuildPalette()
        {
            _paletteHost.Controls.Clear();

            // Swatch geometry is measured, not assumed. The caption under each
            // swatch ("默认" / "1".."12") previously got a fixed 52x16 box while
            // the font needs ~31px of height, so the captions were cut in half -
            // the visible "文字被遮挡" on the palette card.
            const int SwatchW = 52, SwatchH = 52, Gap = 8;
            int labelH = TextRenderer.MeasureText("默认", _paletteLabelFont).Height + 4;
            int step = SwatchW + Gap;

            int x = 0;
            for (int i = 0; i < _draft.Palette.Count; i++)
            {
                int idx = i;
                Color c;
                if (!Settings.TryParseColor(_draft.Palette[i], out c)) c = Color.Gray;

                Panel sw = new Panel
                {
                    Bounds = new Rectangle(x, 0, SwatchW, SwatchH),
                    BackColor = c,
                    Cursor = Cursors.Hand
                };
                sw.Paint += (s, e) =>
                {
                    using (Pen p = new Pen(Color.FromArgb(255, 70, 76, 90), 1))
                        e.Graphics.DrawRectangle(p, 0, 0, sw.Width - 1, sw.Height - 1);
                };
                sw.Click += (s, e) =>
                {
                    using (ColorDialog cd = new ColorDialog())
                    {
                        cd.Color = c;
                        cd.FullOpen = true;
                        if (cd.ShowDialog(this) == DialogResult.OK)
                        {
                            _draft.Palette[idx] = Settings.ToHex(cd.Color);
                            RebuildPalette();
                        }
                    }
                };
                _paletteHost.Controls.Add(sw);

                string cap = (i == 0 ? "默认" : (i + 1).ToString(CultureInfo.InvariantCulture));
                Label n = new Label
                {
                    Text = cap,
                    Font = _paletteLabelFont,
                    ForeColor = TextSub,
                    AutoSize = false,
                    // Widen to the caption's real width so a two-character label
                    // is never squeezed, and give it the full line height.
                    Bounds = new Rectangle(x, SwatchH + 4,
                                           Math.Max(SwatchW, TextRenderer.MeasureText(cap, _paletteLabelFont).Width + 6),
                                           labelH),
                    TextAlign = ContentAlignment.MiddleCenter,
                    BackColor = Color.Transparent
                };
                _paletteHost.Controls.Add(n);
                x += step;
            }
        }

        /// <summary>Font shared by the palette captions (also used to measure them).</summary>
        readonly Font _paletteLabelFont = new Font("Microsoft YaHei UI", 8f);

        void UpdateWidthPreview()
        {
            int w = (int)_numWidth.Value;
            _widthPreview.Invalidate();
            if (_widthPreview.Tag == null || (int)_widthPreview.Tag != w)
            {
                _widthPreview.Tag = w;
                _widthPreview.Paint -= PreviewPaint;
                _widthPreview.Paint += PreviewPaint;
            }
            _widthPreview.Invalidate();
        }

        void PreviewPaint(object sender, PaintEventArgs e)
        {
            int w = _widthPreview.Tag is int ? (int)_widthPreview.Tag : 3;
            int cy = _widthPreview.Height / 2;
            Color c;
            if (!Settings.TryParseColor(_draft.Palette[0], out c)) c = Color.Red;
            using (Pen p = new Pen(c, Math.Max(1, w)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLine(p, 6, cy, _widthPreview.Width - 10, cy);
            }
            using (SolidBrush b = new SolidBrush(TextSub))
            using (Font f = new Font("Microsoft YaHei UI", 7.5f))
                e.Graphics.DrawString(w + " px", f, b, 6, 2);
        }

        // =====================================================================
        //  Load / save
        // =====================================================================

        void LoadDraftIntoControls()
        {
            // Setting Text fires TextChanged, which would mark the last box as
            // "touched by the user" and hand it priority in a clash. Suppress
            // that while the dialog is being populated, then refresh once.
            _loading = true;
            try
            {
            _hkToggle.Text = _draft.ToggleKey;
            _hkLive.Text = _draft.LiveZoomKey;
            _hkShot.Text = _draft.ScreenshotKey;
            _hkCopyShot.Text = _draft.CopyShotKey;
            _hkWindowShot.Text = _draft.WindowShotKey;
            _hkPicker.Text = _draft.ColorPickerKey;
            _hkOcr.Text = _draft.OcrKey;
            _hkGif.Text = _draft.GifKey;
            _hkZoomIn.Text = _draft.ZoomInKey;
            _hkZoomOut.Text = _draft.ZoomOutKey;

            _hkPen.Text = _draft.PenKey;
            _hkLine.Text = _draft.LineKey;
            _hkRect.Text = _draft.RectKey;
            _hkEllipse.Text = _draft.EllipseKey;
            _hkArrow.Text = _draft.ArrowKey;
            _hkText.Text = _draft.TextKey;
            _hkMosaic.Text = _draft.MosaicKey;
            _hkSpotlight.Text = _draft.SpotlightKey;
            _hkHighlighter.Text = _draft.HighlighterKey;
            _hkStep.Text = _draft.StepKey;

            _hkClear.Text = _draft.ClearKey;
            _hkFollow.Text = _draft.FollowKey;
            _hkUndo.Text = _draft.UndoKey;
            _hkRedo.Text = _draft.RedoKey;
            _hkSave.Text = _draft.SaveKey;
            _hkCopy.Text = _draft.CopyKey;

            _numWidth.Value = Clamp(_draft.PenWidth, _numWidth);
            _numMin.Value = Clamp(_draft.MinPenWidth, _numMin);
            _numMax.Value = Clamp(_draft.MaxPenWidth, _numMax);
            _widthTrack.Value = Math.Max(_widthTrack.Minimum,
                                Math.Min(_widthTrack.Maximum, _draft.PenWidth));
            UpdateWidthPreview();
            RebuildPalette();

            _numTextSize.Value = Clamp((decimal)_draft.TextSize, _numTextSize);
            _numMosaic.Value = Clamp((decimal)_draft.MosaicBlock, _numMosaic);
            _numBrushSize.Value = Clamp(_draft.BrushCursorSize, _numBrushSize);

            _chkSmooth.Checked = _draft.SmoothLines;
            _chkBrushCursor.Checked = _draft.ShowBrushCursor;
            _chkModCtrl.Checked = _draft.ModCtrlRectangle;
            _chkModTab.Checked = _draft.ModTabEllipse;
            _chkModShift.Checked = _draft.ModShiftLine;
            _chkModCtrlShift.Checked = _draft.ModCtrlShiftArrow;

            // Tier-1 controls.
            _numHighlightAlpha.Value = Clamp(_draft.HighlighterAlpha, _numHighlightAlpha);
            _cmbArrowStyle.SelectedIndex = Math.Max(0, Math.Min(2, _draft.ArrowStyleIndex));
            _numStepBadge.Value = Clamp((decimal)_draft.StepBadgeSize, _numStepBadge);
            _chkStepPersistent.Checked = _draft.StepNumberPersistent;

            _chkBeautify.Checked = _draft.BeautifyEnabled;
            _numBeautifyPad.Value = Clamp(_draft.BeautifyPadding, _numBeautifyPad);
            _numBeautifyRadius.Value = Clamp(_draft.BeautifyRadius, _numBeautifyRadius);
            _chkBeautifyShadow.Checked = _draft.BeautifyShadow;
            _btnBeautifyColor1.Text = _draft.BeautifyColor1;
            _btnBeautifyColor2.Text = _draft.BeautifyColor2;

            _numPickerLoupe.Value = Clamp(_draft.PickerLoupeSize, _numPickerLoupe);
            _numPickerZoom.Value = Clamp(_draft.PickerZoom, _numPickerZoom);

            _chkOcrCopy.Checked = _draft.OcrCopyToClipboard;
            _chkOcrLines.Checked = _draft.OcrKeepLineBreaks;
            _numOcrUpscale.Value = Clamp(_draft.OcrUpscaleSmall, _numOcrUpscale);
            SelectOcrLanguage(_draft.OcrLanguage);

            _numGifFps.Value = Clamp(_draft.GifFps, _numGifFps);
            _numGifMax.Value = Clamp(_draft.GifMaxSeconds, _numGifMax);
            _numGifCountdown.Value = Clamp(_draft.GifCountdownSeconds, _numGifCountdown);
            _chkGifBorder.Checked = _draft.GifShowBorder;
            _chkGifLoop.Checked = _draft.GifLoop;
            _chkGifSkip.Checked = _draft.GifSkipIdenticalFrames;

            _chkMagnifier.Checked = _draft.MagnifierDuringLiveZoom;
            _numStep.Value = Clamp(_draft.ZoomStepPercent, _numStep);
            _numMinZoom.Value = Clamp((decimal)_draft.MinZoom, _numMinZoom);
            _numMaxZoom.Value = Clamp((decimal)_draft.MaxZoom, _numMaxZoom);

            _chkRegionDim.Checked = _draft.RegionDimOutside;
            _chkRegionSize.Checked = _draft.RegionShowSize;

            _chkStartup.Checked = AutoStart.IsEnabled() || _draft.RunAtStartup;
            _chkTray.Checked = _draft.ShowTrayIcon;
            _chkMinimized.Checked = _draft.StartMinimized;

            _chkScrollFollow.Checked = _draft.ScrollFollowEnabled;
            _chkWheelScroll.Checked = _draft.WheelScrollsDocument;
            _chkClickThrough.Checked = _draft.ScrollFollowClickThrough;

            _chkAutoSave.Checked = _draft.SaveScreenshotsToDefaultFolder;
            _txtFolder.Text = _draft.ScreenshotFolder;
            _cmbFormat.SelectedItem = _draft.ScreenshotFormat;
            if (_cmbFormat.SelectedIndex < 0) _cmbFormat.SelectedIndex = 0;
            _chkSound.Checked = _draft.PlaySoundOnScreenshot;
            }
            finally
            {
                _loading = false;
            }

            // Every box now holds its real value, so the clash readouts and the
            // status line can be computed once for the whole page set.
            RefreshReadouts();
        }

        static decimal Clamp(int v, NumericUpDown n)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        static decimal Clamp(decimal v, NumericUpDown n)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        void ResetDefaults()
        {
            if (Prompt("确定要把所有设置恢复为默认值吗？", "恢复默认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _draft = new Settings();
            LoadDraftIntoControls();
            Info("已恢复默认，点击「保存设置」后生效。");
        }

        /// <summary>
        /// Keys that are safe to use as a global hotkey without any modifier.
        /// A bare letter would swallow that letter while typing everywhere in
        /// Windows, but F1-F24, Pause, ScrollLock and PrintScreen are not typing
        /// keys at all, so refusing them was simply wrong.
        /// </summary>
        static bool ModifierlessAllowed(Keys key)
        {
            if (key >= Keys.F1 && key <= Keys.F24) return true;
            if (key == Keys.Pause || key == Keys.Scroll || key == Keys.PrintScreen) return true;
            if (key == Keys.Insert || key == Keys.Delete) return true;
            if (key == Keys.Home || key == Keys.End ||
                key == Keys.PageUp || key == Keys.PageDown) return true;
            return false;
        }

        /// <summary>
        /// Keys that cannot be captured or registered as a global hotkey.
        /// </summary>
        static bool ReservedKey(Keys key)
        {
            return key == Keys.None ||
                   key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                   key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                   key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                   key == Keys.LWin || key == Keys.RWin;
        }

        void OnSave()
        {
            // Every problem is collected and reported at once. The previous
            // version bailed out on the first issue, so the user could fix one
            // field, hit save, and be told about the next one - with nothing
            // ever being written.
            var problems = new List<string>();

            var seenGlobal = new Dictionary<string, string>();
            var seenLocal = new Dictionary<string, string>();

            // Walk exactly the rows the dialog built, so a newly added row can
            // never be forgotten here (the old hard-coded list had to be kept in
            // sync by hand).
            foreach (KeyBinding p in _bindings)
            {
                string txt = (p.Box.Text ?? "").Trim();

                // Single-key tool bindings may be left empty (feature disabled).
                if (txt.Length == 0)
                {
                    if (p.Global) problems.Add("「" + p.Name + "」的快捷键不能为空。");
                    continue;
                }

                HotKey hk = HotKey.Parse(txt);
                if (hk.IsEmpty || hk.Key == Keys.None || ReservedKey(hk.Key))
                {
                    problems.Add("「" + p.Name + "」无法识别：" + txt);
                    continue;
                }

                // Bare letters/digits only matter for *global* hotkeys, where
                // they would steal that key from every other program.
                if (p.Global && hk.Modifiers() == 0 && !ModifierlessAllowed(hk.Key))
                {
                    problems.Add("「" + p.Name + "」用的是单键 " + hk.Key +
                                 "，作为全局快捷键会抢走打字输入；\n" +
                                 "  请加上 Ctrl / Alt / Shift，或改用 F1~F24 等功能键。");
                    continue;
                }

                // A global hotkey must not collide with the tool keys, which are
                // only active while the overlay is up - otherwise the same key
                // would be ambiguous depending on state.
                string norm = hk.ToString();
                Dictionary<string, string> seen = p.Global ? seenGlobal : seenLocal;
                if (seen.ContainsKey(norm))
                {
                    problems.Add("快捷键冲突：「" + p.Name + "」与「" + seen[norm] + "」都是 " + norm + "。");
                    continue;
                }
                seen[norm] = p.Name;

                // Cross-check the two groups so a global hotkey and a tool key
                // are never the same combination.
                Dictionary<string, string> other = p.Global ? seenLocal : seenGlobal;
                if (other.ContainsKey(norm))
                {
                    problems.Add("快捷键冲突：「" + p.Name + "」与「" + other[norm] + "」都是 " + norm + "。");
                    continue;
                }
            }

            // Buttons that change meaning with modifiers are only usable when the
            // matching override is enabled; warn rather than silently ignore.
            if (!_chkModCtrlShift.Checked && _chkModCtrl.Checked && _chkModShift.Checked)
                problems.Add("提示：Ctrl 与 Shift 同时按下时，会优先按「矩形」或「直线」处理；\n" +
                             "  建议同时开启「按住 Ctrl+Shift 画箭头」以免歧义。");

            if (problems.Count > 0)
            {
                // Duplicate bindings are a *choice*, not an error: the runtime
                // resolves them deterministically (the first match wins), so the
                // user may legitimately keep two rows on one key. Blocking the
                // whole save for that threw away every other edit and left the
                // dialog looking as if nothing could be saved at all. Genuinely
                // broken entries (unparseable, or a bare letter as a global key)
                // still stop the save.
                var errors = new List<string>();
                var clashes = new List<string>();
                foreach (string s in problems)
                {
                    if (s.StartsWith("提示：")) continue;
                    if (s.StartsWith("快捷键冲突：")) clashes.Add(s);
                    else errors.Add(s);
                }

                if (errors.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("以下快捷键设置有问题，尚未保存：");
                    sb.AppendLine();
                    foreach (string s in errors) sb.AppendLine("  · " + s);
                    Warn(sb.ToString());
                    return;
                }

                if (clashes.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("有 " + clashes.Count + " 组快捷键重复：");
                    sb.AppendLine();
                    foreach (string s in clashes)
                        sb.AppendLine("  · " + s.Substring("快捷键冲突：".Length));
                    sb.AppendLine();
                    sb.AppendLine("重复的键在绘制时只会触发其中一项（列表靠前的那一项）。");
                    sb.AppendLine("仍然要保存这些设置吗？");
                    if (Prompt(sb.ToString(), "快捷键重复",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
            }

            // ---- commit ----
            _draft.ToggleKey = _hkToggle.Text.Trim();
            _draft.LiveZoomKey = _hkLive.Text.Trim();
            _draft.ScreenshotKey = _hkShot.Text.Trim();
            _draft.CopyShotKey = _hkCopyShot.Text.Trim();
            _draft.WindowShotKey = _hkWindowShot.Text.Trim();
            _draft.ColorPickerKey = _hkPicker.Text.Trim();
            _draft.OcrKey = _hkOcr.Text.Trim();
            _draft.GifKey = _hkGif.Text.Trim();
            _draft.ZoomInKey = _hkZoomIn.Text.Trim();
            _draft.ZoomOutKey = _hkZoomOut.Text.Trim();

            _draft.PenKey = _hkPen.Text.Trim();
            _draft.LineKey = _hkLine.Text.Trim();
            _draft.RectKey = _hkRect.Text.Trim();
            _draft.EllipseKey = _hkEllipse.Text.Trim();
            _draft.ArrowKey = _hkArrow.Text.Trim();
            _draft.TextKey = _hkText.Text.Trim();
            _draft.MosaicKey = _hkMosaic.Text.Trim();
            _draft.SpotlightKey = _hkSpotlight.Text.Trim();
            _draft.HighlighterKey = _hkHighlighter.Text.Trim();
            _draft.StepKey = _hkStep.Text.Trim();

            _draft.ClearKey = _hkClear.Text.Trim();
            _draft.FollowKey = _hkFollow.Text.Trim();
            _draft.UndoKey = _hkUndo.Text.Trim();
            _draft.RedoKey = _hkRedo.Text.Trim();
            _draft.SaveKey = _hkSave.Text.Trim();
            _draft.CopyKey = _hkCopy.Text.Trim();

            _draft.PenWidth = (int)_numWidth.Value;
            _draft.MinPenWidth = (int)_numMin.Value;
            _draft.MaxPenWidth = (int)_numMax.Value;
            _draft.SmoothLines = _chkSmooth.Checked;
            _draft.MagnifierDuringLiveZoom = _chkMagnifier.Checked;
            _draft.ZoomStepPercent = (int)_numStep.Value;
            _draft.MinZoom = (float)_numMinZoom.Value;
            _draft.MaxZoom = (float)_numMaxZoom.Value;

            _draft.TextSize = (float)_numTextSize.Value;
            _draft.MosaicBlock = (float)_numMosaic.Value;
            _draft.BrushCursorSize = (int)_numBrushSize.Value;
            _draft.ShowBrushCursor = _chkBrushCursor.Checked;
            _draft.ModCtrlRectangle = _chkModCtrl.Checked;
            _draft.ModTabEllipse = _chkModTab.Checked;
            _draft.ModShiftLine = _chkModShift.Checked;
            _draft.ModCtrlShiftArrow = _chkModCtrlShift.Checked;

            // Tier-1 controls.
            _draft.HighlighterAlpha = (int)_numHighlightAlpha.Value;
            _draft.ArrowStyleIndex = Math.Max(0, _cmbArrowStyle.SelectedIndex);
            _draft.StepBadgeSize = (float)_numStepBadge.Value;
            _draft.StepNumberPersistent = _chkStepPersistent.Checked;

            _draft.BeautifyEnabled = _chkBeautify.Checked;
            _draft.BeautifyPadding = (int)_numBeautifyPad.Value;
            _draft.BeautifyRadius = (int)_numBeautifyRadius.Value;
            _draft.BeautifyShadow = _chkBeautifyShadow.Checked;
            _draft.BeautifyColor1 = _btnBeautifyColor1.Text.Trim();
            _draft.BeautifyColor2 = _btnBeautifyColor2.Text.Trim();

            _draft.PickerLoupeSize = (int)_numPickerLoupe.Value;
            _draft.PickerZoom = (int)_numPickerZoom.Value;

            _draft.OcrCopyToClipboard = _chkOcrCopy.Checked;
            _draft.OcrKeepLineBreaks = _chkOcrLines.Checked;
            _draft.OcrUpscaleSmall = (int)_numOcrUpscale.Value;
            _draft.OcrLanguage = SelectedOcrLanguage();

            _draft.GifFps = (int)_numGifFps.Value;
            _draft.GifMaxSeconds = (int)_numGifMax.Value;
            _draft.GifCountdownSeconds = (int)_numGifCountdown.Value;
            _draft.GifShowBorder = _chkGifBorder.Checked;
            _draft.GifLoop = _chkGifLoop.Checked;
            _draft.GifSkipIdenticalFrames = _chkGifSkip.Checked;

            _draft.RegionDimOutside = _chkRegionDim.Checked;
            _draft.RegionShowSize = _chkRegionSize.Checked;

            _draft.ScrollFollowEnabled = _chkScrollFollow.Checked;
            _draft.WheelScrollsDocument = _chkWheelScroll.Checked;
            _draft.ScrollFollowClickThrough = _chkClickThrough.Checked;

            _draft.ShowTrayIcon = _chkTray.Checked;
            _draft.StartMinimized = _chkMinimized.Checked;
            _draft.RunAtStartup = _chkStartup.Checked;

            _draft.SaveScreenshotsToDefaultFolder = _chkAutoSave.Checked;
            _draft.ScreenshotFolder = _txtFolder.Text.Trim();
            _draft.ScreenshotFormat = _cmbFormat.SelectedItem as string ?? "png";
            _draft.PlaySoundOnScreenshot = _chkSound.Checked;

            _draft.Clamp();

            if (_draft.SaveScreenshotsToDefaultFolder &&
                !string.IsNullOrEmpty(_draft.ScreenshotFolder) &&
                !Directory.Exists(_draft.ScreenshotFolder))
            {
                try { Directory.CreateDirectory(_draft.ScreenshotFolder); }
                catch
                {
                    Warn("无法创建截图目录：\n" + _draft.ScreenshotFolder);
                    return;
                }
            }

            // Autostart is a real system change, so report failure honestly.
            if (_draft.RunAtStartup && !AutoStart.Apply(true))
            {
                Warn("无法写入开机自启项（可能是注册表权限问题）。\n其它设置仍会保存。");
            }
            else if (!_draft.RunAtStartup && AutoStart.IsEnabled())
            {
                AutoStart.Apply(false);
            }

            Result = _draft;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Info(string m) { _status.Text = m; _status.ForeColor = Color.FromArgb(255, 120, 200, 140); }
        void Warn(string m)
        {
            Prompt(m, "ScreenPen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public void SetStatus(string m, bool good)
        {
            _status.Text = m;
            _status.ForeColor = good ? Color.FromArgb(255, 120, 200, 140) : Color.FromArgb(255, 240, 140, 90);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BringToFront();
            Activate();
        }

        /// <summary>
        /// Releases the one Font this dialog owns directly.
        ///
        /// _paletteLabelFont is a field, not a local, so nothing else disposes
        /// it; without this override every open/close of the settings panel
        /// leaked one GDI font handle (the child controls dispose their own
        /// fonts through their parent, this one is not on any control's Font
        /// property once RebuildPalette has re-run).
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _paletteLabelFont != null) _paletteLabelFont.Dispose();
            base.Dispose(disposing);
        }
    }
}



