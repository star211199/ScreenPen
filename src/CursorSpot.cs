using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// 鼠标浮标：a semi-transparent circle pinned to the pointer anywhere on the
    /// desktop, plus an optional click ripple. Modelled on the hover dot and
    /// click indicator of cuiliang/ClickShow.
    ///
    /// Two top-level layered windows do the work:
    ///
    ///  * <see cref="SpotWindow"/> is small and is moved onto the pointer every
    ///    tick. Keeping it small is what makes a 60 Hz loop affordable: its whole
    ///    surface is a few hundred kilobytes, so clearing and re-uploading it
    ///    costs well under a millisecond.
    ///  * <see cref="RippleWindow"/> is sized to the union of the live ripples,
    ///    because a ripple has to stay where it was clicked while the pointer
    ///    moves on. It is only shown while at least one ripple is alive, so an
    ///    idle desktop pays nothing for it.
    ///
    /// Both are WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE. The circle
    /// sits exactly where the user clicks, so it must never swallow a click, and
    /// it must never take focus from whatever is being demonstrated.
    ///
    /// Click detection polls GetAsyncKeyState rather than installing a mouse
    /// hook; see Native.IsMouseButtonDown for why that is the more robust choice.
    /// </summary>
    public sealed class CursorSpotOverlay : IDisposable
    {
        /// <summary>
        /// The follow loop. Windows' timer resolution is ~15.6 ms, so asking for
        /// anything smaller would simply be rounded up to this.
        /// </summary>
        const int TickMs = 15;

        /// <summary>How long one click ripple takes to expand and fade out.</summary>
        const int RippleMs = 600;

        /// <summary>Starting alpha of a ripple ring, before the fade.</summary>
        const int RippleAlpha = 220;

        /// <summary>
        /// Hard cap on simultaneous ripples. A held or bouncing button must not
        /// be able to grow the list without bound.
        /// </summary>
        const int MaxRipples = 12;

        /// <summary>Re-assert topmost about once a second.</summary>
        const int RaiseEveryTicks = 60;

        Settings _cfg;
        Settings _preview;
        Timer _timer;
        SpotWindow _spot;
        RippleWindow _ripple;

        bool _running;
        bool _spotShown, _rippleShown;

        /// <summary>
        /// Depth of outstanding hide requests. Nested so a whole GIF recording
        /// can hold the circle down while individual frame grabs also ask for it.
        /// </summary>
        int _suspends;

        // Smoothed pointer position, in physical screen pixels.
        float _x, _y;
        bool _hasPosition;

        int _drawnSpotX = int.MinValue, _drawnSpotY = int.MinValue;
        bool _spotDirty = true;

        // Previous button states, for rising-edge detection.
        bool _leftDown, _rightDown, _middleDown;

        readonly List<Ripple> _ripples = new List<Ripple>();
        int _lastTick;
        int _ticksSinceRaise;
        Rectangle _virtualScreen;
        float _scale;

        public CursorSpotOverlay()
        {
            _timer = new Timer();
            _timer.Interval = TickMs;
            _timer.Tick += delegate { Tick(); };

            _spot = new SpotWindow();
            _ripple = new RippleWindow();

            // Every screen grab in the app funnels through Native.CaptureRegion;
            // this is how the circle gets out of the way of our own screenshots
            // and GIF frames without every call site having to remember.
            Native.CaptureGuard = OnCaptureGuard;
        }

        // =====================================================================
        //  Public surface
        // =====================================================================

        /// <summary>True while the follow loop is actually ticking.</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>The settings in force, previewing a draft when one is set.</summary>
        Settings Active { get { return _preview != null ? _preview : _cfg; } }

        bool SpotOn { get { return _preview != null || (_cfg != null && _cfg.CursorSpotEnabled); } }
        bool RippleOn { get { return _preview != null || (_cfg != null && _cfg.CursorRippleEnabled); } }
        bool Wanted { get { return SpotOn || RippleOn; } }

        /// <summary>
        /// Adopts a new set of settings. Safe to call at any time; it starts or
        /// stops the loop to match.
        /// </summary>
        public void ApplySettings(Settings cfg)
        {
            _cfg = cfg;
            _scale = 0f;
            _spotDirty = true;
            Sync();
        }

        /// <summary>
        /// Draws from <paramref name="draft"/> even when the feature is switched
        /// off, so the settings dialog can show live changes while a slider is
        /// dragged. Cleared by <see cref="EndPreview"/>.
        ///
        /// Without this the user would be tuning a circle they cannot see: the
        /// dialog is modal, and the feature is normally off while they set it up.
        /// </summary>
        public void BeginPreview(Settings draft)
        {
            if (draft == null) return;
            _preview = draft;
            _scale = 0f;
            _spotDirty = true;
            Sync();
        }

        /// <summary>Drops a preview and goes back to the saved settings.</summary>
        public void EndPreview()
        {
            if (_preview == null) return;
            _preview = null;
            _scale = 0f;
            _spotDirty = true;
            Sync();
        }

        /// <summary>
        /// Hides the overlay and stops the loop. Nestable, and the matching
        /// <see cref="Resume"/> restores whatever the settings asked for.
        /// </summary>
        public void Suspend()
        {
            _suspends++;
            if (_suspends == 1) StopInternal();
        }

        /// <summary>Undoes one <see cref="Suspend"/>.</summary>
        public void Resume()
        {
            if (_suspends <= 0) return;
            _suspends--;
            if (_suspends == 0) Sync();
        }

        /// <summary>Test seam: how many nested suspends are outstanding.</summary>
        internal int SuspendDepthForTest { get { return _suspends; } }

        void Sync()
        {
            if (_suspends > 0) StopInternal();
            else if (Wanted) StartInternal();
            else StopInternal();
        }

        /// <summary>
        /// Runs for the duration of one screen grab. The flush matters: hiding a
        /// window is synchronous, but the desktop surface BitBlt reads is one
        /// composition behind, so without it the circle can still land in the
        /// very screenshot it was hidden for.
        /// </summary>
        void OnCaptureGuard(bool entering)
        {
            if (entering)
            {
                _suspends++;
                if (_suspends == 1)
                {
                    HideSpot();
                    HideRipple();
                    Native.FlushComposition();
                }
            }
            else
            {
                if (_suspends > 0) _suspends--;
                if (_suspends == 0)
                {
                    // Hiding the window is not the same as stopping the loop: the
                    // loop is still ticking and its cached "already drawn at
                    // (x,y)" would suppress the redraw, so the circle would never
                    // come back until the pointer moved. Forcing the dirty flag is
                    // what puts it back on screen immediately.
                    _spotDirty = true;
                    Sync();
                }
            }
        }

        // =====================================================================
        //  Loop
        // =====================================================================

        void StartInternal()
        {
            if (_running) return;
            _running = true;

            _hasPosition = false;
            _drawnSpotX = int.MinValue;
            _drawnSpotY = int.MinValue;
            _spotDirty = true;
            _ripples.Clear();
            _scale = 0f;
            _lastTick = Environment.TickCount;
            _ticksSinceRaise = 0;
            _virtualScreen = Native.VirtualScreen();

            // Prime the button states, or a button already held when the feature
            // is switched on would fire a ripple on the very first tick.
            _leftDown = Native.IsMouseButtonDown(Native.VK_LBUTTON);
            _rightDown = Native.IsMouseButtonDown(Native.VK_RBUTTON);
            _middleDown = Native.IsMouseButtonDown(Native.VK_MBUTTON);

            _timer.Start();
        }

        void StopInternal()
        {
            if (!_running) return;
            _running = false;
            _timer.Stop();
            _ripples.Clear();
            HideSpot();
            HideRipple();
        }

        void Tick()
        {
            int now = Environment.TickCount;
            int dtMs = unchecked(now - _lastTick);
            _lastTick = now;
            // A resumed-from-sleep machine or a debugger pause can hand us an
            // enormous delta; clamping keeps the easing from teleporting the
            // circle halfway across the screen.
            if (dtMs < 1 || dtMs > 250) dtMs = TickMs;

            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;

            if (!_hasPosition) { _x = p.X; _y = p.Y; _hasPosition = true; }

            float k = EaseFactor(Active.CursorSpotSmoothing, dtMs / 1000f);
            _x += (p.X - _x) * k;
            _y += (p.Y - _y) * k;

            if (SpotOn)
            {
                int sx = (int)Math.Round(_x);
                int sy = (int)Math.Round(_y);
                if (_spotDirty || sx != _drawnSpotX || sy != _drawnSpotY) DrawSpot(sx, sy);
            }
            else HideSpot();

            if (RippleOn) SampleClicks(now, p.X, p.Y);
            if (_ripples.Count > 0) DrawRipples(now);
            else HideRipple();

            // Topmost windows are ordered by when they were last raised, so a
            // window that appears later - the annotation overlay, a pin - would
            // sit above the circle. Re-asserting once a second is cheap and means
            // the circle cannot silently end up buried behind something.
            if (++_ticksSinceRaise >= RaiseEveryTicks)
            {
                _ticksSinceRaise = 0;
                _scale = 0f;          // the pointer may be on another monitor now
                RaiseTopMost();
            }
        }

        /// <summary>
        /// Fraction of the remaining distance to the pointer to close this tick.
        ///
        /// <paramref name="smoothing"/> is the user's 0-90 slider: 0 means glued
        /// to the pointer, and higher values trade lag for a calmer trail. The
        /// curve is time-based rather than per-frame so the feel does not change
        /// with the tick rate.
        /// </summary>
        internal static float EaseFactor(int smoothing, float dtSeconds)
        {
            if (smoothing <= 0) return 1f;
            if (smoothing > 90) smoothing = 90;
            if (dtSeconds <= 0f) return 0f;
            double tau = smoothing / 100.0 * 0.35;
            if (tau < 0.0005) return 1f;
            double k = 1.0 - Math.Exp(-dtSeconds / tau);
            if (k < 0.0) k = 0.0;
            if (k > 1.0) k = 1.0;
            return (float)k;
        }

        // =====================================================================
        //  Drawing
        // =====================================================================

        /// <summary>
        /// Physical pixels per authored pixel.
        ///
        /// Recomputed on start and once a second thereafter, which is what makes
        /// the circle keep its apparent size when the pointer crosses onto a
        /// monitor with a different scaling factor. The app is per-monitor DPI
        /// aware, so the window's own DC reports the monitor it currently sits on.
        /// </summary>
        float Scale()
        {
            if (_scale > 0f) return _scale;
            float s = 1f;
            try
            {
                if (_spot != null && _spot.Handle != IntPtr.Zero)
                {
                    using (Graphics g = _spot.CreateGraphics()) s = g.DpiX / 96f;
                }
            }
            catch { }
            if (s <= 0.1f || s > 8f) s = 1f;
            _scale = s;
            return s;
        }

        void DrawSpot(int cx, int cy)
        {
            Settings s = Active;
            float scale = Scale();

            int radius = Math.Max(2, (int)Math.Round(s.CursorSpotRadius * scale));
            int ringW = Math.Max(0, (int)Math.Round(s.CursorSpotRingWidth * scale));

            // An alpha of 0 is a legitimate way to ask for "no fill" or "no ring",
            // so it is honoured rather than being treated as "unset".
            Color fill = Color.FromArgb(ClampByte(s.CursorSpotFillAlpha),
                                        ParseOr(s.CursorSpotFillColor, Color.FromArgb(255, 204, 0)));
            Color ring = Color.FromArgb(ClampByte(s.CursorSpotRingAlpha),
                                        ParseOr(s.CursorSpotRingColor, Color.White));
            if (s.CursorSpotFillAlpha <= 0) fill = Color.FromArgb(0, fill.R, fill.G, fill.B);
            if (ringW <= 0 || s.CursorSpotRingAlpha <= 0) ringW = 0;

            ShowSpot();
            _spot.Place(cx, cy, radius, ringW, fill, ring);

            _drawnSpotX = cx;
            _drawnSpotY = cy;
            _spotDirty = false;
        }

        void DrawRipples(int now)
        {
            Settings s = Active;
            float scale = Scale();
            float maxR = Math.Max(4f, s.CursorRippleSize * 0.5f * scale);
            float penW = Math.Max(1f, 3f * scale);

            List<RippleDraw> draws = new List<RippleDraw>();
            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                Ripple r = _ripples[i];
                int age = unchecked(now - r.Start);
                if (age < 0 || age >= RippleMs) { _ripples.RemoveAt(i); continue; }

                float t = age / (float)RippleMs;
                // Ease-out: the ring leaves the click quickly and settles as it
                // fades. A linear ring reads as a slow, dead circle.
                float grow = 1f - (1f - t) * (1f - t);
                int a = (int)(RippleAlpha * (1f - t));
                if (a <= 0) continue;

                RippleDraw d = new RippleDraw();
                d.X = r.X;
                d.Y = r.Y;
                d.Radius = maxR * grow;
                d.MaxRadius = maxR;
                d.Width = penW;
                d.Color = Color.FromArgb(a, r.Color);
                draws.Add(d);
            }

            if (draws.Count == 0) { HideRipple(); return; }

            ShowRipple();
            _ripple.Render(draws, _virtualScreen);
        }

        void SampleClicks(int now, int cx, int cy)
        {
            bool l = Native.IsMouseButtonDown(Native.VK_LBUTTON);
            bool r = Native.IsMouseButtonDown(Native.VK_RBUTTON);
            bool m = Native.IsMouseButtonDown(Native.VK_MBUTTON);

            // Rising edges only: holding a button must produce one ripple, not
            // sixty a second.
            if (l && !_leftDown) AddRipple(now, cx, cy, Active.CursorRippleLeftColor);
            if (r && !_rightDown) AddRipple(now, cx, cy, Active.CursorRippleRightColor);
            if (m && !_middleDown) AddRipple(now, cx, cy, Active.CursorRippleMiddleColor);

            _leftDown = l;
            _rightDown = r;
            _middleDown = m;
        }

        void AddRipple(int now, int cx, int cy, string hex)
        {
            if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);

            Ripple r = new Ripple();
            r.Start = now;
            // Anchored on the real pointer, not the smoothed one: the whole point
            // of the ripple is to mark where the click actually landed.
            r.X = cx;
            r.Y = cy;
            r.Color = ParseOr(hex, Color.White);
            _ripples.Add(r);
        }

        static int ClampByte(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        static Color ParseOr(string hex, Color fallback)
        {
            Color c;
            return Settings.TryParseColor(hex, out c) ? c : fallback;
        }

        // =====================================================================
        //  Window visibility
        // =====================================================================

        void ShowSpot()
        {
            if (_spotShown) return;
            _spot.Show();
            _spotShown = true;
            RaiseTopMost();
        }

        void HideSpot()
        {
            if (!_spotShown) return;
            _spotShown = false;
            try { _spot.Hide(); } catch { }
        }

        void ShowRipple()
        {
            if (_rippleShown) return;
            _ripple.Show();
            _rippleShown = true;
            RaiseTopMost();
        }

        void HideRipple()
        {
            if (!_rippleShown) return;
            _rippleShown = false;
            try { _ripple.Hide(); } catch { }
        }

        void RaiseTopMost()
        {
            try
            {
                uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
                if (_spotShown && _spot != null && _spot.IsHandleCreated)
                    Native.SetWindowPos(_spot.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
                if (_rippleShown && _ripple != null && _ripple.IsHandleCreated)
                    Native.SetWindowPos(_ripple.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        public void Dispose()
        {
            try { StopInternal(); } catch { }

            if (Native.CaptureGuard == OnCaptureGuard) Native.CaptureGuard = null;

            if (_timer != null) { try { _timer.Dispose(); } catch { } _timer = null; }
            if (_spot != null) { try { _spot.Dispose(); } catch { } _spot = null; }
            if (_ripple != null) { try { _ripple.Dispose(); } catch { } _ripple = null; }
        }

        // =====================================================================
        //  Test seams
        //
        //  The probe drives the real Tick() rather than a reimplementation, so a
        //  green run means the shipped loop was exercised.
        // =====================================================================

        internal void TickForTest() { Tick(); }

        internal Bitmap SpotSurfaceForTest { get { return _spot == null ? null : _spot.Surface; } }
        internal Bitmap RippleSurfaceForTest { get { return _ripple == null ? null : _ripple.Surface; } }
        internal Rectangle SpotWindowRectForTest { get { return _spot == null ? Rectangle.Empty : _spot.LastRect; } }
        internal Rectangle RippleWindowRectForTest { get { return _ripple == null ? Rectangle.Empty : _ripple.LastRect; } }
        internal int SpotSideForTest { get { return _spot == null ? 0 : _spot.SideForTest; } }

        /// <summary>
        /// The spot window's HWND, or zero before it has been shown. The probe
        /// reads the real extended styles from it rather than trusting the
        /// CreateParams override to have been honoured.
        /// </summary>
        internal IntPtr SpotHandleForTest
        {
            get
            {
                if (_spot == null || !_spot.IsHandleCreated) return IntPtr.Zero;
                return _spot.Handle;
            }
        }
        internal bool SpotVisibleForTest { get { return _spotShown; } }
        internal bool RippleVisibleForTest { get { return _rippleShown; } }
        internal int RippleCountForTest { get { return _ripples.Count; } }

        internal void AddRippleForTest(int cx, int cy, string hex)
        {
            AddRipple(Environment.TickCount, cx, cy, hex);
        }

        internal void AgeRipplesForTest(int ms)
        {
            for (int i = 0; i < _ripples.Count; i++) _ripples[i].Start -= ms;
        }

        /// <summary>Forces a redraw at a chosen pointer position, without a timer.</summary>
        internal void RenderAtForTest(int cx, int cy)
        {
            _x = cx;
            _y = cy;
            _hasPosition = true;
            _drawnSpotX = int.MinValue;
            _spotDirty = true;
            if (SpotOn) DrawSpot(cx, cy);
            if (_ripples.Count > 0) DrawRipples(Environment.TickCount);
        }
    }

    /// <summary>One animating click ring, anchored in screen coordinates.</summary>
    internal sealed class Ripple
    {
        public int Start;
        public int X, Y;
        public Color Color;
    }

    /// <summary>One ring resolved to concrete geometry for the current frame.</summary>
    internal struct RippleDraw
    {
        /// <summary>Centre in screen pixels.</summary>
        public float X, Y;
        /// <summary>Radius right now, which grows over the ripple's life.</summary>
        public float Radius;
        /// <summary>Radius the ripple will reach, used only to size the window.</summary>
        public float MaxRadius;
        public float Width;
        public Color Color;
    }

    /// <summary>
    /// Base for the two overlay windows: a borderless topmost form whose pixels
    /// come entirely from a 32bpp ARGB surface handed to UpdateLayeredWindow.
    /// </summary>
    internal abstract class LayeredForm : Form
    {
        Bitmap _surface;

        protected LayeredForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "";
            // UpdateLayeredWindow owns every pixel of this window, so GDI painting
            // would only ever fight it and flicker.
            SetStyle(ControlStyles.Opaque, true);
        }

        /// <summary>Never steal focus from whatever the user is working in.</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // LAYERED is what makes UpdateLayeredWindow legal at all.
                // TRANSPARENT is what keeps the pointer working underneath: the
                // circle is drawn exactly where the user clicks, so without it
                // every click inside the circle would land on us instead.
                // NOACTIVATE keeps the overlay from pulling focus away.
                cp.ExStyle |= Native.WS_EX_LAYERED
                            | Native.WS_EX_TRANSPARENT
                            | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>The ARGB surface; internal so the probe can inspect its pixels.</summary>
        internal Bitmap Surface { get { return _surface; } }

        /// <summary>Screen rectangle of the last <see cref="Present"/>.</summary>
        public Rectangle LastRect { get; protected set; }

        /// <summary>
        /// Resizes the ARGB surface, returning true when it had to be
        /// reallocated. A fresh bitmap is already fully transparent, so the
        /// caller does not have to clear it first.
        /// </summary>
        protected bool EnsureSurface(int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            if (_surface != null && _surface.Width == w && _surface.Height == h) return false;
            if (_surface != null) _surface.Dispose();
            // Format32bppPArgb: GDI+ keeps the channels premultiplied, which is
            // exactly what AC_SRC_ALPHA expects. See Native.UpdateLayeredBitmap.
            _surface = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            return true;
        }

        protected bool Present(int x, int y)
        {
            if (_surface == null || !IsHandleCreated) return false;
            LastRect = new Rectangle(x, y, _surface.Width, _surface.Height);
            return Native.UpdateLayeredBitmap(Handle, _surface, x, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _surface != null)
            {
                _surface.Dispose();
                _surface = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The cursor circle itself: a small square window re-centred on the pointer
    /// every tick.
    /// </summary>
    internal sealed class SpotWindow : LayeredForm
    {
        int _half = -1;

        /// <summary>
        /// Draws the circle centred on screen point (<paramref name="cx"/>,
        /// <paramref name="cy"/>) and pushes it to the desktop.
        ///
        /// <paramref name="radius"/> is the radius of the circle you actually
        /// see; a ring straddles that path, so its outer edge sits at
        /// radius + width/2. <paramref name="ringWidth"/> of 0 draws no ring.
        /// </summary>
        public bool Place(int cx, int cy, int radius, int ringWidth,
                          Color fill, Color ring)
        {
            int half = (int)Math.Ceiling(radius + ringWidth / 2.0) + 1;
            if (half < 2) half = 2;

            if (half != _half)
            {
                _half = half;
                EnsureSurface(half * 2, half * 2);
            }

            Graphics g = null;
            try
            {
                g = Graphics.FromImage(Surface);
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                RectangleF box = new RectangleF(half - radius, half - radius,
                                                radius * 2f, radius * 2f);
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, box);
                }
                if (ringWidth > 0 && ring.A > 0)
                {
                    using (Pen p = new Pen(ring, ringWidth)) g.DrawEllipse(p, box);
                }
            }
            finally { if (g != null) g.Dispose(); }

            return Present(cx - half, cy - half);
        }

        /// <summary>Test seam: the surface's side length in pixels.</summary>
        internal int SideForTest { get { return _half * 2; } }
    }

    /// <summary>
    /// The click ripples. Unlike the spot this window is sized to the union of
    /// the rings it is currently drawing, so a click costs a few hundred
    /// kilobytes rather than a full-screen blit.
    /// </summary>
    internal sealed class RippleWindow : LayeredForm
    {
        /// <summary>
        /// Draws every ring in <paramref name="items"/>, clamped to
        /// <paramref name="limit"/> (the virtual screen).
        ///
        /// The window is sized from each ring's *final* radius rather than its
        /// current one, so the geometry stays put while the rings animate and the
        /// surface is allocated once per burst instead of once per frame.
        /// </summary>
        public bool Render(List<RippleDraw> items, Rectangle limit)
        {
            if (items == null || items.Count == 0) return false;

            int left = int.MaxValue, top = int.MaxValue;
            int right = int.MinValue, bottom = int.MinValue;
            foreach (RippleDraw d in items)
            {
                int half = (int)Math.Ceiling(d.MaxRadius + d.Width) + 1;
                left = Math.Min(left, (int)Math.Floor(d.X) - half);
                top = Math.Min(top, (int)Math.Floor(d.Y) - half);
                right = Math.Max(right, (int)Math.Ceiling(d.X) + half);
                bottom = Math.Max(bottom, (int)Math.Ceiling(d.Y) + half);
            }

            // Clamp to the virtual screen. A ripple near an edge is simply
            // clipped, which is what it should look like.
            if (left < limit.Left) left = limit.Left;
            if (top < limit.Top) top = limit.Top;
            if (right > limit.Right) right = limit.Right;
            if (bottom > limit.Bottom) bottom = limit.Bottom;

            int w = right - left;
            int h = bottom - top;
            if (w < 1 || h < 1) return false;

            EnsureSurface(w, h);

            Graphics g = null;
            try
            {
                g = Graphics.FromImage(Surface);
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                foreach (RippleDraw d in items)
                {
                    float r = d.Radius;
                    if (r <= 0.5f) continue;
                    RectangleF box = new RectangleF(d.X - left - r, d.Y - top - r,
                                                    r * 2f, r * 2f);
                    using (Pen p = new Pen(d.Color, d.Width)) g.DrawEllipse(p, box);
                }
            }
            finally { if (g != null) g.Dispose(); }

            return Present(left, top);
        }
    }
}
