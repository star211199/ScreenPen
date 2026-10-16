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
    /// Records a screen region into an animated GIF.
    ///
    /// <para>
    /// Frames are sampled on a <see cref="Timer"/> at the configured rate and
    /// handed to <see cref="GifBuilder"/>, which stores them as PNG blobs and
    /// merges runs of identical frames into a single longer delay. The finished
    /// file goes through <see cref="Gif.Encode"/>, which patches the per-frame
    /// delays and the loop block that <c>GifBitmapEncoder</c> drops.
    /// </para>
    ///
    /// <para>
    /// The recording UI (border, stop bar, countdown) is excluded from the
    /// capture with <c>SetWindowDisplayAffinity</c>, so it never appears in the
    /// file. When that call is unavailable the border is not drawn at all rather
    /// than being recorded.
    /// </para>
    /// </summary>
    public sealed class GifRecorder : IDisposable
    {
        // ---------------------------------------------------------------
        //  Public surface
        // ---------------------------------------------------------------

        /// <summary>Raised on the UI thread when recording finishes or fails.</summary>
        public event EventHandler Finished;

        /// <summary>Raised once per stored frame, for the on-screen counter.</summary>
        public event EventHandler Tick;

        public bool IsRecording { get { return _timer != null && _timer.Enabled; } }
        public bool IsCountingDown { get { return _countdown != null && _countdown.Enabled; } }
        /// <summary>
        /// Frames stored so far. Remains readable after the recording stops, so a
        /// caller can report what was written.
        /// </summary>
        public int FrameCount { get { return _builder == null ? _storedFrames : _builder.FrameCount; } }
        public int CollapsedFrames { get { return _builder == null ? _collapsedFrames : _builder.CollapsedFrames; } }
        /// <summary>How many frame grabs failed. Non-zero means the host is busy.</summary>
        public int GrabErrors { get { return _grabErrors; } }
        /// <summary>The first grab failure, for diagnostics.</summary>
        public string FirstGrabError { get { return _firstGrabError; } }
        /// <summary>Seconds recorded so far, counting collapsed frames.</summary>
        public int ElapsedSeconds { get { return (int)(_elapsedMs / 1000L); } }
        /// <summary>Set when the recording stopped because of an error or a cap.</summary>
        public string StopReason { get { return _stopReason; } }
        /// <summary>The file written by the last successful recording, or null.</summary>
        public string LastFile { get { return _lastFile; } }
        /// <summary>The region being recorded, in screen coordinates.</summary>
        public Rectangle Region { get { return _region; } }

        public GifRecorder(Settings cfg)
        {
            _cfg = cfg == null ? new Settings() : cfg;
        }

        /// <summary>
        /// Starts recording <paramref name="region"/>. The countdown configured by
        /// <see cref="Settings.GifCountdownSeconds"/> runs first; a value of 0
        /// starts immediately.
        /// </summary>
        public void Start(Rectangle region, IWin32Window owner)
        {
            if (IsRecording || IsCountingDown) return;
            if (region.Width < 2 || region.Height < 2)
            {
                _stopReason = "录制区域太小";
                RaiseFinished();
                return;
            }

            _region = region;
            _stopReason = null;
            _lastFile = null;
            _elapsedMs = 0;
            _builder = new GifBuilder(_cfg.GifFps, _cfg.GifSkipIdenticalFrames);
            _owner = owner;

            ShowChrome();

            int cd = _cfg.GifCountdownSeconds;
            if (cd > 0)
            {
                _countdownLeft = cd;
                UpdateChrome();
                _countdown = new System.Windows.Forms.Timer();
                _countdown.Interval = 1000;
                _countdown.Tick += OnCountdownTick;
                _countdown.Start();
            }
            else
            {
                BeginSampling();
            }
        }

        /// <summary>Stops recording and writes the GIF. Returns the path, or null.</summary>
        public string Stop(string reason)
        {
            StopTimers();
            HideChrome();

            if (_stopReason == null && reason != null) _stopReason = reason;

            GifBuilder b = _builder;
            _builder = null;
            if (b != null)
            {
                // Keep the counts readable after the builder is released.
                _storedFrames = b.FrameCount;
                _collapsedFrames = b.CollapsedFrames;
            }
            if (b == null || b.FrameCount == 0)
            {
                if (_stopReason == null) _stopReason = "没有录制到任何画面";
                RaiseFinished();
                return null;
            }

            string path = NextPath();
            string err = b.Save(path, _cfg.GifLoop);
            if (err != null)
            {
                _stopReason = err;
                RaiseFinished();
                return null;
            }

            _lastFile = path;
            RaiseFinished();
            return path;
        }

        /// <summary>Stops and discards the recording without writing a file.</summary>
        public void Cancel()
        {
            StopTimers();
            HideChrome();
            if (_builder != null)
            {
                _storedFrames = _builder.FrameCount;
                _collapsedFrames = _builder.CollapsedFrames;
            }
            _builder = null;
            _stopReason = "已取消";
            RaiseFinished();
        }

        public void Dispose()
        {
            StopTimers();
            HideChrome();
            _builder = null;
            _chrome = null;
        }

        // ---------------------------------------------------------------
        //  Sampling
        // ---------------------------------------------------------------

        Settings _cfg;
        Rectangle _region;
        GifBuilder _builder;
        Timer _timer;
        Timer _countdown;
        int _countdownLeft;
        long _elapsedMs;
        long _lastTickMs;
        string _stopReason;
        string _lastFile;
        IWin32Window _owner;
        RecorderChrome _chrome;
        bool _chromeExcluded;
        int _grabErrors;
        string _firstGrabError;
        int _storedFrames;
        int _collapsedFrames;

        void BeginSampling()
        {
            _lastTickMs = Environment.TickCount;
            int interval = Math.Max(20, 1000 / Math.Max(1, _cfg.GifFps));
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = interval;
            _timer.Tick += OnSample;
            _timer.Start();
        }

        void StopTimers()
        {
            if (_timer != null) { _timer.Stop(); _timer.Tick -= OnSample; _timer.Dispose(); _timer = null; }
            if (_countdown != null) { _countdown.Stop(); _countdown.Tick -= OnCountdownTick; _countdown.Dispose(); _countdown = null; }
        }

        void OnCountdownTick(object sender, EventArgs e)
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                UpdateChrome();
                return;
            }
            if (_countdown != null) { _countdown.Stop(); _countdown.Tick -= OnCountdownTick; _countdown.Dispose(); _countdown = null; }
            BeginSampling();
        }

        void OnSample(object sender, EventArgs e)
        {
            GifBuilder b = _builder;
            if (b == null) return;

            long now = Environment.TickCount;
            int elapsed = (int)(now - _lastTickMs);
            _lastTickMs = now;
            if (elapsed < 0) elapsed = 0;   // TickCount wraps every ~49 days
            _elapsedMs += elapsed;

            // The cap ends the recording cleanly rather than truncating it.
            if (_cfg.GifMaxSeconds > 0 && _elapsedMs >= _cfg.GifMaxSeconds * 1000L)
            {
                Stop("已达到最长录制时间");
                return;
            }

            Bitmap frame = null;
            try
            {
                // The chrome hides itself for the duration of the grab. When it
                // cannot be excluded from capture we do not draw it at all, so
                // there is nothing to hide.
                bool hid = _chromeExcluded && _chrome != null;
                if (hid) _chrome.Hide();
                try
                {
                    frame = Native.CaptureRegion(_region);
                }
                finally
                {
                    if (hid && _chrome != null) _chrome.Show();
                }

                if (frame == null) return;

                if (b.Add(frame)) RaiseTick();

                if (b.IsFull) Stop("已达到最长录制时间");
                else if (_cfg.GifShowBorder) UpdateChrome();
            }
            catch (Exception ex)
            {
                // A single failed grab must not kill a recording in progress, but
                // it must be visible afterwards: a silently swallowed exception
                // here looks exactly like "the screen never changed".
                _grabErrors++;
                if (_firstGrabError == null)
                    _firstGrabError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (frame != null) frame.Dispose();
            }
        }

        void RaiseTick()
        {
            EventHandler h = Tick;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        void RaiseFinished()
        {
            EventHandler h = Finished;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        // ---------------------------------------------------------------
        //  Output path
        // ---------------------------------------------------------------

        string NextPath()
        {
            string dir = _cfg.ScreenshotFolder;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                string def = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenPen");
                try { Directory.CreateDirectory(def); dir = def; }
                catch { dir = Path.GetTempPath(); }
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, "ScreenPen_" + stamp + ".gif");
            int n = 1;
            while (File.Exists(path))
                path = Path.Combine(dir, "ScreenPen_" + stamp + "_" + (n++) + ".gif");
            return path;
        }

        // ---------------------------------------------------------------
        //  Recording chrome
        // ---------------------------------------------------------------

        void ShowChrome()
        {
            if (!_cfg.GifShowBorder) return;
            try
            {
                _chrome = new RecorderChrome(_region);
                _chrome.Show();
                // If the OS will not exclude us from capture, the border has to
                // stay hidden or it would be baked into every frame.
                _chromeExcluded = Native.SetExcludedFromCapture(_chrome.Handle, true);
                if (!_chromeExcluded) _chrome.Hide();
            }
            catch
            {
                _chrome = null;
                _chromeExcluded = false;
            }
        }

        void UpdateChrome()
        {
            if (_chrome == null) return;
            try
            {
                string text = IsCountingDown
                    ? "● " + _countdownLeft
                    : "● " + ElapsedSeconds + "s   " + FrameCount + " 帧   Esc 停止";
                _chrome.SetStatus(text);
            }
            catch { }
        }

        void HideChrome()
        {
            RecorderChrome c = _chrome;
            _chrome = null;
            if (c == null) return;
            try
            {
                if (_chromeExcluded) Native.SetExcludedFromCapture(c.Handle, false);
                c.Close();
                c.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// The thin border and status pill drawn around a region being recorded.
    /// A plain layered window so it can be excluded from capture.
    /// </summary>
    sealed class RecorderChrome : Form
    {
        string _status = "";

        public RecorderChrome(Rectangle region)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            // A little room for the status pill above the top edge.
            int pad = 34;
            Bounds = new Rectangle(region.X - 2, region.Y - pad, region.Width + 4, region.Height + pad + 2);
            _region = region;
        }

        readonly Rectangle _region;

        public void SetStatus(string text)
        {
            _status = text == null ? "" : text;
            Invalidate();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Red recording border, drawn just outside the captured region so it
            // does not sit on top of the content in the file.
            Rectangle border = new Rectangle(
                2, Bounds.Height - _region.Height - 2, _region.Width, _region.Height);
            using (Pen p = new Pen(Color.FromArgb(235, 64, 52), 3f))
                g.DrawRectangle(p, border);

            if (_status.Length == 0) return;

            using (Font f = new Font("Microsoft YaHei UI", 9f))
            {
                SizeF sz = g.MeasureString(_status, f);
                RectangleF pill = new RectangleF(
                    4, 4, sz.Width + 18, sz.Height + 8);
                using (GraphicsPath path = OverlayForm.RoundRect(pill, pill.Height / 2f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(224, 24, 26, 30)))
                {
                    g.FillPath(bg, path);
                    using (Pen edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                        g.DrawPath(edge, path);
                }
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 240, 242, 245)))
                    g.DrawString(_status, f, fg, pill.X + 9, pill.Y + 4);
            }
        }
    }
}
