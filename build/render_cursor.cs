// Renders the 浮标 (cursor spot) and the click ripple to PNGs.
//
// Geometry and pixel assertions live in build/cursor_probe.cs; this exists for
// the thing assertions cannot judge: whether the circle is actually legible
// over real content, and whether the semi-transparency reads as intended.
//
// The overlay paints itself into a 32bpp ARGB surface and hands it to
// UpdateLayeredWindow, so a screen grab of the desktop would show whatever is
// behind it at that moment. Instead this composites the very surface the
// shipped code produced over a synthetic "document" - same pixels, controlled
// backdrop, no dependence on what the machine happens to be displaying.
//
// Usage: build\run_render.ps1 render_cursor
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ScreenPen;

static class RenderCursor
{
    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string outDir = Path.Combine("build", "shots", "render");
        Directory.CreateDirectory(outDir);

        RenderSpots(Path.Combine(outDir, "50_cursor_spot.png"));
        RenderRipples(Path.Combine(outDir, "51_cursor_ripple.png"));

        Console.WriteLine("wrote 50_cursor_spot.png / 51_cursor_ripple.png");
        return 0;
    }

    // -----------------------------------------------------------------
    //  A stand-in for "the app you are demonstrating"
    // -----------------------------------------------------------------
    static Bitmap Backdrop(int w, int h)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (LinearGradientBrush bg = new LinearGradientBrush(
                       new Rectangle(0, 0, w, h), Color.FromArgb(255, 250, 250, 252),
                       Color.FromArgb(255, 232, 236, 245), LinearGradientMode.Vertical))
                g.FillRectangle(bg, 0, 0, w, h);

            // Some text-like bars so the see-through effect is obvious.
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(255, 60, 66, 80)))
            using (SolidBrush faint = new SolidBrush(Color.FromArgb(255, 150, 158, 172)))
            {
                for (int i = 0; i < 9; i++)
                {
                    int y = 26 + i * 34;
                    g.FillRectangle(ink, 26, y, 130, 12);
                    g.FillRectangle(faint, 26, y + 18, 320 - (i % 3) * 60, 8);
                }
            }

            using (SolidBrush box = new SolidBrush(Color.FromArgb(255, 70, 130, 220)))
                g.FillRectangle(box, 380, 40, 120, 46);
            using (SolidBrush box = new SolidBrush(Color.FromArgb(255, 232, 160, 0)))
                g.FillRectangle(box, 380, 100, 120, 46);
        }
        return b;
    }

    static void Caption(Graphics g, string text, int x, int y)
    {
        using (Font f = new Font("Microsoft YaHei UI", 9f))
        {
            // A solid strip behind the caption: the backdrop is deliberately
            // busy (that is the point of it), so bare text over it is unreadable.
            Size need = TextRenderer.MeasureText(text, f);
            using (SolidBrush band = new SolidBrush(Color.FromArgb(255, 250, 250, 252)))
                g.FillRectangle(band, x - 6, y - 4, need.Width + 12, need.Height + 8);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 70, 76, 92)))
                g.DrawString(text, f, b, x, y);
        }
    }

    /// <summary>Clears a caption band at the bottom of the sheet.</summary>
    static void CaptionBand(Graphics g, int w, int h)
    {
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 246, 247, 250)))
            g.FillRectangle(b, 0, h - 46, w, 46);
    }

    // -----------------------------------------------------------------
    //  The spot at four representative settings
    // -----------------------------------------------------------------
    static void RenderSpots(string path)
    {
        const int CellW = 320, CellH = 230, Pad = 16;
        int cols = 4;
        int w = CellW * cols, h = CellH + 46;

        using (Bitmap sheet = Backdrop(w, h))
        using (Graphics g = Graphics.FromImage(sheet))
        using (CursorSpotOverlay ov = new CursorSpotOverlay())
        {
            CaptionBand(g, w, h);
            string[] titles = new string[]
            {
                "默认：半径 40 · 填充 80 · 描边 2",
                "空心圈：填充不透明度 0 · 描边 4",
                "小实心：半径 18 · 填充 200 · 无描边",
                "大而柔和：半径 100 · 平滑 60"
            };

            Settings[] variants = new Settings[4];
            for (int i = 0; i < 4; i++)
            {
                Settings s = new Settings();
                s.CursorSpotEnabled = true;
                s.CursorRippleEnabled = false;
                variants[i] = s;
            }
            variants[1].CursorSpotFillAlpha = 0;
            variants[1].CursorSpotRingWidth = 4;
            variants[1].CursorSpotRingColor = "#E8503A";
            variants[2].CursorSpotRadius = 18;
            variants[2].CursorSpotFillAlpha = 200;
            variants[2].CursorSpotRingWidth = 0;
            variants[3].CursorSpotRadius = 100;
            variants[3].CursorSpotSmoothing = 60;
            variants[3].CursorSpotRingWidth = 3;
            variants[3].CursorSpotFillAlpha = 40;

            for (int i = 0; i < 4; i++)
            {
                variants[i].Clamp();
                ov.ApplySettings(variants[i]);

                // Draw at a point that sits over both text and the coloured
                // blocks, so the transparency has something to show through.
                int cx = i * CellW + 168;
                int cy = 118;
                ov.RenderAtForTest(cx, cy);

                Bitmap surf = ov.SpotSurfaceForTest;
                Rectangle r = ov.SpotWindowRectForTest;
                if (surf != null && r.Width > 0)
                {
                    g.DrawImage(surf, r.Left, r.Top, surf.Width, surf.Height);
                }
                Caption(g, titles[i], i * CellW + Pad, h - 30);
            }

            sheet.Save(path, ImageFormat.Png);
        }
    }

    // -----------------------------------------------------------------
    //  A ripple at three points in its 600 ms life
    // -----------------------------------------------------------------
    static void RenderRipples(string path)
    {
        const int CellW = 320, CellH = 230, Pad = 16;
        int w = CellW * 3, h = CellH + 46;

        using (Bitmap sheet = Backdrop(w, h))
        using (Graphics g = Graphics.FromImage(sheet))
        using (CursorSpotOverlay ov = new CursorSpotOverlay())
        {
            CaptionBand(g, w, h);
            Settings s = new Settings();
            s.CursorSpotEnabled = false;
            s.CursorRippleEnabled = true;
            s.CursorRippleSize = 150;
            s.Clamp();
            ov.ApplySettings(s);

            string[] titles = new string[] { "刚点下（0 ms）", "扩散中（300 ms）", "即将消失（540 ms）" };
            int[] ages = new int[] { 60, 300, 540 };

            for (int i = 0; i < 3; i++)
            {
                // Clear the list between cells, or each frame would also show
                // the ripples from the previous ones and the sheet would claim
                // to show "one ripple at three ages" while showing six.
                ov.Suspend();
                ov.Resume();

                ov.AddRippleForTest(i * CellW + 160, 118, "#1E90FF");
                ov.AgeRipplesForTest(ages[i]);
                ov.RenderAtForTest(i * CellW + 160, 118);

                Bitmap surf = ov.RippleSurfaceForTest;
                Rectangle r = ov.RippleWindowRectForTest;
                if (surf != null && r.Width > 0)
                    g.DrawImage(surf, r.Left, r.Top, surf.Width, surf.Height);

                Caption(g, titles[i], i * CellW + Pad, h - 30);
            }

            sheet.Save(path, ImageFormat.Png);
        }
    }
}
