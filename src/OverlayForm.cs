using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// The full-screen annotation overlay.
    ///
    /// Design notes
    /// ------------
    /// All stroke geometry is stored in *screen* (virtual-desktop) coordinates.
    /// The view is then a pure affine transform (pan + zoom), so:
    ///   * strokes stay glued to the desktop content while panning/zooming, and
    ///   * undo/clear/screenshot all operate on a single canonical model.
    ///
    /// Live zoom uses a captured backdrop bitmap which is drawn scaled. When the
    /// cursor approaches a viewport edge the pan origin scrolls, giving the
    /// "mouse at the edge pushes the screen" behaviour that was requested.
    /// </summary>
    public sealed class OverlayForm : Form
    {
        Settings _cfg;
        readonly Func<Size> _dpi;

        // ---- model ----------------------------------------------------------
        readonly List<Stroke> _strokes = new List<Stroke>();
        readonly List<Pin> _pins = new List<Pin>();

        /// <summary>
        /// Upper bound on the raw samples stored for one freehand stroke.
        /// Stored points stay exactly the raw mouse samples (densification is
        /// render-time only), so this is a memory/CPU guard, not a quality
        /// setting; see OnMouseMove for why it exists.
        /// </summary>
        const int MaxStrokePoints = 20000;

        /// <summary>
        /// Minimum screen-space distance between two stored freehand samples.
        /// A mouse can emit move events far faster than the pointer actually
        /// travels (and Windows coalesces/repeats them), so a stroke drawn
        /// slowly used to accumulate long runs of near-identical points: pure
        /// memory and per-frame densify cost for no visible geometry. Samples
        /// closer than this to the last stored point are dropped, which keeps
        /// the stored list exactly raw mouse samples (never interpolated) while
        /// bounding the count. 1.5px is well below any visible wiggle, and every
        /// test hook steps 10px or more per move, so probe point counts are
        /// unchanged.
        /// </summary>
        const float MinSampleDist = 1.5f;

        // ---- densify cache --------------------------------------------------
        // DrawStroke used to rebuild Densify(new List<PointF>(pts), 3f) for
        // every smooth pen stroke on every Paint, so an unchanging finished
        // stroke was re-subdivided 60+ times a second forever. The result only
        // depends on the stored points and the zoom, so it is memoised here.
        //
        // The cached array is kept in *screen* coordinates: Densify depends
        // only on point differences, so a pure pan cannot invalidate it, and
        // the client-space result is recovered by projecting the cached points
        // (a cheap affine pass we already pay for when drawing them). The
        // sampled step is what the old client-space call used, converted by
        // the current zoom.
        //
        // Keyed by Stroke reference: Stroke is a plain sealed class with no
        // Equals/GetHashCode override, so the default reference identity is
        // exactly the "same stroke object" test we want.
        sealed class DenseEntry
        {
            public PointF[] Pts;
            public int Count;
            public float Zoom;
        }

        readonly Dictionary<Stroke, DenseEntry> _denseCache = new Dictionary<Stroke, DenseEntry>();

        /// <summary>
        /// Densified screen-space points for a smooth pen stroke, or null when
        /// densifying would not add anything (the caller then draws the raw
        /// samples, exactly as the uncached code did). Rebuilt only when the
        /// stroke's stored point count or the zoom has changed.
        ///
        /// This reproduces the old client-space call Densify(clientPoints, 3f)
        /// at every zoom: client space differs from screen space by a uniform
        /// scale _zoom plus a translation, and Densify's decisions depend only
        /// on point *differences* (its cubic coefficients sum to 1, so
        /// CatmullRom is translation-invariant). Scaling the stored points by
        /// _zoom and densifying with maxStep 3 therefore takes the same
        /// subdivision decisions as the old call, and the result is scaled back
        /// for caching - identical up to the float rounding of that round-trip.
        /// (Densifying the screen points directly with 3f/_zoom would *not* be
        /// equivalent: Densify clamps maxStep up to 1f, which the zoomed
        /// equivalent would hit once _zoom > 3.)
        /// </summary>
        PointF[] DensifiedFor(Stroke s)
        {
            if (s == null || s.Points.Count < 3) return null;

            DenseEntry e;
            if (!_denseCache.TryGetValue(s, out e)) e = null;
            if (e != null && e.Count == s.Points.Count && e.Zoom == _zoom) return e.Pts;

            float scale = _zoom > 0.0001f ? _zoom : 0.0001f;
            List<PointF> scaled = new List<PointF>(s.Points.Count);
            for (int i = 0; i < s.Points.Count; i++)
                scaled.Add(new PointF(s.Points[i].X * scale, s.Points[i].Y * scale));

            List<PointF> dense = Densify(scaled, 3f);

            // densify never returns fewer points than it was given
            PointF[] arr = null;
            if (dense.Count > s.Points.Count)
            {
                arr = new PointF[dense.Count];
                for (int i = 0; i < dense.Count; i++)
                    arr[i] = new PointF(dense[i].X / scale, dense[i].Y / scale);
            }

            if (e == null) { e = new DenseEntry(); _denseCache[s] = e; }
            e.Pts = arr;
            e.Count = s.Points.Count;
            e.Zoom = _zoom;
            return arr;
        }

        /// <summary>Drops memoised densification; call whenever stored geometry changes.</summary>
        void InvalidateDensifyCache()
        {
            _denseCache.Clear();
        }

        // ---- live drawing state --------------------------------------------
        bool _drawing;
        Stroke _current;
        PointF _dragStart;
        PointF _cursorScreen;

        // ---- view state -----------------------------------------------------
        Bitmap _backdrop;            // frozen screen snapshot used as the canvas
        Rectangle _screenBounds;
        float _zoom = 1f;
        PointF _origin;              // screen coord shown at viewport top-left
        bool _liveMode;              // zoom/pan interaction enabled

        // ---- palette / tool -------------------------------------------------
        Color[] _palette;
        int _colorIndex;
        Tool _tool = Tool.Pen;
        float _penWidth;
        Point _mouseDownClient;
        bool _rightDrag;

        // ---- modifier overrides ---------------------------------------------
        // Holding a modifier while dragging temporarily picks a different tool
        // (Ctrl = rectangle, Tab = ellipse, Shift = line, Ctrl+Shift = arrow).
        bool _tabHeld;
        Point _lastFollowCursor;
        bool _followSawMotion;
        /// <summary>Guards <see cref="CommitTextEditing"/> against re-entrancy.</summary>
        bool _committingText;
        Cursor _brushPointer;

        // ---- text tool ------------------------------------------------------
        TextBox _textEditor;         // live inline editor, created on demand
        Stroke _textStroke;          // the stroke being edited

        // ---- scroll-follow --------------------------------------------------
        bool _scrollFollow;
        Timer _followTimer;
        int[] _prevColProfile;
        int[] _prevRowProfile;
        // _followMisses is declared with the scroll-follow section below.
        int _followLastDx, _followLastDy;
        public event EventHandler ScrollFollowChanged;

        // ---- edge pan -------------------------------------------------------
        const int EdgeZone = 24;     // px from the viewport edge that triggers pan
        const int EdgeSpeed = 14;    // px per tick
        Timer _edgeTimer;

        static readonly Color HudBack = Color.FromArgb(210, 16, 18, 22);
        static readonly Color HudText = Color.FromArgb(255, 240, 242, 245);
        static readonly Color HudAccent = Color.FromArgb(255, 232, 160, 0);

        public OverlayForm(Settings cfg, Func<Size> dpiProvider)
        {
            _cfg = cfg;
            _dpi = dpiProvider;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Text = "ScreenPen Overlay";

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            _palette = cfg.ColorArray();
            _penWidth = cfg.PenWidth;
            _colorIndex = 0;

            _edgeTimer = new Timer();
            _edgeTimer.Interval = 30;
            _edgeTimer.Tick += (s, e) => EdgeTick();

            // Scroll-follow polls a low-resolution capture to work out how far
            // the underlying document moved, then shifts the annotations by the
            // same amount so they stay glued to the content.
            //
            // 140 ms is a deliberate compromise: fast enough to feel attached to
            // the page, slow enough that the capture does not dominate the UI
            // thread during a drag.
            _followTimer = new Timer();
            _followTimer.Interval = 140;
            _followTimer.Tick += (s, e) => FollowTick();

            KeyPreview = true;
        }

        public bool LiveMode { get { return _liveMode; } }

        /// <summary>
        /// Re-applies settings that can change while the app is running
        /// (palette, pen width, smoothing, zoom limits).
        /// </summary>
        public void ApplySettings(Settings cfg)
        {
            // Swap the reference so every later read (zoom limits, smoothing,
            // palette) picks up the new values without a restart.
            _cfg = cfg;
            _palette = cfg.ColorArray();
            if (_colorIndex >= _palette.Length) _colorIndex = 0;
            _penWidth = Math.Max(cfg.MinPenWidth, Math.Min(cfg.MaxPenWidth, _penWidth));
            ReleaseBrushPointer();                // rebuilt lazily at the new size
            ApplyCursorForMode();
            Invalidate();
        }

        /// <summary>
        /// Frees the custom brush pointer and forgets it.
        ///
        /// The cursor owns a real HICON plus two DIB sections (see
        /// BrushCursor.CreateCursor), so dropping the reference without calling
        /// Dispose() leaks all three GDI objects. Every colour change used to do
        /// exactly that, which meant holding the palette key down long enough
        /// would exhaust the 10 000-handle per-process GDI cap and make the
        /// whole app fail to paint.
        ///
        /// The Cursor is detached from the form before being disposed: assigning
        /// a disposed Cursor to a live window is undefined, so the caller must
        /// re-run ApplyCursorForMode() afterwards (which every call site does).
        /// </summary>
        void ReleaseBrushPointer()
        {
            if (_brushPointer == null) return;
            Cursor doomed = _brushPointer;
            _brushPointer = null;
            if (Cursor == doomed) Cursor = Cursors.Default;
            try { doomed.Dispose(); } catch { }
        }

        /// <summary>Picks the pointer: brush image while annotating, crosshair in zoom mode.</summary>
        void ApplyCursorForMode()
        {
            if (_liveMode) { Cursor = Cursors.Cross; return; }

            if (_cfg.ShowBrushCursor)
            {
                if (_brushPointer == null)
                {
                    try
                    {
                        Color c = _palette == null || _palette.Length == 0
                            ? Color.Red : _palette[Math.Max(0, Math.Min(_palette.Length - 1, _colorIndex))];
                        _brushPointer = BrushCursor.CreateCursor(
                            Math.Max(16, Math.Min(128, _cfg.BrushCursorSize)), c);
                    }
                    catch { ReleaseBrushPointer(); }
                }
                Cursor = _brushPointer ?? Cursors.Cross;
            }
            else
            {
                Cursor = Cursors.Cross;
            }
        }

        /// <summary>The pointer shown while the overlay is idle (non-drawing).</summary>
        public Cursor PointerForTest { get { return Cursor; } }

        // =====================================================================
        //  Activation
        // =====================================================================

        /// <summary>
        /// Shows the overlay over the whole virtual desktop, freezing the current
        /// screen as the drawing backdrop.
        /// </summary>
        public void ActivateOverlay(bool liveZoom)
        {
            _screenBounds = Native.VirtualScreen();

            // Hide briefly so we never capture our own window, then snapshot.
            bool wasVisible = Visible;
            if (wasVisible) Hide();
            Application.DoEvents();

            Bitmap shot = Native.CaptureRegion(_screenBounds);
            if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
            _backdrop = shot;

            Bounds = _screenBounds;
            _zoom = 1f;
            _origin = new PointF(_screenBounds.Left, _screenBounds.Top);

            _liveMode = liveZoom;
            _drawing = false;
            _current = null;

            Show();
            BringToFront();

            // TopMost must be re-asserted *after* Show(): the value set in the
            // constructor is frequently lost when the window is first mapped,
            // which would let other windows cover the overlay.
            TopMost = false;
            TopMost = true;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);

            Native.ForceForeground(Handle);
            Activate();
            ApplyCursorForMode();
            if (_liveMode) _edgeTimer.Start();

            // Scroll-follow is NOT enabled on activation. Each tick performs a
            // full-screen capture on the UI thread, which starves the message
            // loop and makes strokes come out empty; it is therefore an opt-in
            // mode the user turns on with the follow key once they are actually
            // annotating a scrollable document.
            StopScrollFollow();

            Invalidate();
        }

        public void DeactivateOverlay()
        {
            _edgeTimer.Stop();
            StopScrollFollow();
            CommitTextEditing(false);
            _drawing = false;
            _current = null;
            Hide();
        }

        /// <summary>
        /// Re-shows the overlay after a modal child (such as the region
        /// selector or the settings dialog) has been dismissed.
        ///
        /// Deliberately does NOT re-capture the backdrop: the user's annotations
        /// are positioned against that frozen image, and re-capturing would both
        /// discard their canvas and leave the strokes misaligned.
        /// </summary>
        public void ResumeAfterCapture()
        {
            if (_backdrop == null) return;
            Show();
            BringToFront();
            TopMost = false;
            TopMost = true;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            Native.ForceForeground(Handle);
            Activate();
            ApplyCursorForMode();
            if (_liveMode) _edgeTimer.Start();

            // Deliberately NOT re-enabling scroll-follow here. ActivateOverlay
            // keeps it off (each tick is a synchronous full-screen capture), and
            // silently turning it back on after a screenshot would contradict
            // that and re-introduce the stutter. The user re-arms it with the
            // follow key.
            StopScrollFollow();

            Invalidate();
        }

        // =====================================================================
        //  Tool resolution (including modifier overrides)
        // =====================================================================

        /// <summary>
        /// The tool a drag started now should use, taking the held modifiers
        /// into account. Ctrl+Shift wins over Ctrl alone, and Shift alone only
        /// forces a line, so the combinations never fight each other.
        /// </summary>
        Tool EffectiveTool()
        {
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;

            if (ctrl && shift && _cfg.ModCtrlShiftArrow) return Tool.Arrow;
            if (ctrl && _cfg.ModCtrlRectangle) return Tool.Rectangle;
            if (_tabHeld && _cfg.ModTabEllipse) return Tool.Ellipse;
            if (shift && _cfg.ModShiftLine) return Tool.Line;
            return _tool;
        }

        /// <summary>Human-readable name, used by the HUD and by tests.</summary>
        public static string ToolName(Tool t)
        {
            switch (t)
            {
                case Tool.Pen: return "画笔";
                case Tool.Line: return "直线";
                case Tool.Rectangle: return "矩形";
                case Tool.Ellipse: return "椭圆";
                case Tool.Arrow: return "箭头";
                case Tool.Text: return "文字";
                case Tool.Mosaic: return "马赛克";
                case Tool.Spotlight: return "聚光灯";
            }
            return "画笔";
        }

        /// <summary>
        /// Latches the Tab key. Tab is used as a held modifier for the ellipse
        /// override, and WinForms delivers no usable "Tab held" state, so the
        /// controller tracks it explicitly.
        /// </summary>
        public void SetTabHeld(bool down)
        {
            if (_tabHeld == down) return;
            _tabHeld = down;
            if (!_liveMode) Invalidate();
        }

        public bool TabHeld { get { return _tabHeld; } }

        /// <summary>
        /// Makes Tab (and the arrows) real input keys instead of dialog keys.
        /// Without this, WinForms consumes Tab for focus navigation and neither
        /// KeyDown nor the controller ever sees it, which would make the
        /// Tab-to-draw-an-ellipse override impossible to trigger.
        /// </summary>
        protected override bool IsInputKey(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Tab || code == Keys.Up || code == Keys.Down ||
                code == Keys.Left || code == Keys.Right || code == Keys.Enter)
                return true;
            return base.IsInputKey(keyData);
        }

        /// <summary>
        /// Last line of defence for Tab: a Form treats it as a navigation key at
        /// the message level, before IsInputKey is consulted.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab)
            {
                // While typing, Tab is not the ellipse modifier: latching it here
                // would leave _tabHeld stuck true while the user is editing text,
                // so the next drag after committing would silently be an ellipse.
                // Let the editor handle the key normally instead.
                if (_textEditor != null) return base.ProcessCmdKey(ref msg, keyData);
                SetTabHeld(true);
                // Swallow it: never let Tab move focus away from the overlay.
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Tab) SetTabHeld(false);
        }

        /// <summary>Human-readable name of the tool the pointer would draw with.</summary>
        public string EffectiveToolNameForTest()
        {
            return ToolName(EffectiveTool());
        }

        /// <summary>
        /// Reports which tool each modifier combination resolves to, so the
        /// override rules can be asserted without synthesising key state.
        /// </summary>
        public string EffectiveToolForModifiersForTest(bool ctrl, bool shift, bool tab)
        {
            bool savedTab = _tabHeld;
            _tabHeld = tab;
            Tool t;
            try
            {
                if (ctrl && shift && _cfg.ModCtrlShiftArrow) t = Tool.Arrow;
                else if (ctrl && _cfg.ModCtrlRectangle) t = Tool.Rectangle;
                else if (tab && _cfg.ModTabEllipse) t = Tool.Ellipse;
                else if (shift && _cfg.ModShiftLine) t = Tool.Line;
                else t = _tool;
            }
            finally { _tabHeld = savedTab; }
            return t.ToString();
        }

        /// <summary>Opens the inline text editor at a screen point (as a click would).</summary>
        public void TestBeginText(PointF screenPt)
        {
            SetTool(Tool.Text);
            TestBeginStroke(screenPt);
            BeginTextEditing(screenPt);
        }

        /// <summary>True while the inline text editor is on screen.</summary>
        public bool IsEditingTextForTest() { return _textEditor != null; }

        public string TextEditorContentForTest()
        {
            return _textEditor == null ? null : _textEditor.Text;
        }

        /// <summary>Types into the inline editor, bypassing keyboard injection.</summary>
        public void TestSetTextEditorContent(string s)
        {
            if (_textEditor != null) _textEditor.Text = s;
        }

        // =====================================================================
        //  Coordinate transforms
        // =====================================================================

        PointF ScreenToClientF(PointF p)
        {
            return new PointF((p.X - _origin.X) * _zoom, (p.Y - _origin.Y) * _zoom);
        }

        PointF ClientToScreenF(PointF p)
        {
            return new PointF(_origin.X + p.X / _zoom, _origin.Y + p.Y / _zoom);
        }

        // =====================================================================
        //  Zoom & pan
        // =====================================================================
        public void ZoomBy(int delta, Point anchorClient)
        {
            if (_backdrop == null) return;
            float factor = 1f + (_cfg.ZoomStepPercent / 100f);
            float target = delta > 0 ? _zoom * factor : _zoom / factor;
            ZoomTo(target, anchorClient);
        }

        public void ZoomTo(float target, Point anchorClient)
        {
            target = Math.Max(_cfg.MinZoom, Math.Min(_cfg.MaxZoom, target));
            if (Math.Abs(target - _zoom) < 0.0001f) return;

            // Keep the screen point under the cursor stationary.
            PointF anchorScreen = ClientToScreenF(new PointF(anchorClient.X, anchorClient.Y));
            _zoom = target;
            _origin = new PointF(
                anchorScreen.X - anchorClient.X / _zoom,
                anchorScreen.Y - anchorClient.Y / _zoom);
            ClampOrigin();
            Invalidate();
        }

        /// <summary>
        /// Keeps the viewport inside the desktop while zoomed in. When zoomed
        /// out past 1x the backdrop is centred instead.
        /// </summary>
        void ClampOrigin()
        {
            if (_screenBounds.Width <= 0 || _screenBounds.Height <= 0) return;
            float viewW = ClientSize.Width / _zoom;
            float viewH = ClientSize.Height / _zoom;

            if (viewW >= _screenBounds.Width)
                _origin.X = _screenBounds.Left - (viewW - _screenBounds.Width) / 2f;
            else
                _origin.X = Math.Max(_screenBounds.Left,
                           Math.Min(_screenBounds.Right - viewW, _origin.X));

            if (viewH >= _screenBounds.Height)
                _origin.Y = _screenBounds.Top - (viewH - _screenBounds.Height) / 2f;
            else
                _origin.Y = Math.Max(_screenBounds.Top,
                           Math.Min(_screenBounds.Bottom - viewH, _origin.Y));
        }

        void EdgeTick()
        {
            if (!_liveMode || _backdrop == null) return;
            if (_zoom <= 1.001f) return;

            Point p = PointToClient(Cursor.Position);
            float dx = 0, dy = 0;

            if (p.X <= EdgeZone) dx = -EdgeSpeed / _zoom;
            else if (p.X >= ClientSize.Width - EdgeZone) dx = EdgeSpeed / _zoom;

            if (p.Y <= EdgeZone) dy = -EdgeSpeed / _zoom;
            else if (p.Y >= ClientSize.Height - EdgeZone) dy = EdgeSpeed / _zoom;

            if (dx == 0 && dy == 0) return;

            float before = _origin.X + _origin.Y;
            _origin = new PointF(_origin.X + dx, _origin.Y + dy);
            ClampOrigin();
            if (Math.Abs((_origin.X + _origin.Y) - before) > 0.001f)
            {
                Invalidate();
            }
        }

        // =====================================================================
        //  Scroll-follow mode
        // =====================================================================

        /// <summary>
        /// Starts observing the document underneath so annotations can be shifted
        /// when it scrolls.
        ///
        /// Two things make this possible:
        ///   * the overlay marks itself excluded from screen capture, so the
        ///     frames it observes contain only the real document, and
        ///   * a low-resolution luminance capture is correlated between frames
        ///     to recover the scroll offset.
        /// </summary>
        public void StartScrollFollow()
        {
            _followRefusal = "";
            if (_liveMode || !_cfg.ScrollFollowEnabled) return;
            _scrollFollow = true;
            _followMisses = 0;
            _followLastDx = 0;
            _followLastDy = 0;

            // Exclude our own window from capture or the correlation would see
            // the annotations move with the viewport instead of the content.
            //
            // SetWindowDisplayAffinity fails on Windows versions/builds without
            // WDA_EXCLUDEFROMCAPTURE (it needs Win10 2004+), and on failure the
            // correlation keeps seeing our own annotations - the offset it
            // recovers is then wrong, which looks like the canvas drifting. Warn
            // instead of silently following badly. The return value used to be
            // discarded outright.
            //
            // Only record it when nothing more specific is set: StartScrollFollow
            // is not the only writer of _followRefusal and must not clobber a
            // caller's reason.
            if (!Native.SetExcludedFromCapture(Handle, true) && _followRefusal.Length == 0)
                _followRefusal = "系统不支持抓屏排除，跟随滚动可能不准";

            SeedFollowProfiles();
            _followTimer.Start();
            RaiseScrollFollowChanged();
        }

        public void StopScrollFollow()
        {
            if (!_scrollFollow) { _followTimer.Stop(); return; }
            _scrollFollow = false;
            _followTimer.Stop();
            try { Native.SetExcludedFromCapture(Handle, false); }
            catch { }

            _prevColProfile = null;
            _prevRowProfile = null;
            RaiseScrollFollowChanged();
        }

        void RaiseScrollFollowChanged()
        {
            EventHandler h = ScrollFollowChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public bool ScrollFollowActive { get { return _scrollFollow; } }

        /// <summary>
        /// Why the last <see cref="ToggleScrollFollow"/> did not change state, or
        /// an empty string when it succeeded. Previously pressing the follow key
        /// in live-zoom mode did nothing at all and gave no feedback, so the key
        /// simply looked broken.
        /// </summary>
        public string LastFollowRefusal { get { return _followRefusal; } }
        string _followRefusal = "";

        /// <summary>Human-readable reason follow mode cannot be used right now.</summary>
        public string FollowUnavailableReason()
        {
            if (_liveMode) return "实时缩放模式下无法使用跟随滚动";
            if (!_cfg.ScrollFollowEnabled) return "跟随滚动已在设置中关闭";
            return "";
        }

        /// <summary>Turns scroll-follow on/off and reports the new state.</summary>
        public bool ToggleScrollFollow()
        {
            _followRefusal = "";
            if (_scrollFollow)
            {
                StopScrollFollow();
            }
            else
            {
                string why = FollowUnavailableReason();
                if (why.Length > 0)
                {
                    // Record it instead of failing silently.
                    _followRefusal = why;
                    return false;
                }
                StartScrollFollow();
            }
            Invalidate();
            return _scrollFollow;
        }

        const int FollowDiv = 6;        // downscale factor for correlation
        const int FollowSearch = 22;    // max shift searched, in profile samples
        const int FollowMinScore = 18;  // minimum mean-abs-difference improvement

        void SeedFollowProfiles()
        {
            int w, h;
            byte[] gray = Native.CaptureGray(
                new Point(_screenBounds.Left, _screenBounds.Top),
                new Size(_screenBounds.Width, _screenBounds.Height),
                FollowDiv, out w, out h);
            if (gray == null) { _prevColProfile = null; _prevRowProfile = null; return; }
            BuildProfiles(gray, w, h);
        }

        /// <summary>
        /// Reduces a frame to two 1-D profiles: the mean luminance of each
        /// column and of each row. Comparing profiles instead of the full image
        /// makes the search for the scroll offset trivial and fast.
        /// </summary>
        void BuildProfiles(byte[] gray, int w, int h)
        {
            int[] cols, rows;
            BuildProfiles(gray, w, h, out cols, out rows);
            _prevColProfile = cols;
            _prevRowProfile = rows;
        }

        /// <summary>
        /// Shared profiling used by both the baseline seed and the live
        /// correlation, so the two can never drift apart.
        /// </summary>
        static void BuildProfiles(byte[] gray, int w, int h, out int[] cols, out int[] rows)
        {
            cols = new int[w];
            rows = new int[h];
            for (int y = 0; y < h; y++)
            {
                int rowSum = 0;
                int off = y * w;
                for (int x = 0; x < w; x++)
                {
                    int v = gray[off + x];
                    cols[x] += v;
                    rowSum += v;
                }
                rows[y] = rowSum;
            }
            for (int x = 0; x < w; x++) cols[x] /= Math.Max(1, h);
            for (int y = 0; y < h; y++) rows[y] /= Math.Max(1, w);
        }

        void FollowTick()
        {
            if (!_scrollFollow || !Visible) return;
            // Never run during a gesture: CaptureGray is a synchronous
            // full-screen StretchBlt + GetDIBits on the UI thread, and doing that
            // between mouse messages makes the drag stutter and lose points.
            if (_drawing) return;
            if (_textEditor != null) return;          // never disturb typing

            // Also skip while the pointer is actively moving: a scroll gesture is
            // a burst of wheel input followed by a pause, so waiting for the
            // pointer to settle costs nothing and keeps drawing responsive.
            Point now = Cursor.Position;
            bool moved = (now.X != _lastFollowCursor.X || now.Y != _lastFollowCursor.Y);
            _lastFollowCursor = now;
            if (moved && _followSawMotion)
            {
                _followSawMotion = false;
                return;
            }
            _followSawMotion = moved;

            int w, h;
            byte[] gray = Native.CaptureGray(
                new Point(_screenBounds.Left, _screenBounds.Top),
                new Size(_screenBounds.Width, _screenBounds.Height),
                FollowDiv, out w, out h);
            if (gray == null) return;

            float sdx, sdy;
            if (ComputeFollowShift(gray, w, h, out sdx, out sdy))
                ShiftAnnotations(sdx, sdy);

            // Leave follow mode once the document has clearly settled, so an
            // idle overlay stops capturing the screen every tick.
            if (_followMisses >= FollowIdleFrames)
            {
                StopScrollFollow();
                RaiseScrollFollowIdled();
            }
        }

        /// <summary>
        /// Correlates a fresh downscaled luminance frame against the previous one
        /// and reports how far the document content moved, in screen pixels.
        ///
        /// Kept separate from <see cref="FollowTick"/> so the correlation can be
        /// exercised with synthetic frames instead of live screen captures.
        /// </summary>
        public bool ComputeFollowShift(byte[] gray, int w, int h, out float sdx, out float sdy)
        {
            sdx = 0;
            sdy = 0;
            if (gray == null || w <= 0 || h <= 0) return false;

            int[] cols, rows;
            BuildProfiles(gray, w, h, out cols, out rows);

            int dx = 0, dy = 0;
            bool foundX = _prevColProfile != null && _prevColProfile.Length == w;
            bool foundY = _prevRowProfile != null && _prevRowProfile.Length == h;

            if (foundX) dx = BestShift(_prevColProfile, cols, FollowSearch);
            if (foundY) dy = BestShift(_prevRowProfile, rows, FollowSearch);

            // Update the baseline before deciding, so a rejected frame does not
            // poison the next comparison.
            _prevColProfile = cols;
            _prevRowProfile = rows;

            if (dx == 0 && dy == 0)
            {
                _followMisses++;
                return false;
            }
            _followMisses = 0;

            // Convert profile-sample shift back to screen pixels. A positive
            // shift means content moved toward larger coordinates.
            sdx = dx * FollowDiv;
            sdy = dy * FollowDiv;

            // Sanity bound: a single frame scrolling more than half the screen
            // is almost certainly a false match, not a real page scroll.
            if (Math.Abs(sdx) > _screenBounds.Width * 0.5f) sdx = 0;
            if (Math.Abs(sdy) > _screenBounds.Height * 0.5f) sdy = 0;

            // A rejected shift counts as a miss too; otherwise the idle counter
            // would be reset by a false match and follow mode would never stop.
            if (sdx == 0 && sdy == 0) _followMisses++;
            else _followMisses = 0;
            if (sdx == 0 && sdy == 0) return false;

            _followLastDx = dx;
            _followLastDy = dy;
            return true;
        }

        /// <summary>
        /// Finds the integer shift of <paramref name="cur"/> that best matches
        /// <paramref name="prev"/>. Returns 0 unless the match is clearly better
        /// than "no shift", so static content never drifts.
        /// </summary>
        static int BestShift(int[] prev, int[] cur, int maxShift)
        {
            int n = Math.Min(prev.Length, cur.Length);
            if (n < maxShift * 3) return 0;

            double zero = ProfileScore(prev, cur, 0, n);
            double best = zero;
            int bestShift = 0;
            for (int s = -maxShift; s <= maxShift; s++)
            {
                if (s == 0) continue;
                double v = ProfileScore(prev, cur, s, n);
                if (v < best - 0.001)
                {
                    best = v;
                    bestShift = s;
                }
            }

            // Require a meaningful improvement, otherwise report "no scroll".
            if (bestShift == 0 || zero - best < FollowMinScore) return 0;
            return bestShift;
        }

        /// <summary>
        /// Mean absolute difference between the two profiles over their overlap
        /// when <paramref name="cur"/> is shifted by <paramref name="shift"/>.
        /// </summary>
        static double ProfileScore(int[] prev, int[] cur, int shift, int n)
        {
            int start = Math.Max(0, -shift);
            int end = Math.Min(n, n - shift);
            if (end - start < n / 3) return double.MaxValue;
            long sum = 0;
            for (int i = start; i < end; i++)
                sum += Math.Abs(prev[i] - cur[i + shift]);
            return (double)sum / (end - start);
        }

        /// <summary>
        /// Moves every annotation by the given screen-space delta so it stays
        /// attached to the document content that just scrolled.
        /// </summary>
        public void ShiftAnnotations(float dx, float dy)
        {
            if (dx == 0 && dy == 0) return;
            foreach (Stroke s in _strokes)
            {
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = new PointF(s.Points[i].X + dx, s.Points[i].Y + dy);
            }
            if (_current != null)
            {
                for (int i = 0; i < _current.Points.Count; i++)
                    _current.Points[i] = new PointF(_current.Points[i].X + dx, _current.Points[i].Y + dy);
            }
            _dragStart = new PointF(_dragStart.X + dx, _dragStart.Y + dy);
            foreach (Pin p in _pins)
                p.Origin = new PointF(p.Origin.X + dx, p.Origin.Y + dy);
            // Points were rewritten in place, so the stored count and zoom are
            // unchanged and the memo would otherwise keep serving the pre-shift
            // geometry (see DensifiedFor).
            InvalidateDensifyCache();
            Invalidate();
        }

        /// <summary>
        /// Called after forwarding a wheel event. The scroll it causes lands a
        /// moment later, so poll a few times quickly rather than waiting for the
        /// next scheduled tick.
        /// </summary>
        void NoteManualScroll()
        {
            if (!_scrollFollow) return;
            // A wheel notch is proof the document is still moving, so it clears
            // the idle counter even if correlation happens to find nothing.
            _followMisses = 0;
            for (int i = 0; i < 3; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(24);
                FollowTick();
            }
        }

        /// <summary>Total number of annotations, including the in-progress one.</summary>
        public int StrokeCount { get { return _strokes.Count; } }

        /// <summary>Read-only snapshot of stroke positions (test/debug use).</summary>
        public List<PointF> FirstStrokePoints()
        {
            if (_strokes.Count == 0) return new List<PointF>();
            return new List<PointF>(_strokes[0].Points);
        }

        /// <summary>
        /// Consecutive correlation frames that found no scroll. Used to leave
        /// follow mode automatically once the document has clearly stopped
        /// moving.
        ///
        /// This counter previously existed but was never read: the increment and
        /// the reset were both there, with no consumer. The effect was that
        /// follow mode kept doing a synchronous full-screen capture every tick
        /// forever, burning CPU on a page that was never going to scroll again,
        /// with no way for the user to tell it had stopped being useful.
        /// </summary>
        int _followMisses;

        /// <summary>
        /// How many consecutive no-scroll frames before follow mode switches
        /// itself off. At the 140 ms tick this is roughly 2.8 s of stillness —
        /// long enough not to trip between two wheel notches, short enough that
        /// an idle overlay stops capturing promptly.
        /// </summary>
        const int FollowIdleFrames = 20;

        /// <summary>
        /// True when the overlay is excluding itself from screen capture, so a
        /// probe can assert the exclusion is always paired with a restore.
        /// </summary>
        public bool IsExcludedFromCaptureForTest { get { return _scrollFollow; } }

        /// <summary>Consecutive no-scroll frames observed (test/diagnostic use).</summary>
        public int FollowMissesForTest { get { return _followMisses; } }

        /// <summary>
        /// Whether a wheel event would currently be forwarded to the document
        /// instead of adjusting pen width. Mirrors the OnMouseWheel decision so a
        /// probe can assert the routing without synthesising OS input.
        /// </summary>
        public bool WheelGoesToDocumentForTest()
        {
            if (_liveMode) return false;
            if ((ModifierKeys & Keys.Control) == Keys.Control) return false;
            return _cfg.WheelScrollsDocument || _scrollFollow;
        }

        /// <summary>Raised when follow mode turns itself off after going idle.</summary>
        public event EventHandler ScrollFollowIdled;

        void RaiseScrollFollowIdled()
        {
            EventHandler h = ScrollFollowIdled;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>Last detected scroll offset, in profile samples (test use).</summary>
        public void LastScrollDelta(out int dx, out int dy)
        {
            dx = _followLastDx;
            dy = _followLastDy;
        }

        // =====================================================================
        //  Painting
        // =====================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            // A throwing Paint handler in WinForms is not a skipped frame: it
            // surfaces as an application error dialog, and because this window is
            // a borderless full-screen topmost form, that dialog can appear
            // *behind* the very window that failed to paint - an unrecoverable
            // freeze. Rendering is also the one place that touches raw GDI+ and
            // screen-sized bitmaps, so it is the likeliest place for a transient
            // failure (display change, lost device, OOM on a huge mosaic tile).
            //
            // Painted output is by definition re-derivable: the next Invalidate()
            // rebuilds it. Keeping the previous frame beats freezing the app.
            try
            {
                PaintCore(e.Graphics);
            }
            catch
            {
                // Deliberately swallowed - see above. Fall back to a flat fill so
                // the window does not keep stale pixels from another window.
                try
                {
                    using (SolidBrush back = new SolidBrush(Color.FromArgb(255, 22, 24, 28)))
                        e.Graphics.FillRectangle(back, 0, 0, ClientSize.Width, ClientSize.Height);
                }
                catch { }
            }
        }

        /// <summary>Real rendering. Called by <see cref="OnPaint"/>; see it for the exception policy.</summary>
        void PaintCore(Graphics g)
        {
            g.InterpolationMode = _zoom >= 1f
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = _cfg.SmoothLines ? SmoothingMode.AntiAlias : SmoothingMode.None;

            Rectangle clip = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            using (SolidBrush back = new SolidBrush(Color.FromArgb(255, 22, 24, 28)))
                g.FillRectangle(back, clip);

            // 1. backdrop
            if (_backdrop != null)
            {
                RectangleF dst = new RectangleF(
                    (_screenBounds.Left - _origin.X) * _zoom,
                    (_screenBounds.Top - _origin.Y) * _zoom,
                    _screenBounds.Width * _zoom,
                    _screenBounds.Height * _zoom);

                // Clamp the source so we never sample outside the bitmap when
                // the origin has been pushed past the desktop edge.
                float sx = Math.Max(0, (_origin.X - _screenBounds.Left));
                float sy = Math.Max(0, (_origin.Y - _screenBounds.Top));
                float sw = Math.Min(_screenBounds.Width - sx, ClientSize.Width / _zoom);
                float sh = Math.Min(_screenBounds.Height - sy, ClientSize.Height / _zoom);
                if (sw > 0 && sh > 0)
                {
                    RectangleF src = new RectangleF(
                        sx, sy, sw, sh);
                    RectangleF dstClipped = new RectangleF(
                        (sx + _screenBounds.Left - _origin.X) * _zoom,
                        (sy + _screenBounds.Top - _origin.Y) * _zoom,
                        sw * _zoom, sh * _zoom);
                    g.DrawImage(_backdrop, dstClipped, src, GraphicsUnit.Pixel);
                }
                else
                {
                    g.DrawImage(_backdrop, dst);
                }
            }

            // 2. pinned snapshots
            foreach (Pin pin in _pins)
            {
                if (pin.Image == null) continue;
                RectangleF d = new RectangleF(
                    (pin.Origin.X - _origin.X) * _zoom,
                    (pin.Origin.Y - _origin.Y) * _zoom,
                    pin.Image.Width * pin.Zoom * _zoom,
                    pin.Image.Height * pin.Zoom * _zoom);
                g.DrawImage(pin.Image, d);
            }

            // 3. strokes
            foreach (Stroke s in _strokes) DrawStroke(g, s);
            if (_current != null) DrawStroke(g, _current);

            // 4. cursor ring / HUD
            if (_liveMode) DrawZoomHud(g);
            else DrawDrawHud(g);
            DrawCursorRing(g);
        }

        /// <summary>
        /// Status strip shown while annotating: current tool, colour, pen width,
        /// and how many annotations are on screen. Without this the user has no
        /// way to tell which tool a modifier key has switched to.
        /// </summary>
        void DrawDrawHud(Graphics g)
        {
            Tool shown = _drawing ? EffectiveTool() : _tool;
            string tool = ToolName(shown);
            string mods = "";
            if (_drawing)
            {
                bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
                bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
                if (ctrl && shift && _cfg.ModCtrlShiftArrow) mods = "  (Ctrl+Shift)";
                else if (ctrl && _cfg.ModCtrlRectangle) mods = "  (Ctrl)";
                else if (_tabHeld && _cfg.ModTabEllipse) mods = "  (Tab)";
                else if (shift && _cfg.ModShiftLine) mods = "  (Shift)";
            }

            string hint;
            if (_followRefusal.Length > 0)
            {
                // Show why the follow key did nothing, otherwise the key looks
                // broken rather than unavailable.
                hint = _followRefusal;
            }
            else if (_cfg.ScrollFollowEnabled && _scrollFollow)
            {
                hint = "滚轮滚动网页 · 标注自动跟随 · F 关闭跟随 · [ ] 笔宽 · Esc 退出";
            }
            else
            {
                hint = "P/L/R/O/A/T/M/K 切换工具 · Ctrl 矩形 · Tab 椭圆 · Shift 直线 · [ ] 笔宽 · F 跟随";
            }

            string text = tool + mods + "   " + _strokes.Count + " 笔   " + hint;

            using (Font f = new Font("Microsoft YaHei UI", 9.5f))
            {
                SizeF sz = g.MeasureString(text, f);
                float w = sz.Width + 28;
                float h = sz.Height + 14;
                RectangleF box = new RectangleF((ClientSize.Width - w) / 2f,
                                                ClientSize.Height - h - 22, w, h);

                using (GraphicsPath path = RoundRect(box, 10))
                using (SolidBrush b = new SolidBrush(HudBack))
                    g.FillPath(b, path);
                using (SolidBrush b = new SolidBrush(HudText))
                    g.DrawString(text, f, b, box.X + 14, box.Y + 7);
            }

            // Colour + width swatch, left of the status strip.
            Color c = _palette == null || _palette.Length == 0
                ? Color.Red : _palette[Math.Max(0, Math.Min(_palette.Length - 1, _colorIndex))];
            float r = 9f;
            float cx = 26, cy = ClientSize.Height - 40;
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (Pen p = new Pen(Color.FromArgb(180, 255, 255, 255), 1.2f))
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        }

        /// <summary>
        /// Spline tension for smoothed pen strokes. 0.5 is GDI+'s default and
        /// gives a natural hand-drawn feel without the overshoot that higher
        /// values produce on sharp direction changes.
        /// </summary>
        public const float SmoothTension = 0.5f;

        /// <summary>
        /// Draws a polyline, optionally as a smooth spline.
        ///
        /// This exists because "平滑线条" (SmoothLines) used to only switch
        /// SmoothingMode.AntiAlias — which softens pixel edges but leaves the
        /// geometry a chain of straight segments. A fast circular stroke samples
        /// the mouse every few dozen pixels, so it rendered as a visible polygon.
        /// DrawCurve fits a Cardinal spline through the same points, so the
        /// stroke is actually round; antialiasing is applied on top of that.
        ///
        /// Exposed as a static so a probe can assert that a smooth request really
        /// does take the curve path instead of the polyline path.
        /// </summary>
        public static void DrawPolyline(Graphics g, Pen pen, PointF[] pts, bool smooth)
        {
            if (g == null || pen == null || pts == null || pts.Length < 2) return;

            // DrawCurve needs at least 3 points; below that a line is exact.
            if (smooth && pts.Length >= 3)
                g.DrawCurve(pen, pts, SmoothTension);
            else
                g.DrawLines(pen, pts);
        }

        /// <summary>
        /// Inserts interpolated points so no two consecutive stored points are
        /// further apart than <paramref name="maxStep"/>.
        ///
        /// Catmull-Rom through the original samples, which is the same curve
        /// family DrawCurve uses, so the interpolated points lie on the path the
        /// renderer would have drawn anyway. Used to keep stroke geometry dense
        /// enough that later operations (mosaic hit-testing, erasing, scroll
        /// shifting) do not see a coarse polygon.
        /// </summary>
        public static List<PointF> Densify(List<PointF> src, float maxStep)
        {
            List<PointF> outp = new List<PointF>();
            if (src == null || src.Count == 0) return outp;
            if (maxStep < 1f) maxStep = 1f;

            outp.Add(src[0]);
            for (int i = 0; i < src.Count - 1; i++)
            {
                PointF p0 = i > 0 ? src[i - 1] : src[i];
                PointF p1 = src[i];
                PointF p2 = src[i + 1];
                PointF p3 = (i + 2 < src.Count) ? src[i + 2] : src[i + 1];

                float dx = p2.X - p1.X, dy = p2.Y - p1.Y;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist <= maxStep)
                {
                    outp.Add(p2);
                    continue;
                }

                // The chord is shorter than the arc, so sizing purely by chord
                // distance leaves gaps slightly wider than requested. Walk the
                // parameter up in equal t steps and subdivide until every
                // emitted segment is within maxStep, with a bounded retry.
                int steps = (int)Math.Ceiling(dist / maxStep);
                if (steps < 1) steps = 1;

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (steps > 64) { steps = 64; break; }

                    PointF prev = p1;
                    float worst = 0f;
                    for (int s = 1; s <= steps; s++)
                    {
                        PointF cur = CatmullRom(p0, p1, p2, p3, (float)s / steps);
                        float gx = cur.X - prev.X, gy = cur.Y - prev.Y;
                        float g = (float)Math.Sqrt(gx * gx + gy * gy);
                        if (g > worst) worst = g;
                        prev = cur;
                    }
                    if (worst <= maxStep) break;
                    steps *= 2;
                }

                for (int s = 1; s <= steps; s++)
                    outp.Add(CatmullRom(p0, p1, p2, p3, (float)s / steps));
            }
            return outp;
        }

        /// <summary>Standard Catmull-Rom interpolation at parameter t in [0,1].</summary>
        static PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float x = 0.5f * ((2f * p1.X)
                + (-p0.X + p2.X) * t
                + (2f * p0.X - 5f * p1.X + 4f * p2.X - p3.X) * t2
                + (-p0.X + 3f * p1.X - 3f * p2.X + p3.X) * t3);
            float y = 0.5f * ((2f * p1.Y)
                + (-p0.Y + p2.Y) * t
                + (2f * p0.Y - 5f * p1.Y + 4f * p2.Y - p3.Y) * t2
                + (-p0.Y + 3f * p1.Y - 3f * p2.Y + p3.Y) * t3);
            return new PointF(x, y);
        }

        void DrawStroke(Graphics g, Stroke s)
        {
            if (s == null) return;

            // Tools that do not depend on a point list.
            if (s.Tool == Tool.Mosaic) { DrawMosaic(g, s); return; }
            if (s.Tool == Tool.Spotlight) { DrawSpotlight(g, s); return; }
            if (s.Tool == Tool.Text) { DrawText(g, s); return; }

            if (s.Points.Count == 0) return;
            float w = Math.Max(1f, s.Width * _zoom);
            using (Pen pen = new Pen(s.Color, w))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (s.Tool == Tool.Pen)
                {
                    if (s.Points.Count == 1)
                    {
                        PointF p = ScreenToClientF(s.Points[0]);
                        using (SolidBrush b = new SolidBrush(s.Color))
                            g.FillEllipse(b, p.X - w / 2f, p.Y - w / 2f, w, w);
                    }
                    else
                    {
                        // Try the memoised densification first: it is stored in
                        // screen space and depends only on the stored samples
                        // plus the zoom, so an unchanged finished stroke is no
                        // longer re-subdivided on every Paint (see
                        // DensifiedFor). Falls back to the raw samples exactly
                        // like the uncached code when densifying adds nothing.
                        PointF[] pts = null;
                        if (_cfg.SmoothLines)
                        {
                            PointF[] dense = DensifiedFor(s);
                            if (dense != null)
                            {
                                pts = new PointF[dense.Length];
                                for (int i = 0; i < dense.Length; i++)
                                    pts[i] = ScreenToClientF(dense[i]);
                            }
                        }

                        if (pts == null)
                        {
                            pts = new PointF[s.Points.Count];
                            for (int i = 0; i < s.Points.Count; i++)
                                pts[i] = ScreenToClientF(s.Points[i]);
                        }

                        // Only freehand strokes are splined. Line/Rectangle/
                        // Ellipse/Arrow below stay exact geometry.
                        DrawPolyline(g, pen, pts, _cfg.SmoothLines);
                    }
                }
                else if (s.Points.Count >= 2)
                {
                    PointF a = ScreenToClientF(s.Points[0]);
                    PointF b = ScreenToClientF(s.Points[s.Points.Count - 1]);
                    switch (s.Tool)
                    {
                        case Tool.Line:
                            g.DrawLine(pen, a, b);
                            break;
                        case Tool.Rectangle:
                            g.DrawRectangle(pen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                            break;
                        case Tool.Ellipse:
                            g.DrawEllipse(pen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                          Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                            break;
                        case Tool.Arrow:
                            DrawArrow(g, pen, a, b);
                            break;
                    }
                }
            }
        }

        /// <summary>Screen-space bounding box of a two-point or point-list stroke.</summary>
        RectangleF StrokeRect(Stroke s)
        {
            if (s == null || s.Points.Count == 0) return RectangleF.Empty;
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (PointF p in s.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Pixelates the backdrop inside the stroke rectangle. Sampling the
        /// frozen backdrop (not the screen) keeps it correct while zoomed and
        /// makes the effect stable across repaints.
        /// </summary>
        void DrawMosaic(Graphics g, Stroke s)
        {
            RectangleF r = StrokeRect(s);
            if (r.Width < 1f || r.Height < 1f) return;

            PointF tl = ScreenToClientF(new PointF(r.Left, r.Top));
            PointF br = ScreenToClientF(new PointF(r.Right, r.Bottom));
            RectangleF dst = RectangleF.FromLTRB(
                Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y),
                Math.Max(tl.X, br.X), Math.Max(tl.Y, br.Y));
            if (dst.Width < 1f || dst.Height < 1f) return;

            float block = Math.Max(2f, s.BlockSize * _zoom);

            // Preferred path: sample the real screen pixels under the region.
            Bitmap tile = null;
            // Hoisted out of the try so the finally can always free it: the
            // sampling bitmap is full-resolution for the selected rectangle, so
            // leaking one per Paint frame while dragging a large mosaic would
            // exhaust memory in seconds.
            Bitmap raw = null;
            try
            {
                Rectangle src = Rectangle.Round(r);
                if (_backdrop != null)
                {
                    src.Intersect(new Rectangle(_backdrop.Width > 0 ? 0 : 0, 0,
                                                _backdrop.Width, _backdrop.Height));
                }
                else
                {
                    src.Intersect(_screenBounds);
                }
                if (src.Width > 0 && src.Height > 0)
                {
                    raw = _backdrop != null
                        ? CropBackdrop(src)
                        : Native.CaptureRegion(src);
                    if (raw != null)
                    {
                        int bw = Math.Max(1, (int)Math.Round(src.Width / s.BlockSize));
                        int bh = Math.Max(1, (int)Math.Round(src.Height / s.BlockSize));
                        tile = new Bitmap(bw, bh);
                        using (Graphics tg = Graphics.FromImage(tile))
                        {
                            tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            tg.DrawImage(raw, 0, 0, bw, bh);
                        }
                    }
                }
            }
            catch { if (tile != null) { tile.Dispose(); tile = null; } }
            finally { if (raw != null) { try { raw.Dispose(); } catch { } raw = null; } }

            if (tile != null)
            {
                using (tile)
                {
                    // NearestNeighbor is what actually produces the mosaic look.
                    InterpolationMode saved = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(tile, dst);
                    g.InterpolationMode = saved;
                }
            }
            else
            {
                // Fallback so the tool still visibly does something even when
                // the screen cannot be sampled.
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 40, 40, 40)))
                    g.FillRectangle(b, dst);
            }

            // Outline the region while it is still being dragged.
            if (s == _current)
            {
                using (Pen p = new Pen(Color.FromArgb(200, 255, 255, 255), 1.4f))
                {
                    p.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(p, dst.X, dst.Y, dst.Width, dst.Height);
                }
            }
        }

        Bitmap CropBackdrop(Rectangle src)
        {
            try
            {
                // Backdrop is in virtual-screen coordinates; translate to bitmap space.
                Rectangle local = src;
                local.Offset(-_screenBounds.Left, -_screenBounds.Top);
                local.Intersect(new Rectangle(0, 0, _backdrop.Width, _backdrop.Height));
                if (local.Width <= 0 || local.Height <= 0) return null;
                Bitmap b = new Bitmap(local.Width, local.Height, PixelFormat.Format32bppArgb);
                using (Graphics bg = Graphics.FromImage(b))
                    bg.DrawImage(_backdrop, new Rectangle(0, 0, local.Width, local.Height),
                                 local, GraphicsUnit.Pixel);
                return b;
            }
            catch { return null; }
        }

        /// <summary>Dims everything outside the ellipse so the area pops.</summary>
        void DrawSpotlight(Graphics g, Stroke s)
        {
            RectangleF r = StrokeRect(s);
            if (r.Width < 1f || r.Height < 1f) return;

            PointF tl = ScreenToClientF(new PointF(r.Left, r.Top));
            PointF br = ScreenToClientF(new PointF(r.Right, r.Bottom));
            RectangleF dst = RectangleF.FromLTRB(
                Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y),
                Math.Max(tl.X, br.X), Math.Max(tl.Y, br.Y));

            int alpha = (int)Math.Round(Math.Max(0.05f, Math.Min(0.95f, _cfg.SpotlightDim)) * 255);

            // Fill the whole viewport except the ellipse. EvenOdd on a path made
            // of the viewport rectangle plus the ellipse yields exactly that.
            // GraphicsPath exposes FillMode.Alternate, which behaves as even-odd
            // for the viewport-plus-hole construct used here.
            using (GraphicsPath path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
                path.AddEllipse(dst);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    g.FillPath(b, path);
            }

            using (Pen p = new Pen(Color.FromArgb(220, 255, 255, 255), 1.6f))
                g.DrawEllipse(p, dst.X, dst.Y, dst.Width, dst.Height);
            if (s == _current)
            {
                using (Pen p = new Pen(Color.FromArgb(200, 255, 220, 80), 1.4f))
                {
                    p.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(p, dst.X, dst.Y, dst.Width, dst.Height);
                }
            }
        }

        /// <summary>Draws a text label with an optional contrasting outline.</summary>
        void DrawText(Graphics g, Stroke s)
        {
            if (s == null || string.IsNullOrEmpty(s.Text)) return;
            if (s.Points.Count == 0) return;

            PointF p = ScreenToClientF(s.Points[0]);
            float size = Math.Max(6f, s.FontSize * _zoom);
            using (Font f = new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font ff = new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                if (s.TextShadow)
                {
                    using (SolidBrush sh = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                    {
                        for (int dx = -2; dx <= 2; dx += 2)
                            for (int dy = -2; dy <= 2; dy += 2)
                                if (dx != 0 || dy != 0)
                                    g.DrawString(s.Text, ff, sh, p.X + dx, p.Y + dy);
                    }
                }
                using (SolidBrush b = new SolidBrush(s.Color))
                    g.DrawString(s.Text, f, b, p.X, p.Y);
            }

            if (s == _current)
            {
                // The measuring font must be disposed: this runs once per Paint
                // frame while the stroke is being dragged, and an undisposed Font
                // is a GDI object, so holding a Text tool drag would exhaust the
                // per-process GDI cap.
                SizeF sz;
                using (Font measure = new Font("Microsoft YaHei UI", size,
                                               FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    sz = TextRenderer.MeasureText(s.Text.Length == 0 ? " " : s.Text, measure);
                }
                if (string.IsNullOrEmpty(s.Text)) sz = new SizeF(size * 2, size);
                using (Pen pen = new Pen(Color.FromArgb(200, 255, 220, 80), 1.3f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(pen, p.X - 4, p.Y - 3, sz.Width + 8, sz.Height + 6);
                }
            }
        }

        static void DrawArrow(Graphics g, Pen pen, PointF a, PointF b)
        {
            g.DrawLine(pen, a, b);
            double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
            float head = Math.Max(8f, pen.Width * 3.2f);
            double spread = Math.PI / 7.0;

            PointF p1 = new PointF(
                b.X - (float)(head * Math.Cos(ang - spread)),
                b.Y - (float)(head * Math.Sin(ang - spread)));
            PointF p2 = new PointF(
                b.X - (float)(head * Math.Cos(ang + spread)),
                b.Y - (float)(head * Math.Sin(ang + spread)));

            using (SolidBrush brush = new SolidBrush(pen.Color))
            {
                g.FillPolygon(brush, new[] { b, p1, p2 });
                // Round the joint so thick arrows don't show a notch.
                g.DrawLine(pen, p1, b);
                g.DrawLine(pen, b, p2);
            }
        }

        /// <summary>Numeric zoom readout plus a short hint, top-centre.</summary>
        void DrawZoomHud(Graphics g)
        {
            string zoomText = string.Format("{0:0}%", Math.Round(_zoom * 100));
            string hint = "滚轮缩放 · 边缘移动 · Esc 退出";
            using (Font f = new Font("Microsoft YaHei UI", 10f))
            using (Font fb = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(zoomText, fb);
                SizeF sh = g.MeasureString(hint, f);
                float w = Math.Max(sz.Width, sh.Width) + 36;
                float h = sz.Height + sh.Height + 30;
                RectangleF box = new RectangleF((ClientSize.Width - w) / 2f, 18, w, h);

                using (GraphicsPath path = RoundRect(box, 12))
                using (SolidBrush b = new SolidBrush(HudBack))
                    g.FillPath(b, path);

                using (SolidBrush b = new SolidBrush(HudAccent))
                    g.DrawString(zoomText, fb, b,
                                 box.X + (w - sz.Width) / 2f, box.Y + 8);
                using (SolidBrush b = new SolidBrush(HudText))
                    g.DrawString(hint, f, b,
                                 box.X + (w - sh.Width) / 2f, box.Y + 12 + sz.Height);
            }
        }

        void DrawCursorRing(Graphics g)
        {
            if (!_liveMode) return;
            Point p = PointToClient(Cursor.Position);
            float r = 9f;
            using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.6f))
                g.DrawEllipse(pen, p.X - r, p.Y - r, r * 2, r * 2);
            using (Pen pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
                g.DrawEllipse(pen, p.X - r - 1, p.Y - r - 1, r * 2 + 2, r * 2 + 2);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // =====================================================================
        //  Mouse
        // =====================================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // ForceForeground, not the bare call: a plain SetForegroundWindow is
            // only a request and the window manager refuses it whenever we did
            // not last receive input - exactly the case after activating from the
            // tray icon. The rest of this class already uses the AttachThreadInput
            // bypass at every other activation point; a lone bare call here made
            // the first click after a tray activation unresponsive.
            Native.ForceForeground(Handle);

            if (e.Button == MouseButtons.Right)
            {
                _rightDrag = true;
                _mouseDownClient = e.Location;
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            PointF sp = ClientToScreenF(new PointF(e.X, e.Y));
            _dragStart = sp;
            _cursorScreen = sp;

            // The tool actually used may differ from the sticky tool because of
            // a held modifier (Ctrl/Tab/Shift/Ctrl+Shift).
            Tool use = EffectiveTool();

            if (use == Tool.Text)
            {
                BeginTextEditing(sp);
                return;
            }

            _drawing = true;
            _current = new Stroke
            {
                Tool = use,
                Color = _palette[Math.Max(0, Math.Min(_palette.Length - 1, _colorIndex))],
                Width = _penWidth,
                FontSize = _cfg.TextSize,
                BlockSize = _cfg.MosaicBlock
            };
            _current.Points.Add(sp);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_rightDrag)
            {
                int dx = e.X - _mouseDownClient.X;
                int dy = e.Y - _mouseDownClient.Y;
                if (Math.Abs(dx) > 0 || Math.Abs(dy) > 0)
                {
                    _origin = new PointF(_origin.X - dx / _zoom, _origin.Y - dy / _zoom);
                    _mouseDownClient = e.Location;
                    ClampOrigin();
                    Invalidate();
                }
                return;
            }

            _cursorScreen = ClientToScreenF(new PointF(e.X, e.Y));

            if (!_drawing || _current == null)
            {
                // Keep the HUD/tool readout live so modifier changes are visible.
                if (!_liveMode) Invalidate();
                return;
            }

            PointF sp = _cursorScreen;

            // Hard cap on stored samples. A pen stroke used to grow without
            // bound for as long as the button was held: every mouse-move added a
            // point, and each Paint then re-densified the whole list, so a long
            // stroke made the overlay progressively slower and heavier. 20 000
            // raw samples is far more than any real annotation (a mouse emits
            // roughly 100-200 samples/second, so this is several minutes of
            // unbroken drawing) while keeping the per-frame cost bounded.
            if (_current.Tool == Tool.Pen)
            {
                // Drop samples that have barely moved since the last stored one
                // (see MinSampleDist - raw samples are stored, just fewer of
                // them); still hard-capped in case a platform emits genuinely
                // long-distance moves without pause.
                bool farEnough = true;
                if (_current.Points.Count > 0)
                {
                    PointF last = _current.Points[_current.Points.Count - 1];
                    float dx = sp.X - last.X, dy = sp.Y - last.Y;
                    farEnough = (dx * dx + dy * dy) >= MinSampleDist * MinSampleDist;
                }
                if (farEnough && _current.Points.Count < MaxStrokePoints) _current.Points.Add(sp);
            }
            else
            {
                _current.Points.Clear();
                _current.Points.Add(_dragStart);
                _current.Points.Add(sp);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Right) { _rightDrag = false; return; }
            if (e.Button != MouseButtons.Left) return;
            if (!_drawing || _current == null) { _drawing = false; return; }

            _drawing = false;
            if (_current.Points.Count >= 2) _strokes.Add(_current);
            _current = null;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            if (_liveMode)
            {
                // In live-zoom mode the wheel changes magnification.
                ZoomBy(e.Delta, e.Location);
                return;
            }

            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;

            // Ctrl+wheel always adjusts pen width so the user never loses that
            // even when the plain wheel is routed to the document.
            if (ctrl)
            {
                AdjustPenWidth(e.Delta > 0 ? 1 : -1);
                return;
            }

            // Scroll-follow mode implies the wheel must reach the document.
            //
            // This was the actual "F 跟随有 bug" report. The F key toggles
            // _scrollFollow (annotation tracking) while wheel pass-through was
            // gated on the unrelated WheelScrollsDocument setting. With that
            // setting off - the default - pressing F announced "已开启跟随滚动"
            // but the wheel still adjusted pen width, so nothing ever scrolled,
            // no motion was ever detected, and the annotations never moved. The
            // mode was on and provably doing nothing.
            bool passToDocument = _cfg.WheelScrollsDocument || _scrollFollow;

            if (passToDocument)
            {
                // Forward the wheel to the real window underneath. The
                // follow-timer notices the resulting scroll and shifts the
                // annotations to match.
                Point screen = Cursor.Position;
                if (Native.IsScrollableWindowUnder(screen))
                {
                    _followRefusal = "";
                    Native.SendWheel(e.Delta, false);
                    NoteManualScroll();
                    return;
                }

                // Nothing scrollable under the cursor. Say so rather than
                // silently falling through to a pen-width change, which looks
                // like the wheel doing the wrong thing.
                if (_scrollFollow)
                {
                    _followRefusal = "光标下没有可滚动的窗口";
                    Invalidate();
                    return;
                }
            }

            AdjustPenWidth(e.Delta > 0 ? 1 : -1);
        }

        // =====================================================================
        //  Text tool (inline editor)
        // =====================================================================

        /// <summary>
        /// Places an editable TextBox at the click point. It is a real control so
        /// IME/CJK input, caret movement and paste all behave normally; on
        /// commit the text is converted into a stroke in screen coordinates.
        /// </summary>
        void BeginTextEditing(PointF screenPoint)
        {
            CommitTextEditing(true);

            _textStroke = new Stroke
            {
                Tool = Tool.Text,
                Color = _palette[Math.Max(0, Math.Min(_palette.Length - 1, _colorIndex))],
                Width = _penWidth,
                FontSize = _cfg.TextSize,
                TextShadow = true
            };
            _textStroke.Points.Add(screenPoint);
            _textStroke.Points.Add(screenPoint);

            PointF client = ScreenToClientF(screenPoint);
            float px = Math.Max(8f, _cfg.TextSize * _zoom);

            _textEditor = new TextBox
            {
                Font = new Font("Microsoft YaHei UI", px, FontStyle.Bold, GraphicsUnit.Pixel),
                ForeColor = _textStroke.Color,
                // NOTE: TextBox.BackColor throws ArgumentException ("控件不支持透明
                // 的背景色") for any colour with A < 255. The opaque equivalent of
                // the old 215-alpha tint is used instead; alpha is impossible here.
                BackColor = Color.FromArgb(20, 22, 26),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = false,
                Location = new Point((int)client.X, (int)client.Y),
                Width = 260,
                Text = ""
            };
            _textEditor.KeyDown += TextEditorKeyDown;
            _textEditor.LostFocus += TextEditorLostFocus;
            Controls.Add(_textEditor);
            _textEditor.BringToFront();

            // If another window still owns the foreground, Focus() is refused and
            // LostFocus fires immediately, which would commit the (still empty)
            // label and throw the editor away the instant it appeared. Claim the
            // foreground first so the caret really lands in the box.
            Native.ForceForeground(Handle);
            _textEditor.Focus();
            Invalidate();
        }

        /// <summary>
        /// Commits the label when the editor loses focus. Kept as a named method
        /// so <see cref="CommitTextEditing"/> can detach it before tearing the
        /// control down.
        /// </summary>
        void TextEditorLostFocus(object sender, EventArgs e)
        {
            // An empty editor normally just means the user clicked away without
            // typing, which discards the label. But the same event fires when the
            // window manager refuses our foreground claim: the caret never
            // arrives, LostFocus is raised at once, and the box would vanish
            // before a single keystroke could land. When that happens - nothing
            // typed, and the foreground is another application - take the
            // foreground back and keep editing instead of silently dropping it.
            if (_textEditor != null && _textEditor.Text.Length == 0 &&
                !_committingText && ForegroundIsForeign())
            {
                Native.ForceForeground(Handle);
                _textEditor.Focus();
                if (_textEditor.Focused) return;
            }
            CommitTextEditing(true);
        }

        /// <summary>True when the foreground window belongs to another process.</summary>
        bool ForegroundIsForeign()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            return pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        void TextEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                CommitTextEditing(false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                CommitTextEditing(true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        /// <summary>
        /// Ends text editing. When <paramref name="keep"/> is true a non-empty
        /// label becomes a stroke; otherwise the edit is discarded.
        /// </summary>
        public void CommitTextEditing(bool keep)
        {
            if (_textEditor == null || _textStroke == null) return;

            // Re-entrancy guard. Removing or disposing the TextBox makes it lose
            // focus, which fires the LostFocus handler below and calls straight
            // back into this method. Without the guard the inner call nulls the
            // fields and the outer call then dereferences null.
            if (_committingText) return;
            _committingText = true;

            string text = _textEditor.Text;
            Stroke st = _textStroke;

            // Detach the handlers *before* removing the control so nothing can
            // call back in while the control tree is being modified.
            try { _textEditor.KeyDown -= TextEditorKeyDown; } catch { }
            try { _textEditor.LostFocus -= TextEditorLostFocus; } catch { }

            // Clear the fields first: by the time the control is gone this method
            // must already look "finished" to any re-entrant caller.
            TextBox editor = _textEditor;
            _textEditor = null;
            _textStroke = null;

            try
            {
                Controls.Remove(editor);
                editor.Dispose();
            }
            catch { }

            try
            {
                if (keep && !string.IsNullOrEmpty(text))
                {
                    st.Text = text;
                    _strokes.Add(st);
                }
            }
            finally
            {
                _committingText = false;
            }

            Invalidate();
        }

        /// <summary>True while the inline text editor is open (used by tests).</summary>
        public bool IsEditingText { get { return _textEditor != null; } }

        /// <summary>Sets the text of the open editor, for automated testing.</summary>
        public void SetEditingText(string text)
        {
            if (_textEditor != null) _textEditor.Text = text;
        }

        // =====================================================================
        //  Actions
        // =====================================================================

        public void AdjustPenWidth(int direction)
        {
            _penWidth = Math.Max(_cfg.MinPenWidth,
                       Math.Min(_cfg.MaxPenWidth, _penWidth + direction));
            Invalidate();
        }

        public void SetPenWidth(int w)
        {
            _penWidth = Math.Max(_cfg.MinPenWidth, Math.Min(_cfg.MaxPenWidth, w));
            Invalidate();
        }

        public int PenWidth { get { return (int)Math.Round(_penWidth); } }

        public void Undo()
        {
            if (_strokes.Count == 0) return;
            _strokes.RemoveAt(_strokes.Count - 1);
            InvalidateDensifyCache();
            Invalidate();
        }

        public void Clear()
        {
            CommitTextEditing(false);
            _strokes.Clear();
            foreach (Pin p in _pins) if (p.Image != null) p.Image.Dispose();
            _pins.Clear();
            InvalidateDensifyCache();
            Invalidate();
        }

        public void SetTool(Tool t)
        {
            CommitTextEditing(true);
            _tool = t;
            Invalidate();
        }

        public Tool CurrentTool { get { return _tool; } }

        // =====================================================================
        //  Test hooks
        //
        //  These drive the *real* drawing pipeline without synthetic input, so a
        //  test can prove that a given tool genuinely renders something even
        //  when OS-level event injection is unreliable on the build machine.
        // =====================================================================

        /// <summary>Begins a stroke at a screen point using the current tool.</summary>
        public void TestBeginStroke(PointF screenPt)
        {
            _current = new Stroke();
            _current.Tool = EffectiveTool();
            _current.Color = _palette[Math.Max(0, Math.Min(_palette.Length - 1, _colorIndex))];
            _current.Width = _penWidth;
            _current.FontSize = _cfg.TextSize;
            _current.BlockSize = _cfg.MosaicBlock;
            _current.Points.Add(screenPt);
            _dragStart = screenPt;
            _drawing = true;
        }

        /// <summary>Extends the in-progress stroke, mirroring OnMouseMove.</summary>
        public void TestExtendStroke(PointF screenPt)
        {
            if (_current == null) return;
            if (_current.Tool == Tool.Pen) _current.Points.Add(screenPt);
            else
            {
                _current.Points.Clear();
                _current.Points.Add(_dragStart);
                _current.Points.Add(screenPt);
            }
        }

        /// <summary>Finishes the in-progress stroke, mirroring OnMouseUp.</summary>
        public void TestEndStroke()
        {
            if (_current == null) { _drawing = false; return; }
            if (_current.Points.Count >= 2 && _current.Tool != Tool.Text)
                _strokes.Add(_current);
            _current = null;
            _drawing = false;
            Invalidate();
        }

        /// <summary>Convenience: a complete drag in one call.</summary>
        public void TestStrokeDrag(Tool tool, PointF from, PointF to)
        {
            SetTool(tool);
            TestBeginStroke(from);
            for (int i = 1; i <= 12; i++)
            {
                float t = i / 12f;
                TestExtendStroke(new PointF(from.X + (to.X - from.X) * t,
                                            from.Y + (to.Y - from.Y) * t));
            }
            TestEndStroke();
        }

        /// <summary>Tool actually used by the last TestBeginStroke call.</summary>
        public string LastStrokeToolForTest()
        {
            if (_strokes.Count == 0) return "";
            return _strokes[_strokes.Count - 1].Tool.ToString();
        }

        public int LastStrokePointCountForTest()
        {
            if (_strokes.Count == 0) return 0;
            return _strokes[_strokes.Count - 1].Points.Count;
        }

        /// <summary>Number of committed strokes, for end-to-end click tests.</summary>
        public int StrokeCountForTest() { return _strokes.Count; }

        /// <summary>Clears annotations and any in-flight stroke/editor.</summary>
        public void ClearForTest()
        {
            CommitTextEditing(false);
            _strokes.Clear();
            _current = null;
            _drawing = false;
            InvalidateDensifyCache();
            Invalidate();
        }

        public void SelectColor(int index)
        {
            if (_palette.Length == 0) return;
            if (index < 0) index = 0;
            if (index >= _palette.Length) index = _palette.Length - 1;
            _colorIndex = index;
            // The brush cursor is tinted with the active colour, so it must be
            // rebuilt or it would keep showing the old one. Release first: the
            // old cursor owns a HICON and two DIBs that must not be leaked.
            ReleaseBrushPointer();
            ApplyCursorForMode();
            Invalidate();
        }

        public void CycleColor(int delta)
        {
            if (_palette.Length == 0) return;
            _colorIndex = ((_colorIndex + delta) % _palette.Length + _palette.Length) % _palette.Length;
            ReleaseBrushPointer();
            ApplyCursorForMode();
            Invalidate();
        }

        public int ColorIndex { get { return _colorIndex; } }

        /// <summary>Resets the live-zoom view back to 1:1 and re-centres.</summary>
        public void ResetView()
        {
            _zoom = 1f;
            _origin = new PointF(_screenBounds.Left, _screenBounds.Top);
            Invalidate();
        }

        // =====================================================================
        //  Export
        // =====================================================================

        /// <summary>
        /// Flattens the current view (backdrop + strokes) into a bitmap.
        /// When <paramref name="wholeDesktop"/> is true the entire virtual
        /// desktop is rendered at 1:1 instead of just the viewport.
        /// </summary>
        public Bitmap RenderComposite(bool wholeDesktop)
        {
            Rectangle area = wholeDesktop
                ? _screenBounds
                : new Rectangle(
                    (int)Math.Floor(_origin.X), (int)Math.Floor(_origin.Y),
                    Math.Max(1, (int)Math.Ceiling(ClientSize.Width / _zoom)),
                    Math.Max(1, (int)Math.Ceiling(ClientSize.Height / _zoom)));
            return RenderRegion(area);
        }

        /// <summary>
        /// Flattens the backdrop plus every annotation into a bitmap covering
        /// exactly <paramref name="area"/> in screen coordinates, rendered 1:1.
        ///
        /// The view transform is temporarily neutralised (both zoom *and*
        /// origin) rather than relying on a Graphics translation: DrawStroke
        /// converts through <see cref="ScreenToClientF"/>, so translating as
        /// well would apply the origin twice and shift every stroke.
        /// </summary>
        public Bitmap RenderRegion(Rectangle area)
        {
            CommitTextEditing(true);

            if (area.Width <= 0 || area.Height <= 0)
                return new Bitmap(1, 1, PixelFormat.Format32bppArgb);

            Bitmap bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = _cfg.SmoothLines ? SmoothingMode.AntiAlias : SmoothingMode.None;

                // Neutralise the view transform so screen coords map 1:1 onto
                // the output bitmap with the region's top-left at (0,0).
                float savedZoom = _zoom;
                PointF savedOrigin = _origin;
                _zoom = 1f;
                _origin = new PointF(area.X, area.Y);
                try
                {
                    if (_backdrop != null)
                    {
                        Rectangle src = area;
                        src.Offset(-_screenBounds.Left, -_screenBounds.Top);
                        src.Intersect(new Rectangle(0, 0, _backdrop.Width, _backdrop.Height));
                        if (src.Width > 0 && src.Height > 0)
                        {
                            RectangleF dst = new RectangleF(
                                src.X + _screenBounds.Left - area.X,
                                src.Y + _screenBounds.Top - area.Y,
                                src.Width, src.Height);
                            g.DrawImage(_backdrop, dst, src, GraphicsUnit.Pixel);
                        }
                    }

                    foreach (Pin pin in _pins)
                    {
                        if (pin.Image == null) continue;
                        g.DrawImage(pin.Image, pin.Origin.X - area.X, pin.Origin.Y - area.Y,
                                    pin.Image.Width * pin.Zoom, pin.Image.Height * pin.Zoom);
                    }

                    foreach (Stroke s in _strokes) DrawStroke(g, s);
                }
                finally
                {
                    _zoom = savedZoom;
                    _origin = savedOrigin;
                }
            }
            return bmp;
        }

        /// <summary>
        /// Composited view for the region selector: the frozen backdrop is
        /// captured *before* the selector opens, so no selector chrome leaks in.
        /// </summary>
        public Bitmap CurrentViewForRegionPick()
        {
            return RenderComposite(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_edgeTimer != null) { _edgeTimer.Stop(); _edgeTimer.Dispose(); _edgeTimer = null; }
                if (_followTimer != null) { _followTimer.Stop(); _followTimer.Dispose(); _followTimer = null; }
                try { Native.SetExcludedFromCapture(Handle, false); } catch { }
                if (_backdrop != null) { _backdrop.Dispose(); _backdrop = null; }
                if (_brushPointer != null) { _brushPointer.Dispose(); _brushPointer = null; }
                // Drop the memo so nothing can outlive the strokes it references.
                InvalidateDensifyCache();
                foreach (Pin p in _pins) if (p.Image != null) p.Image.Dispose();
                _pins.Clear();
            }
            base.Dispose(disposing);
        }
    }
}

