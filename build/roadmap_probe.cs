// Feasibility probe for the top two roadmap candidates, so the roadmap rests on
// measured behaviour instead of optimism.
//
//  A) SCROLLING (STITCHED) CAPTURE — ScreenPen already has row/column profile
//     correlation in OverlayForm for scroll-follow. This probe reimplements that
//     same correlation standalone and drives it with synthetic scrolling frames
//     to prove the primitive can recover exact pixel offsets, then stitches the
//     frames into one tall image and checks for duplicated/missing rows.
//
//  B) BEAUTIFIER (padding + rounded corners + drop shadow + background) — pure
//     GDI+, no dependency. Checked here mainly for cost/quality: rounded-corner
//     clipping without jagged edges, and shadow blending.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

static class RoadmapProbe
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    // ================= A) scrolling capture ================================
    // Mirrors OverlayForm's algorithm: downscale to grayscale, build row/column
    // mean profiles, find the integer shift with the best mean-abs-difference.
    const int Div = 6;
    const int Search = 22;
    const double MinScore = 18.0;

    static void Profiles(Bitmap bmp, out int[] cols, out int[] rows)
    {
        ProfilesAt(bmp, Div, out cols, out rows);
    }

    static void ProfilesAt(Bitmap bmp, int div, out int[] cols, out int[] rows)
    {
        int w = Math.Max(1, bmp.Width / div), h = Math.Max(1, bmp.Height / div);
        int[] acc = new int[w * h];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                     ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bd.Stride;
            byte[] buf = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            // BLOCK AVERAGE, not point sample. The shipped Native.CaptureGray
            // downscales with StretchBlt in HALFTONE mode, which averages each
            // Div x Div block. Point sampling aliases against fine row pitch and
            // destroys the profile - that is exactly what an earlier revision of
            // this probe got wrong.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, n = 0;
                    for (int dy = 0; dy < div; dy++)
                    {
                        int sy = y * div + dy;
                        if (sy >= bmp.Height) break;
                        int ro = sy * stride;
                        for (int dx = 0; dx < div; dx++)
                        {
                            int sx = x * div + dx;
                            if (sx >= bmp.Width) break;
                            int o = ro + sx * 4;
                            int b = buf[o], g = buf[o + 1], r = buf[o + 2];
                            sum += (r * 77 + g * 150 + b * 29) >> 8;
                            n++;
                        }
                    }
                    acc[y * w + x] = n > 0 ? sum / n : 0;
                }
        }
        finally { bmp.UnlockBits(bd); }

        cols = new int[w];
        rows = new int[h];
        for (int x = 0; x < w; x++)
        {
            long s = 0;
            for (int y = 0; y < h; y++) s += acc[y * w + x];
            cols[x] = (int)(s / h);
        }
        for (int y = 0; y < h; y++)
        {
            long s = 0;
            for (int x = 0; x < w; x++) s += acc[y * w + x];
            rows[y] = (int)(s / w);
        }
    }

    static double Score(int[] prev, int[] cur, int shift, int n)
    {
        int start = Math.Max(0, -shift), end = Math.Min(n, n - shift);
        if (end - start < n / 3) return double.MaxValue;
        long sum = 0;
        for (int i = start; i < end; i++) sum += Math.Abs(prev[i] - cur[i + shift]);
        return (double)sum / (end - start);
    }

    static int BestShift(int[] prev, int[] cur, int maxShift)
    {
        int n = Math.Min(prev.Length, cur.Length);
        if (n < maxShift * 3) return 0;
        double zero = Score(prev, cur, 0, n), best = zero;
        int bestShift = 0;
        for (int s = -maxShift; s <= maxShift; s++)
        {
            if (s == 0) continue;
            double v = Score(prev, cur, s, n);
            if (v < best - 0.001) { best = v; bestShift = s; }
        }
        if (Diag) Console.WriteLine("        [diag] n=" + n + " zero=" + zero.ToString("0.0") + " best=" + best.ToString("0.0") + " shift=" + bestShift + " improve=" + (zero - best).ToString("0.0") + " (need " + MinScore + ")");
        if (bestShift == 0 || zero - best < MinScore) return 0;
        return bestShift;
    }

    static bool Diag = false;

    // A tall "document" that gets scrolled through a fixed viewport.
    //
    // Content must be DENSE and HIGH-CONTRAST. Three earlier attempts failed for
    // instructive reasons, all of which are real constraints on a scrolling
    // capture feature:
    //   1. mostly-blank rows give a flat profile with no signal at all;
    //   2. perfectly periodic bands alias against the Div=6 downsampling;
    //   3. low-contrast rows keep the mean-abs-difference above FollowMinScore,
    //      so the detector correctly refuses to report a shift.
    // Real pages have dark text on light background, i.e. strong row-to-row
    // contrast, so we render text-like dense dark rows with irregular extents.
    static Bitmap RenderDoc(int totalH, int viewH, int scrollY, int width)
    {
        Bitmap bmp = new Bitmap(width, viewH);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int Row = 12;
            // Iterate over ABSOLUTE document rows aligned to the Row grid, then
            // place each at its viewport position. Keying content on the absolute
            // row (not on the loop counter) is what makes the document stationary
            // in document space: an earlier revision stepped from -scrollY, so a
            // scrollY that was not a multiple of Row shifted the content phase and
            // the detector legitimately could not match consecutive frames.
            int first = (scrollY / Row) * Row;
            for (int r = first; r <= scrollY + viewH + Row; r += Row)
            {
                if (r < 0 || r > totalH) continue;
                int y = r - scrollY;
                int k = r / Row;
                uint h = (uint)(k * 2654435761u);
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                int x = 4;
                uint bits = h;
                while (x < width - 20)
                {
                    bits = bits * 1103515245u + 12345u;
                    int run = 10 + (int)((bits >> 16) % 70);
                    int gap = 4 + (int)((bits >> 8) % 22);
                    if ((bits & 1) == 0)
                        g.FillRectangle(Brushes.Black, x, y + 1, Math.Min(run, width - 20 - x), Row - 3);
                    x += run + gap;
                }
                if (k % 5 == 0)
                    g.FillRectangle(Brushes.Black, 0, y + Row - 3, width, 2);
            }
        }
        return bmp;
    }

    static void TestScrolling()
    {
        Console.WriteLine("-- A) scrolling / stitched capture --");
        const int W = 600, ViewH = 400, TotalH = 4000;

        // The shipped scroll-follow detector uses FollowDiv=6 (downsample factor)
        // and FollowSearch=22 (samples searched), giving a +-132 px detection
        // window and a hard 6 px quantization on every measured offset.
        //
        // For scroll-FOLLOW that is fine: it only needs to move the viewport, and
        // being 5 px off is invisible. For scroll-STITCHING the same error is
        // cumulative - N frames of 5 px drift visibly tears the output. So sweep
        // Div to find the accuracy/cost tradeoff the roadmap should specify.
        Console.WriteLine("     shipped follow params: FollowDiv=" + Div + " FollowSearch=" + Search
                          + " FollowMinScore=" + MinScore + "  => +-" + (Search * Div) + " px window, " + Div + " px quantum");
        Console.WriteLine();

        int[] steps = new int[] { 0, 119, 238, 357, 476, 595 };
        int[] divs = new int[] { 6, 4, 2, 1 };

        // KEY FINDING, and the reason this sweep exists: FollowSearch is counted
        // in PROFILE SAMPLES, so the detection window in pixels is
        // FollowSearch * FollowDiv. At Div=6 that is +-132 px; at Div=1 it
        // collapses to +-22 px. Window and precision are therefore COUPLED in the
        // shipped constants, and a stitching feature cannot use them as-is.
        // Fix: express the window in PIXELS and derive the sample search range.
        //
        // SECOND FINDING: shipped BestShift() bails out when
        // n < maxShift * 3, i.e. the search window can never exceed ONE THIRD of
        // the profile length. For a viewport H pixels tall the maximum detectable
        // per-frame scroll is therefore H/3 regardless of Div. Keep the window
        // under that bound or every frame reports "no scroll".
        const int WindowPx = 120;

        int bestDiv = -1;
        string bestNote = null;

        foreach (int d in divs)
        {
            int maxShift = Math.Max(1, WindowPx / d);
            int[] colPrev = null, rowPrev = null;
            int[] measured = new int[steps.Length - 1];
            int idx = 0, worstErr = 0, sumErr = 0, drift = 0, locked = 0;

            for (int i = 0; i < steps.Length; i++)
            {
                using (Bitmap bmp = RenderDoc(TotalH, ViewH, steps[i], W))
                {
                    int[] c, r;
                    ProfilesAt(bmp, d, out c, out r);
                    if (i > 0)
                    {
                        int dy = BestShift(rowPrev, r, maxShift);
                        int pix = -dy * d;
                        measured[idx++] = pix;
                        if (pix != 0) locked++;
                        int truth = steps[i] - steps[i - 1];
                        int err = pix - truth;
                        worstErr = Math.Max(worstErr, Math.Abs(err));
                        sumErr += Math.Abs(err);
                        drift += err;
                    }
                    colPrev = c; rowPrev = r;
                }
            }

            double meanErr = sumErr / (double)measured.Length;
            Console.WriteLine("     Div=" + d + "  window=" + (maxShift * d) + "px (" + maxShift + " samples)"
                              + "  locked " + locked + "/" + measured.Length
                              + "  worstErr=" + worstErr + "  meanErr=" + meanErr.ToString("0.0")
                              + "  netDrift=" + drift + " px"
                              + "  offsets=[" + string.Join(",", new List<int>(measured).ConvertAll(v => v.ToString()).ToArray()) + "]");

            if (locked == measured.Length && worstErr <= d && bestDiv < 0)
            {
                bestDiv = d;
                bestNote = "Div=" + d + " locks every frame, worstErr=" + worstErr + " <= quantum " + d;
            }
        }

        Console.WriteLine();
        Check(bestDiv > 0,
              "with the search window expressed in PIXELS, a downsample factor locks EVERY frame within one quantum",
              bestNote == null ? "none found" : bestNote);
        Check(bestDiv == 1,
              "full-resolution (Div=1) correlation is required for stitching accuracy (Div=6 quantizes to 6 px)",
              bestDiv == 1 ? "Div=1 confirmed" : "first adequate Div=" + bestDiv);

        // Quantization claim, stated precisely: with Div=6 a 119 px scroll reads
        // as a multiple of 6.
        int[] c6 = null, r6 = null, c6b = null, r6b = null;
        using (Bitmap a = RenderDoc(TotalH, ViewH, 0, W)) ProfilesAt(a, 6, out c6, out r6);
        using (Bitmap b = RenderDoc(TotalH, ViewH, 119, W)) ProfilesAt(b, 6, out c6b, out r6b);
        int q6 = -BestShift(r6, r6b, Search) * 6;
        Check(q6 % 6 == 0, "Div=6 offsets are quantized to multiples of 6 px", "119 px scroll measured as " + q6 + " px");

        // Detection ceiling: FollowSearch * Div.
        int over = Search * Div + 20;
        int overMeasured = -9999;
        using (Bitmap a = RenderDoc(TotalH, ViewH, 0, W))
        using (Bitmap b = RenderDoc(TotalH, ViewH, over, W))
        {
            int[] ca, ra, cb, rb;
            ProfilesAt(a, Div, out ca, out ra);
            ProfilesAt(b, Div, out cb, out rb);
            overMeasured = BestShift(ra, rb, Search) * Div;
        }
        Console.WriteLine("     overshoot test: scrolled " + over + " px (past the " + (Search * Div)
                          + " px window), detector reported " + overMeasured + " px");
        Check(Math.Abs(overMeasured) <= Search * Div,
              "a scroll past the " + (Search * Div) + " px search window can never report a wild offset",
              "reported " + overMeasured);

        // Stitch at full resolution, which the sweep above selects, and verify
        // the composite against a single render of the whole document.
        Bitmap stitched = null;
        try
        {
            int[] colPrev = null, rowPrev = null;
            int[] measured = new int[steps.Length - 1];
            int idx = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                using (Bitmap bmp = RenderDoc(TotalH, ViewH, steps[i], W))
                {
                    int[] c, r;
                    ProfilesAt(bmp, 1, out c, out r);
                    if (i > 0) measured[idx++] = -BestShift(rowPrev, r, WindowPx);
                    colPrev = c; rowPrev = r;
                }
            }
            int scrollSum = 0;
            foreach (int s in measured) scrollSum += s;
            int expectH = ViewH + scrollSum;
            stitched = new Bitmap(W, expectH);
            using (Graphics g = Graphics.FromImage(stitched))
            {
                g.Clear(Color.White);
                int drawn = 0;
                for (int i = 0; i < steps.Length; i++)
                {
                    using (Bitmap bmp = RenderDoc(TotalH, ViewH, steps[i], W))
                    {
                        if (i == 0) { g.DrawImageUnscaled(bmp, 0, 0); drawn = ViewH; }
                        else
                        {
                            int dd = measured[i - 1];
                            g.DrawImage(bmp, new Rectangle(0, drawn, W, dd),
                                             new Rectangle(0, ViewH - dd, W, dd), GraphicsUnit.Pixel);
                            drawn += dd;
                        }
                    }
                }
            }
            Check(stitched.Height == expectH, "stitched image has the expected total height",
                  stitched.Height + " (expected " + expectH + ", scroll total " + scrollSum + ")");

            using (Bitmap truth = new Bitmap(W, expectH))
            {
                using (Graphics g = Graphics.FromImage(truth))
                {
                    g.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    const int Row = 12;
                    for (int y = 0; y < expectH + Row; y += Row)
                    {
                        if (y > TotalH) continue;
                        int k = y / Row;
                        uint h = (uint)(k * 2654435761u);
                        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                        int x = 4;
                        uint bits = h;
                        while (x < W - 20)
                        {
                            bits = bits * 1103515245u + 12345u;
                            int run = 10 + (int)((bits >> 16) % 70);
                            int gap = 4 + (int)((bits >> 8) % 22);
                            if ((bits & 1) == 0)
                                g.FillRectangle(Brushes.Black, x, y + 1, Math.Min(run, W - 20 - x), Row - 3);
                            x += run + gap;
                        }
                        if (k % 5 == 0)
                            g.FillRectangle(Brushes.Black, 0, y + Row - 3, W, 2);
                    }
                }
                int diff = 0, total = 0;
                for (int y = 0; y < stitched.Height; y += 3)
                    for (int x = 0; x < W; x += 3)
                    {
                        total++;
                        Color a = stitched.GetPixel(x, y), b = truth.GetPixel(x, y);
                        if (Math.Abs(a.R - b.R) > 40 || Math.Abs(a.G - b.G) > 40 || Math.Abs(a.B - b.B) > 40) diff++;
                    }
                double pct = 100.0 * diff / total;
                Check(pct < 2.0, "stitched output matches the true document (no duplicated/missing rows)",
                      "mismatch=" + pct.ToString("0.00") + "% over " + total + " samples");
            }
        }
        catch (Exception ex) { Check(false, "stitch frames", ex.Message); }
        finally { if (stitched != null) stitched.Dispose(); }
    }

    // ================= B) beautifier =======================================
    static void TestBeautifier()
    {
        Console.WriteLine();
        Console.WriteLine("-- B) beautifier (padding / rounded corners / shadow / background) --");

        const int SW = 400, SH = 240, Pad = 48, Radius = 24, Shadow = 14;
        int outW = SW + Pad * 2 + Shadow, outH = SH + Pad * 2 + Shadow;

        Bitmap outp = null;
        try
        {
            outp = new Bitmap(outW, outH);
            using (Graphics g = Graphics.FromImage(outp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // gradient background
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, outW, outH), Color.FromArgb(255, 96, 108, 220), Color.FromArgb(255, 176, 96, 220), 35f))
                    g.FillRectangle(bg, 0, 0, outW, outH);

                Rectangle card = new Rectangle(Pad, Pad, SW, SH);

                // drop shadow: draw the rounded rect a few times with increasing
                // alpha offset, which is cheap and looks smooth enough.
                for (int i = Shadow; i >= 1; i--)
                {
                    int a = (int)(70.0 * (Shadow - i + 1) / Shadow);
                    using (GraphicsPath sp = Rounded(card.X + i / 2, card.Y + i / 2, card.Width, card.Height, Radius))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }

                // the screenshot itself, clipped to a rounded rect
                using (GraphicsPath cp = Rounded(card.X, card.Y, card.Width, card.Height, Radius))
                {
                    g.SetClip(cp);
                    using (Bitmap shot = new Bitmap(SW, SH))
                    {
                        using (Graphics gs = Graphics.FromImage(shot))
                        {
                            gs.Clear(Color.WhiteSmoke);
                            gs.FillRectangle(Brushes.CornflowerBlue, 20, 20, 160, 90);
                            using (Font f = new Font("Segoe UI", 18f))
                                gs.DrawString("screenshot", f, Brushes.Black, new PointF(24, 130));
                        }
                        g.DrawImageUnscaled(shot, card.X, card.Y);
                    }
                    g.ResetClip();
                }
                // hairline border
                using (GraphicsPath bp = Rounded(card.X, card.Y, card.Width, card.Height, Radius))
                using (Pen p = new Pen(Color.FromArgb(60, 0, 0, 0), 1f))
                    g.DrawPath(p, bp);
            }
            Check(outp.Width == outW && outp.Height == outH, "beautified canvas sized with padding + shadow",
                  outW + "x" + outH);

            // The corner just outside the card must show background, not the
            // screenshot -> proves the rounded clip actually took effect.
            Color corner = outp.GetPixel(Pad + 2, Pad + 2);
            Check(corner.B > 150 && corner.R > 60 && corner.R < 200,
                  "rounded corner shows background (clip applied, not a square screenshot)",
                  "corner=" + corner.R + "," + corner.G + "," + corner.B);

            // Centre must be the screenshot content. The test screenshot is
            // WhiteSmoke with a blue block, so at the card centre we expect the
            // near-neutral WhiteSmoke, clearly distinct from the purple gradient.
            Color centre = outp.GetPixel(Pad + SW / 2, Pad + SH / 2);
            bool neutral = Math.Abs(centre.R - centre.G) < 8 && Math.Abs(centre.G - centre.B) < 8;
            Check(neutral && centre.R > 200,
                  "card centre is screenshot content (neutral WhiteSmoke, not the coloured background)",
                  "centre=" + centre.R + "," + centre.G + "," + centre.B);

            // And the blue block must survive the rounded clip.
            Color block = outp.GetPixel(Pad + 100, Pad + 60);
            Check(block.B > block.R + 40, "screenshot's blue block preserved through the clip",
                  "block=" + block.R + "," + block.G + "," + block.B);

            // Shadow region below the card must be darker than the raw gradient.
            Color below = outp.GetPixel(Pad + SW / 2, Pad + SH + Shadow / 2);
            using (Bitmap refImg = new Bitmap(outW, outH))
            {
                using (Graphics g2 = Graphics.FromImage(refImg))
                using (LinearGradientBrush bg2 = new LinearGradientBrush(
                    new Rectangle(0, 0, outW, outH), Color.FromArgb(255, 96, 108, 220), Color.FromArgb(255, 176, 96, 220), 35f))
                    g2.FillRectangle(bg2, 0, 0, outW, outH);
                Color raw = refImg.GetPixel(Pad + SW / 2, Pad + SH + Shadow / 2);
                Check(below.GetBrightness() < raw.GetBrightness(),
                      "drop shadow actually darkens the background beneath the card",
                      "with=" + below.GetBrightness().ToString("0.00") + " without=" + raw.GetBrightness().ToString("0.00"));
            }

            string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screenpen_beautify.png");
            outp.Save(outPath, ImageFormat.Png);
            Check(new System.IO.FileInfo(outPath).Length > 0, "beautified PNG written",
                  (new System.IO.FileInfo(outPath).Length / 1024) + " KB");
            try { System.IO.File.Delete(outPath); } catch { }
        }
        catch (Exception ex) { Check(false, "beautify render", ex.Message); }
        finally { if (outp != null) outp.Dispose(); }
    }

    static GraphicsPath Rounded(int x, int y, int w, int h, int r)
    {
        GraphicsPath gp = new GraphicsPath();
        if (r * 2 > w) r = w / 2;
        if (r * 2 > h) r = h / 2;
        gp.AddArc(x, y, r * 2, r * 2, 180, 90);
        gp.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        gp.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        gp.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    static void Main()
    {
        Console.WriteLine("=== ROADMAP FEASIBILITY PROBE ===");
        Console.WriteLine("runtime = " + Environment.Version);
        Console.WriteLine();
        TestScrolling();
        TestBeautifier();
        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
    }
}
