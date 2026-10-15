// Verifies scroll-follow: when the underlying document scrolls, the annotations
// must move by exactly the same amount so they stay attached to the content.
//
// The correlation is driven with synthetic luminance frames rather than live
// screen captures, so the result is deterministic and does not depend on what
// happens to be on screen.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ScreenPen;

class FollowProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    /// <summary>
    /// Builds a synthetic "document" frame: vertical bands of varying brightness
    /// with a strong horizontal edge, so the row profile has a distinctive shape
    /// that a shift can be recovered from.
    /// </summary>
    static byte[] Frame(int w, int h, int offsetY, int offsetX)
    {
        byte[] g = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int docY = y + offsetY;
            // A pattern that repeats every 24 rows but with a large-scale ramp,
            // so only the true shift aligns well.
            int band = (docY / 6) % 4;
            int luma = 40 + band * 45 + (docY % 220);
            if (luma > 250) luma = 250;
            for (int x = 0; x < w; x++)
            {
                int docX = x + offsetX;
                int v = luma + ((docX / 7) % 3) * 6;
                if (v > 255) v = 255;
                g[y * w + x] = (byte)v;
            }
        }
        return g;
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();

        Settings cfg = new Settings();
        cfg.ScrollFollowEnabled = true;
        cfg.WheelScrollsDocument = true;

        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov.ActivateOverlay(false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);
        Application.DoEvents();

        const int W = 120, H = 90;      // profile-space dimensions

        // ------------------------------------------------------------------
        // 1. A scrolled frame is detected and reported in screen pixels.
        // ------------------------------------------------------------------
        Console.WriteLine("--- scroll detection ---");
        byte[] a = Frame(W, H, 0, 0);

        // Seed the baseline with the first frame (no previous profile yet).
        float sx, sy;
        bool first = ov.ComputeFollowShift(a, W, H, out sx, out sy);
        Check(!first, "the first frame yields no shift (nothing to compare)",
              string.Format("dx={0:F1} dy={1:F1}", sx, sy));

        // Scroll the document down by 30 profile rows -> 30*6 = 180 screen px.
        // Keep the shift inside the sanity bound by using a smaller step.
        const int ScrollRows = 3;                       // profile rows
        byte[] b = Frame(W, H, ScrollRows, 0);
        bool got = ov.ComputeFollowShift(b, W, H, out sx, out sy);
        Console.WriteLine(string.Format("  scrolled {0} profile rows -> reported dy={1:F1} dx={2:F1}",
                                        ScrollRows, sy, sx));
        Check(got, "a scrolled frame is detected", got ? "" : "no shift found");

        int pdx, pdy;
        ov.LastScrollDelta(out pdx, out pdy);
        Console.WriteLine(string.Format("  LastScrollDelta = ({0},{1}) profile samples", pdx, pdy));
        Check(pdy != 0, "the vertical shift is reported", "dy=" + pdy);
        Check(pdx == 0, "no spurious horizontal shift on a vertical scroll", "dx=" + pdx);

        // ------------------------------------------------------------------
        // 2. Static content must never drift.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- static content ---");
        // Re-seed, then feed the identical frame repeatedly.
        ov.ComputeFollowShift(a, W, H, out sx, out sy);
        int drift = 0;
        for (int i = 0; i < 8; i++)
        {
            bool any = ov.ComputeFollowShift(a, W, H, out sx, out sy);
            if (any) drift++;
        }
        Check(drift == 0, "an unchanged frame never reports a shift",
              drift + " false positives in 8 frames");

        // ------------------------------------------------------------------
        // 3. A noisy but static frame must also be rejected.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- noise rejection ---");
        Random rnd = new Random(12345);
        ov.ComputeFollowShift(a, W, H, out sx, out sy);
        int noisy = 0;
        for (int i = 0; i < 8; i++)
        {
            byte[] n = (byte[])a.Clone();
            for (int k = 0; k < n.Length; k += 3)
            {
                int v = n[k] + rnd.Next(-4, 5);
                n[k] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
            if (ov.ComputeFollowShift(n, W, H, out sx, out sy)) noisy++;
        }
        Check(noisy <= 1, "mild sensor noise does not trigger a scroll",
              noisy + " false positives in 8 noisy frames");

        // ------------------------------------------------------------------
        // 4. ShiftAnnotations moves strokes so they track the content.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- annotations follow the content ---");
        ov.TestBeginStroke(new PointF(100, 100));
        ov.TestExtendStroke(new PointF(200, 150));
        ov.TestEndStroke();
        Check(ov.StrokeCount == 1, "one stroke recorded", "count=" + ov.StrokeCount);

        List<PointF> before = ov.FirstStrokePoints();
        PointF b0 = before[0];

        const float MoveX = 12f, MoveY = 34f;
        ov.ShiftAnnotations(MoveX, MoveY);

        List<PointF> after = ov.FirstStrokePoints();
        PointF a0 = after[0];
        Console.WriteLine(string.Format("  stroke[0] {0} -> {1}", b0, a0));
        Check(Math.Abs(a0.X - (b0.X + MoveX)) < 0.01f &&
              Math.Abs(a0.Y - (b0.Y + MoveY)) < 0.01f,
              "every point moves by the scroll delta",
              string.Format("expected ({0:F1},{1:F1}) got ({2:F1},{3:F1})",
                            b0.X + MoveX, b0.Y + MoveY, a0.X, a0.Y));

        Check(after.Count == before.Count,
              "shifting preserves the point count",
              after.Count + " vs " + before.Count);

        // A zero shift must be a genuine no-op.
        ov.ShiftAnnotations(0, 0);
        PointF a1 = ov.FirstStrokePoints()[0];
        Check(a1.X == a0.X && a1.Y == a0.Y, "a zero shift does not move anything",
              string.Format("({0:F1},{1:F1})", a1.X, a1.Y));

        // ------------------------------------------------------------------
        // 5. Round trip: scroll away and back returns to the start.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- round trip ---");
        float rx, ry;
        ov.ComputeFollowShift(a, W, H, out rx, out ry);       // baseline = a
        ov.ComputeFollowShift(b, W, H, out rx, out ry);       // scrolled down
        float downY = ry;
        ov.ComputeFollowShift(a, W, H, out rx, out ry);       // scrolled back
        float backY = ry;
        Console.WriteLine(string.Format("  down dy={0:F1}  back dy={1:F1}", downY, backY));
        Check(downY != 0 && backY != 0, "both directions of the round trip are detected",
              string.Format("down={0:F1} back={1:F1}", downY, backY));
        Check(Math.Sign(downY) != Math.Sign(backY),
              "scrolling back reports the opposite direction",
              string.Format("down={0:F1} back={1:F1}", downY, backY));

        // ------------------------------------------------------------------
        // 6. The toggle must gate the feature.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- toggle gating ---");
        bool on = ov.ToggleScrollFollow();
        Check(on, "scroll-follow can be switched on", "active=" + on);
        Check(ov.ScrollFollowActive, "the overlay reports it is following", "");
        bool off = ov.ToggleScrollFollow();
        Check(!off, "scroll-follow can be switched back off", "active=" + off);
        Check(!ov.ScrollFollowActive, "the overlay reports it stopped", "");

        // Disabled in settings: the toggle must refuse to enable it.
        Settings off2 = new Settings();
        off2.ScrollFollowEnabled = false;
        OverlayForm ov2 = new OverlayForm(off2, delegate { return new Size(32, 32); });
        ov2.ActivateOverlay(false);
        Application.DoEvents();
        bool forced = ov2.ToggleScrollFollow();
        Check(!forced, "the toggle refuses when disabled in settings",
              "active=" + forced);
        Check(ov2.LastFollowRefusal.Length > 0,
              "the refusal explains why", "\"" + ov2.LastFollowRefusal + "\"");
        ov2.DeactivateOverlay();

        // ------------------------------------------------------------------
        // 7. The idle counter is actually consumed, so follow mode ends by
        //    itself instead of capturing the screen forever.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- idle exit ---");
        OverlayForm ov3 = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov3.ActivateOverlay(false);
        Application.DoEvents();
        Check(ov3.ToggleScrollFollow(), "follow mode is on for the idle test", "");

        // Identical frames = no detected scroll = a miss on every tick.
        ov3.ComputeFollowShift(a, W, H, out rx, out ry);   // seeds baseline
        int missesAfterFirst = ov3.FollowMissesForTest;
        for (int i = 0; i < 25; i++)
            ov3.ComputeFollowShift(a, W, H, out rx, out ry);
        Check(ov3.FollowMissesForTest > missesAfterFirst,
              "repeated identical frames accumulate misses",
              missesAfterFirst + " -> " + ov3.FollowMissesForTest);

        // A real scroll must clear the counter, otherwise one still moment
        // would end follow mode for good.
        ov3.ComputeFollowShift(b, W, H, out rx, out ry);
        Check(ov3.FollowMissesForTest == 0,
              "a detected scroll resets the miss counter",
              "misses=" + ov3.FollowMissesForTest);
        ov3.DeactivateOverlay();

        // The live-mode refusal must be visible rather than a silent no-op.
        OverlayForm ov4 = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov4.ActivateOverlay(true);
        Application.DoEvents();
        bool live = ov4.ToggleScrollFollow();
        Check(!live, "follow mode is refused in live-zoom mode", "active=" + live);
        Check(ov4.LastFollowRefusal.Length > 0,
              "live-mode refusal is reported to the user",
              "\"" + ov4.LastFollowRefusal + "\"");
        ov4.DeactivateOverlay();

        // ------------------------------------------------------------------
        // 8. Follow mode must route the wheel to the document on its own.
        //
        //    The original bug: F turned on annotation tracking, but wheel
        //    pass-through required the separate WheelScrollsDocument setting.
        //    With that off, F reported success and the wheel still changed pen
        //    width, so the document never scrolled and nothing ever followed.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- follow mode drives the wheel itself ---");
        Settings wheelOff = new Settings();
        wheelOff.ScrollFollowEnabled = true;
        wheelOff.WheelScrollsDocument = false;      // the default, and the bug
        OverlayForm ov5 = new OverlayForm(wheelOff, delegate { return new Size(32, 32); });
        ov5.ActivateOverlay(false);
        Application.DoEvents();

        Check(ov5.WheelGoesToDocumentForTest() == false,
              "with follow off, the wheel stays on pen width", "");
        Check(ov5.ToggleScrollFollow(), "follow mode turns on", "");
        Check(ov5.WheelGoesToDocumentForTest(),
              "turning follow ON routes the wheel to the document "
              + "even when WheelScrollsDocument is false",
              "WheelScrollsDocument=" + wheelOff.WheelScrollsDocument);
        ov5.DeactivateOverlay();

        ov.DeactivateOverlay();

        Console.WriteLine();
        if (fails == 0) { Console.WriteLine("ALL SCROLL-FOLLOW CHECKS PASSED"); return 0; }
        Console.WriteLine(fails + " SCROLL-FOLLOW CHECK(S) FAILED");
        return 1;
    }
}
