using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Full-screen colour picker. Shows a frozen copy of the desktop with a
    /// magnifier loupe following the pointer; a click picks the pixel and closes.
    ///
    /// The backdrop is frozen rather than read live for two reasons: reading a
    /// live pixel per mouse-move through GetPixel would flicker as other windows
    /// redraw underneath, and a frozen frame guarantees the pixel the user sees
    /// in the loupe is exactly the pixel that gets returned.
    /// </summary>
    public sealed class ColorPickerForm : Form
    {
        readonly Settings _cfg;
        readonly Rectangle _desktop;
        readonly Bitmap _frozen;
        readonly bool _ownsFrozen;
        readonly int _zoom;
        readonly int _loupe;

        Point _cursor;          // client coords
        bool _haveCursor;
        Color _picked = Color.Empty;

        /// <summary>The picked colour, or <see cref="Color.Empty"/> if cancelled.</summary>
        public Color PickedColor { get { return _picked; } }

        /// <param name="desktop">Virtual-desktop bounds in screen coordinates.</param>
        /// <param name="frozen">Backdrop to sample; must cover <paramref name="desktop"/>.</param>
        /// <param name="ownsFrozen">
        /// True when the caller transferred ownership of <paramref name="frozen"/>
        /// to this form. Mirrors PinForm's contract: exactly one of the two
        /// parties disposes it, never both and never neither.
        /// </param>
        public ColorPickerForm(Settings cfg, Rectangle desktop, Bitmap frozen, bool ownsFrozen)
        {
            _cfg = cfg != null ? cfg : new Settings();
            _desktop = desktop;
            _frozen = frozen;
            _ownsFrozen = ownsFrozen;

            int z = _cfg.PickerZoom;
            _zoom = Math.Max(2, Math.Min(32, z));
            _loupe = Math.Max(60, Math.Min(400, _cfg.PickerLoupeSize));

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktop;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            // A crosshair cursor is the universal "pick a pixel" affordance and
            // tells the user immediately that this is a picker, not the annotator.
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // ForceForeground, not Activate: this form is shown from a global
            // hotkey while another app owns the foreground, and the window
            // manager refuses a plain activation in exactly that case.
            Native.ForceForeground(Handle);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cursor = e.Location;
            _haveCursor = true;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _picked = SampleAt(e.Location);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            // Right-click or middle-click cancels - the same "get me out of here"
            // gesture the region selector uses.
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        /// <summary>
        /// Maps a client point to a pixel in the frozen backdrop. The frozen
        /// bitmap's own origin is the virtual desktop's top-left, which can have
        /// negative coordinates on a multi-monitor setup, so the offset is
        /// applied rather than assumed to be zero.
        /// </summary>
        Color SampleAt(Point client)
        {
            Point screen = new Point(_desktop.Left + client.X, _desktop.Top + client.Y);

            // Read the frozen frame first: it is guaranteed to be the pixels the
            // user is looking at in the loupe. GetPixel on the live desktop is
            // only a fallback, for the case where the backdrop is missing.
            if (_frozen != null)
            {
                int fx = screen.X - _desktop.Left;
                int fy = screen.Y - _desktop.Top;
                if (fx >= 0 && fy >= 0 && fx < _frozen.Width && fy < _frozen.Height)
                {
                    try { return _frozen.GetPixel(fx, fy); }
                    catch { }
                }
            }
            return Native.PixelAt(screen);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_frozen != null)
            {
                // Drawn unscaled so the picker shows a true 1:1 desktop; scaling
                // here would make the loupe disagree with the actual pixel.
                try { g.DrawImageUnscaled(_frozen, 0, 0); }
                catch { }
            }
            else
            {
                g.Clear(Color.FromArgb(255, 24, 24, 28));
            }

            // A light scrim so the loupe and readout read as UI rather than as
            // part of the desktop. Kept very faint: this overlay must not hide
            // the colour the user is trying to judge.
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);

            if (!_haveCursor) return;

            Color c = SampleAt(_cursor);
            DrawLoupe(g, c);
            DrawReadout(g, c);
        }

        void DrawLoupe(Graphics g, Color c)
        {
            int size = _loupe;
            // Keep the loupe fully on screen: near an edge it flips to the other
            // side of the pointer instead of being clipped.
            int lx = _cursor.X + 24;
            int ly = _cursor.Y + 24;
            if (lx + size > ClientSize.Width) lx = _cursor.X - 24 - size;
            if (ly + size > ClientSize.Height) ly = _cursor.Y - 24 - size;
            lx = Math.Max(4, lx);
            ly = Math.Max(4, ly);

            Rectangle box = new Rectangle(lx, ly, size, size);

            // Sample a grid around the pointer and blow each source pixel up into
            // a _zoom-sized square. Nearest-neighbour is deliberate: interpolating
            // would blend neighbouring pixels and show a colour that is not the
            // one being picked.
            int half = (size / _zoom) / 2;
            if (half < 1) half = 1;

            for (int gy = -half; gy <= half; gy++)
            {
                for (int gx = -half; gx <= half; gx++)
                {
                    Color sc = SampleAt(new Point(_cursor.X + gx, _cursor.Y + gy));
                    int px = box.X + (gx + half) * _zoom;
                    int py = box.Y + (gy + half) * _zoom;
                    using (SolidBrush b = new SolidBrush(sc))
                        g.FillRectangle(b, px, py, _zoom, _zoom);
                }
            }

            // Centre crosshair marking the exact pixel that will be returned.
            int cx = box.X + half * _zoom;
            int cy = box.Y + half * _zoom;
            using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 1.6f))
            {
                g.DrawRectangle(p, cx - 1, cy - 1, _zoom + 2, _zoom + 2);
            }
            using (Pen p = new Pen(Color.FromArgb(200, 0, 0, 0), 1f))
            {
                g.DrawRectangle(p, cx - 2, cy - 2, _zoom + 4, _zoom + 4);
            }

            using (Pen p = new Pen(Color.FromArgb(160, 255, 255, 255), 1f))
                g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);
        }

        void DrawReadout(Graphics g, Color c)
        {
            string hex = Beautifier.FormatColor(c);
            string rgb = c.R + ", " + c.G + ", " + c.B;

            using (Font f = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point))
            {
                Size hs = TextRenderer.MeasureText(hex, f);
                Size rs = TextRenderer.MeasureText(rgb, f);
                int w = Math.Max(hs.Width, rs.Width) + 44;
                int h = hs.Height + rs.Height + 18;

                // Place the readout opposite the loupe so the two never overlap.
                int x = _cursor.X + 24;
                if (x + w > ClientSize.Width) x = _cursor.X - 24 - w;
                int y = _cursor.Y - 24 - h;
                if (y < 4) y = _cursor.Y + 24;
                x = Math.Max(4, x);
                y = Math.Max(4, y);

                Rectangle box = new Rectangle(x, y, w, h);
                using (GraphicsPath path = OverlayForm.RoundRect(box, 8))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 16, 18, 22)))
                    g.FillPath(b, path);
                using (GraphicsPath path = OverlayForm.RoundRect(box, 8))
                using (Pen p = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    g.DrawPath(p, path);

                // Swatch of the live colour, with a ring so white-on-white is
                // still visible.
                Rectangle sw = new Rectangle(box.X + 12, box.Y + 10, 22, 22);
                using (SolidBrush b = new SolidBrush(c))
                    g.FillRectangle(b, sw);
                using (Pen p = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
                    g.DrawRectangle(p, sw);

                TextRenderer.DrawText(g, hex, f,
                    new Point(box.X + 42, box.Y + 8),
                    Color.FromArgb(255, 240, 242, 245));
                TextRenderer.DrawText(g, rgb, f,
                    new Point(box.X + 42, box.Y + 8 + hs.Height),
                    Color.FromArgb(255, 168, 176, 190));
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Only dispose the backdrop when ownership was transferred to us.
            // The caller passes ownsFrozen=false when it still needs the bitmap
            // afterwards, and disposing it there would crash the caller.
            if (disposing && _ownsFrozen && _frozen != null)
            {
                try { _frozen.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
