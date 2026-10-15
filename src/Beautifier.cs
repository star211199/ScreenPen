using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenPen
{
    /// <summary>
    /// Turns a bare screenshot into a presentation-ready "card": padded inside a
    /// diagonal gradient, with rounded corners and an optional soft drop shadow.
    ///
    /// Why a whole class rather than a few lines in the save path: this is the
    /// single most-requested cosmetic feature in every screenshot tool that has
    /// it (CleanShot X, Shottr, Xnapper, Snagit's "add effects"), and it is the
    /// one feature whose correctness is entirely visual. Keeping it separate
    /// means a probe can assert the geometry and the exact pixel colours without
    /// going through the UI.
    ///
    /// Everything here is deterministic and pure: given the same input bitmap and
    /// settings it always produces byte-identical output, which is what makes the
    /// probe in build\tier1_probe.cs meaningful.
    /// </summary>
    public static class Beautifier
    {
        /// <summary>
        /// Extra room reserved outside the card for the shadow to bleed into.
        /// A shadow drawn right up to the canvas edge would be clipped into a
        /// hard line, which looks worse than no shadow at all.
        /// </summary>
        const int ShadowRoom = 60;

        /// <summary>
        /// Renders the beautified version of <paramref name="src"/>.
        /// Returns a new bitmap; the caller owns it. Returns null if the input is
        /// unusable, so callers can fall back to saving the raw image rather than
        /// writing a corrupt file.
        /// </summary>
        public static Bitmap Render(Bitmap src, Settings cfg)
        {
            if (src == null || src.Width <= 0 || src.Height <= 0) return null;
            if (cfg == null) return null;

            int pad = Math.Max(0, cfg.BeautifyPadding);
            int radius = Math.Max(0, cfg.BeautifyRadius);
            // A radius larger than half the shorter side makes GraphicsPath's arcs
            // overlap and produces a bowtie shape, so clamp it to a true radius.
            int maxRadius = Math.Min(src.Width, src.Height) / 2;
            if (radius > maxRadius) radius = maxRadius;

            bool shadow = cfg.BeautifyShadow;
            int room = shadow ? ShadowRoom : 0;

            int outW = src.Width + pad * 2 + room * 2;
            int outH = src.Height + pad * 2 + room * 2;
            // Guard against a pathological settings file asking for a gigapixel
            // canvas; GDI+ throws a generic OutOfMemoryException that would look
            // like a crash rather than a settings problem.
            if ((long)outW * outH > 80L * 1024 * 1024) return null;

            Color c1, c2;
            if (!TryParseColor(cfg.BeautifyColor1, out c1)) c1 = Color.FromArgb(255, 0x6D, 0x5B, 0xFF);
            if (!TryParseColor(cfg.BeautifyColor2, out c2)) c2 = Color.FromArgb(255, 0x22, 0xB8, 0xCF);

            Bitmap outBmp = null;
            try
            {
                outBmp = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(outBmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    Rectangle card = new Rectangle(pad + room, pad + room,
                                                   src.Width, src.Height);

                    // 1. Background. A diagonal gradient reads as "designed"
                    //    where a flat fill reads as "unfinished", and it costs
                    //    one extra constructor argument.
                    using (LinearGradientBrush bg = new LinearGradientBrush(
                               new Rectangle(0, 0, outW, outH), c1, c2, 45f))
                    {
                        g.FillRectangle(bg, 0, 0, outW, outH);
                    }

                    // 2. Shadow, under the card.
                    if (shadow)
                        DrawShadow(g, card, radius);

                    // 3. The screenshot itself, clipped to the rounded rect so
                    //    the corners are genuinely transparent rather than
                    //    painted over with a guess at the background colour.
                    GraphicsPath clip = null;
                    try
                    {
                        if (radius > 0)
                        {
                            clip = OverlayForm.RoundRect(card, radius);
                            g.SetClip(clip);
                        }
                        g.DrawImage(src, card);
                    }
                    finally
                    {
                        g.ResetClip();
                        if (clip != null) clip.Dispose();
                    }

                    // 4. A hairline inside the card edge. Without it a light
                    //    screenshot on a light background dissolves into the
                    //    gradient; a 1px translucent white line keeps the card
                    //    readable on any background without looking like a border.
                    if (radius > 0)
                    {
                        using (GraphicsPath edge = OverlayForm.RoundRect(card, radius))
                        using (Pen p = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
                            g.DrawPath(p, edge);
                    }
                }
                return outBmp;
            }
            catch
            {
                if (outBmp != null) outBmp.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Paints a soft drop shadow beneath a rounded rectangle.
        ///
        /// GDI+ has no blur primitive, so the standard trick is to stack a few
        /// progressively larger, progressively fainter rounded rects. Six layers
        /// is the point where the banding stops being visible at 100% zoom; more
        /// layers cost time without changing the result. Each layer is offset
        /// down slightly, because a shadow centred on the card looks like a glow
        /// rather than a light source above.
        /// </summary>
        static void DrawShadow(Graphics g, Rectangle card, int radius)
        {
            const int Layers = 6;
            const int Spread = 26;     // total blur radius
            const int OffsetY = 10;    // light comes from above

            for (int i = Layers; i >= 1; i--)
            {
                // Outermost layer first, so inner (darker) layers paint on top.
                float t = i / (float)Layers;             // 1.0 .. 0.167
                int grow = (int)Math.Round(Spread * t);
                // Alpha falls off quadratically: a linear ramp bands visibly
                // because the eye is far more sensitive to the dark end.
                int alpha = (int)Math.Round(70 * (1f - t) * (1f - t));
                if (alpha <= 0) continue;

                Rectangle r = new Rectangle(card.X - grow, card.Y - grow + OffsetY,
                                            card.Width + grow * 2,
                                            card.Height + grow * 2);
                if (r.Width <= 0 || r.Height <= 0) continue;
                int rad = radius + grow;
                int maxR = Math.Min(r.Width, r.Height) / 2;
                if (rad > maxR) rad = maxR;

                GraphicsPath path = null;
                try
                {
                    path = rad > 0 ? OverlayForm.RoundRect(r, rad)
                                   : RectPath(r);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                        g.FillPath(b, path);
                }
                finally { if (path != null) path.Dispose(); }
            }
        }

        static GraphicsPath RectPath(Rectangle r)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddRectangle(r);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Parses "#RRGGBB" or "#AARRGGBB". Deliberately does not use
        /// ColorTranslator.FromHtml, which silently returns a colour for garbage
        /// input on some inputs and throws on others.
        /// </summary>
        public static bool TryParseColor(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.StartsWith("#")) t = t.Substring(1);
            if (t.Length != 6 && t.Length != 8) return false;

            int v;
            if (!int.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out v))
                return false;

            if (t.Length == 6) c = Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            else c = Color.FromArgb((v >> 24) & 0xFF, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }

        /// <summary>Formats a colour as "#RRGGBB", for round-tripping settings.</summary>
        public static string FormatColor(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
    }
}
