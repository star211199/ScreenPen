// Probe for the 浮标 (cursor spot) feature, modelled on cuiliang/ClickShow's
// hover dot + click indicator.
//
// Sections, because the failures are independent and a combined probe would
// hide which one regressed:
//
//   1. smoothing curve      - the slider must do something monotonic, and 0 must
//                             mean "glued to the pointer", not "slightly behind"
//   2. circle geometry      - surface size, centre, and the actual pixels: fill
//                             alpha in the middle, nothing in the corners, the
//                             ring at the radius
//   3. window styles        - read the REAL ex-styles off the HWND; the whole
//                             feature is unusable if the window is not
//                             TRANSPARENT (it would eat every click)
//   4. lifecycle            - enable/disable, nested suspend/resume, preview
//   5. capture guard        - the circle must be off screen while WE grab the
//                             screen, or it ends up inside our own screenshots
//   6. ripple               - ring not disc, window centred on the click, and it
//                             expires
//   7. settings + dialog    - keys round-trip through the file, the 浮标 page
//                             binds both ways, and the live-preview callback fires
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ScreenPen;

static class CursorProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    static void Pump(int ms)
    {
        int end = Environment.TickCount + ms;
        while (Environment.TickCount - end < 0)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
    }

    static int Alpha(Bitmap b, int x, int y)
    {
        if (b == null) return -1;
        if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return -1;
        return b.GetPixel(x, y).A;
    }

    /// <summary>Largest alpha found anywhere on a surface, sampling every pixel.</summary>
    static int MaxAlpha(Bitmap b)
    {
        if (b == null) return -1;
        int best = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int a = b.GetPixel(x, y).A;
                if (a > best) best = a;
            }
        return best;
    }

    [STAThread]
    static int Main()
    {
        try { Application.EnableVisualStyles(); }
        catch { }

        // The probe's settings live beside its own exe, so the user's real
        // configuration is never touched. Start from a clean slate anyway, or a
        // previous run's leftover "enabled = true" would change the baseline.
        string cfgPath = Settings.SettingsPath();
        try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { }

        Console.WriteLine("=== CURSOR SPOT PROBE ===");

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 1. smoothing curve --");

        Check(CursorSpotOverlay.EaseFactor(0, 0.015f) == 1f,
              "smoothing 0 is glued to the pointer (factor 1)",
              CursorSpotOverlay.EaseFactor(0, 0.015f).ToString("0.###"));

        float prev = 1.1f;
        bool monotonic = true;
        for (int sm = 0; sm <= 90; sm += 10)
        {
            float k = CursorSpotOverlay.EaseFactor(sm, 0.015f);
            if (k < 0f || k > 1f || k > prev + 1e-6f) monotonic = false;
            prev = k;
        }
        Check(monotonic, "the factor shrinks monotonically as smoothing rises",
              "0->" + CursorSpotOverlay.EaseFactor(0, 0.015f).ToString("0.###") +
              " 90->" + CursorSpotOverlay.EaseFactor(90, 0.015f).ToString("0.###"));

        Check(CursorSpotOverlay.EaseFactor(60, 0.030f) >
              CursorSpotOverlay.EaseFactor(60, 0.015f),
              "a longer frame closes more of the gap (time-based, not per-frame)",
              CursorSpotOverlay.EaseFactor(60, 0.015f).ToString("0.####") + " -> " +
              CursorSpotOverlay.EaseFactor(60, 0.030f).ToString("0.####"));

        Check(CursorSpotOverlay.EaseFactor(200, 0.015f) ==
              CursorSpotOverlay.EaseFactor(90, 0.015f),
              "out-of-range smoothing is clamped, not extrapolated", "");

        Check(CursorSpotOverlay.EaseFactor(50, 0f) == 0f,
              "a zero-length frame moves nothing", "");

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 2. circle geometry and pixels --");

        Settings on = new Settings();
        on.CursorSpotEnabled = true;
        on.CursorSpotRadius = 30;
        on.CursorSpotRingWidth = 6;
        on.CursorSpotFillAlpha = 100;
        on.CursorSpotRingAlpha = 200;
        on.CursorSpotFillColor = "#FFCC00";
        on.CursorSpotRingColor = "#FFFFFF";
        on.CursorSpotSmoothing = 0;
        on.Clamp();

        using (CursorSpotOverlay ov = new CursorSpotOverlay())
        {
            ov.ApplySettings(on);
            Check(ov.IsRunning, "enabling the spot starts the follow loop", "");

            ov.RenderAtForTest(600, 400);

            Bitmap surf = ov.SpotSurfaceForTest;
            Check(surf != null, "the spot has a surface", surf == null ? "null" : surf.Size.ToString());

            // half = ceil(radius + ring/2) + 1 = ceil(33) + 1 = 34
            int expectSide = 68;
            Check(surf != null && surf.Width == expectSide && surf.Height == expectSide,
                  "the surface is exactly the circle plus its ring and one pixel of slack",
                  surf == null ? "null" : surf.Width + "x" + surf.Height + " (expected " + expectSide + ")");
            Check(ov.SpotSideForTest == expectSide, "the window reports the same side",
                  ov.SpotSideForTest.ToString());

            int cx = surf == null ? 0 : surf.Width / 2;
            int cy = surf == null ? 0 : surf.Height / 2;

            int centre = Alpha(surf, cx, cy);
            Check(Math.Abs(centre - 100) <= 8,
                  "the middle carries the fill's alpha", "alpha=" + centre);

            // The ring straddles the radius, so a pixel at exactly `radius` from
            // the centre must be ring, not fill and not background.
            int ring = Alpha(surf, cx + 30, cy);
            Check(ring >= 150, "a pixel on the radius is the ring", "alpha=" + ring);

            Check(Alpha(surf, 0, 0) == 0 && Alpha(surf, surf.Width - 1, surf.Height - 1) == 0,
                  "the corners are fully transparent",
                  Alpha(surf, 0, 0) + " / " + Alpha(surf, surf.Width - 1, surf.Height - 1));

            // The window must be centred on the point we asked for.
            Rectangle wr = ov.SpotWindowRectForTest;
            Check(wr.Width == expectSide &&
                  wr.Left + wr.Width / 2 == 600 && wr.Top + wr.Height / 2 == 400,
                  "the window is centred on the pointer", wr.ToString());

            // ---- an outline-only circle: fill alpha 0 ----------------------
            Settings outline = on.Clone();
            outline.CursorSpotFillAlpha = 0;
            ov.ApplySettings(outline);
            ov.RenderAtForTest(600, 400);
            surf = ov.SpotSurfaceForTest;
            Check(Alpha(surf, surf.Width / 2, surf.Height / 2) == 0,
                  "fill alpha 0 leaves the middle empty",
                  Alpha(surf, surf.Width / 2, surf.Height / 2).ToString());
            Check(Alpha(surf, surf.Width / 2 + 30, surf.Height / 2) >= 150,
                  "…while the ring is still drawn",
                  Alpha(surf, surf.Width / 2 + 30, surf.Height / 2).ToString());

            // ---- ring width 0 with no fill: nothing at all -----------------
            Settings empty = on.Clone();
            empty.CursorSpotRingWidth = 0;
            empty.CursorSpotFillAlpha = 0;
            ov.ApplySettings(empty);
            ov.RenderAtForTest(600, 400);
            Check(MaxAlpha(ov.SpotSurfaceForTest) == 0,
                  "ring width 0 plus fill alpha 0 draws nothing",
                  MaxAlpha(ov.SpotSurfaceForTest).ToString());

            // ---- radius is honoured ---------------------------------------
            Settings big = on.Clone();
            big.CursorSpotRadius = 120;
            big.CursorSpotRingWidth = 2;
            ov.ApplySettings(big);
            ov.RenderAtForTest(600, 400);
            // half = ceil(120 + 1) + 1 = 122 -> 244
            Check(ov.SpotSideForTest == 244, "a bigger radius grows the window",
                  ov.SpotSideForTest + " (expected 244)");

            // ---- 3. real window styles ------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- 3. window styles (read off the HWND) --");

            ov.ApplySettings(on);
            ov.RenderAtForTest(600, 400);
            Pump(150);

            IntPtr h = ov.SpotHandleForTest;
            Check(h != IntPtr.Zero, "the spot window has a real HWND", "0x" + h.ToString("X"));
            if (h != IntPtr.Zero)
            {
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                Check((ex & Native.WS_EX_LAYERED) != 0,
                      "WS_EX_LAYERED is set (required by UpdateLayeredWindow)", "0x" + ex.ToString("X"));
                Check((ex & Native.WS_EX_TRANSPARENT) != 0,
                      "WS_EX_TRANSPARENT is set (clicks reach the app underneath)",
                      "0x" + ex.ToString("X"));
                Check((ex & Native.WS_EX_NOACTIVATE) != 0,
                      "WS_EX_NOACTIVATE is set (never steals focus)",
                      "0x" + ex.ToString("X"));
            }

            // =============================================================
            Console.WriteLine();
            Console.WriteLine("-- 4. lifecycle, suspend and preview --");

            Settings off = on.Clone();
            off.CursorSpotEnabled = false;
            off.CursorRippleEnabled = false;
            ov.ApplySettings(off);
            Pump(120);
            Check(!ov.IsRunning, "switching both features off stops the loop", "");
            Check(!ov.SpotVisibleForTest, "…and takes the circle off the screen", "");

            ov.ApplySettings(on);
            Pump(150);
            Check(ov.IsRunning && ov.SpotVisibleForTest, "switching it back on shows it again", "");

            // Nested suspend: two Suspend calls need two Resumes.
            ov.Suspend();
            Pump(60);
            Check(!ov.SpotVisibleForTest && ov.SuspendDepthForTest == 1,
                  "Suspend hides the circle", "depth=" + ov.SuspendDepthForTest);

            ov.Suspend();
            Pump(60);
            Check(!ov.SpotVisibleForTest && ov.SuspendDepthForTest == 2,
                  "a nested Suspend does not change what is on screen",
                  "depth=" + ov.SuspendDepthForTest);

            ov.Resume();
            Pump(60);
            Check(!ov.SpotVisibleForTest && ov.SuspendDepthForTest == 1,
                  "one Resume is not enough to bring it back", "depth=" + ov.SuspendDepthForTest);

            ov.Resume();
            Pump(120);
            Check(ov.SpotVisibleForTest && ov.SuspendDepthForTest == 0,
                  "the last Resume restores it", "depth=" + ov.SuspendDepthForTest);

            ov.Resume();
            Check(ov.SuspendDepthForTest == 0, "an extra Resume cannot underflow", "");

            // Preview: the real scenario is tuning a feature that is switched
            // OFF, so start from off and check the draft draws anyway.
            ov.ApplySettings(off);
            Pump(120);
            Check(!ov.IsRunning && !ov.SpotVisibleForTest,
                  "with the saved setting off, nothing is drawn", "");

            Settings draft = off.Clone();
            draft.CursorSpotRadius = 55;
            ov.BeginPreview(draft);
            Pump(120);
            Check(ov.IsRunning && ov.SpotVisibleForTest,
                  "preview draws even when the saved setting is off", "");
            ov.RenderAtForTest(400, 300);
            // half = ceil(55 + 3) + 1 = 59 -> 118
            Check(ov.SpotSideForTest == 118,
                  "preview uses the draft's radius, not the saved one",
                  ov.SpotSideForTest + " (expected 118)");

            ov.EndPreview();
            Pump(120);
            Check(!ov.IsRunning && !ov.SpotVisibleForTest,
                  "EndPreview falls back to the saved settings", "");

            // ---- 5. capture guard -----------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- 5. the circle is hidden while WE capture the screen --");

            ov.ApplySettings(on);
            Pump(150);
            Check(ov.SpotVisibleForTest, "the circle is up before the capture", "");

            Action<bool> original = Native.CaptureGuard;
            bool visibleDuring = true;
            int guardCalls = 0;
            Native.CaptureGuard = delegate(bool entering)
            {
                if (original != null) original(entering);
                // Read the state AFTER the overlay's own guard ran: that is the
                // state the BitBlt will actually see.
                if (entering) { guardCalls++; visibleDuring = ov.SpotVisibleForTest; }
            };
            try
            {
                using (Bitmap grab = Native.CaptureRegion(new Rectangle(0, 0, 32, 32)))
                {
                    Check(grab != null, "a capture still returns pixels",
                          grab == null ? "null" : grab.Size.ToString());
                }
            }
            finally
            {
                Native.CaptureGuard = original;
            }

            Check(guardCalls > 0, "the capture funnel consulted the guard", "calls=" + guardCalls);
            Check(!visibleDuring, "the circle was off screen during the capture",
                  "visible=" + visibleDuring);

            Pump(150);
            Check(ov.SpotVisibleForTest, "…and it comes back afterwards", "");

            // ---- 6. ripple -------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- 6. click ripple --");

            Settings rip = on.Clone();
            rip.CursorSpotEnabled = false;
            rip.CursorRippleEnabled = true;
            rip.CursorRippleSize = 150;
            ov.ApplySettings(rip);
            Pump(120);
            Check(!ov.SpotVisibleForTest, "the spot stays off while the ripple is on", "");
            Check(ov.RippleCountForTest == 0, "no ripple before a click", "");

            ov.AddRippleForTest(700, 500, "#1E90FF");
            Check(ov.RippleCountForTest == 1, "a click adds one ripple",
                  ov.RippleCountForTest.ToString());

            // 300 ms in: the ring has expanded well past the centre, so the
            // middle must be empty and the window must be centred on the click.
            ov.AgeRipplesForTest(300);
            ov.RenderAtForTest(700, 500);

            Bitmap rs = ov.RippleSurfaceForTest;
            Check(rs != null && MaxAlpha(rs) > 0, "the ripple surface has drawn pixels",
                  rs == null ? "null" : "maxAlpha=" + MaxAlpha(rs));

            if (rs != null)
            {
                int mid = Alpha(rs, rs.Width / 2, rs.Height / 2);
                Check(mid == 0, "a ripple is a ring, not a disc", "centre alpha=" + mid);
            }

            Rectangle rr = ov.RippleWindowRectForTest;
            Check(Math.Abs(rr.Left + rr.Width / 2 - 700) <= 1 &&
                  Math.Abs(rr.Top + rr.Height / 2 - 500) <= 1,
                  "the ripple window is centred on where the click landed",
                  rr.ToString() + "  centre=" + (rr.Left + rr.Width / 2) + "," +
                  (rr.Top + rr.Height / 2));

            // Past its lifetime the ripple is dropped, not left frozen on screen.
            ov.AgeRipplesForTest(1000);
            ov.RenderAtForTest(700, 500);
            Check(ov.RippleCountForTest == 0, "an expired ripple is discarded",
                  ov.RippleCountForTest.ToString());
            Check(!ov.RippleVisibleForTest, "…and its window is hidden", "");
        }

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 7. settings, file round-trip and the 浮标 page --");

        Settings s = new Settings();
        Check(!s.CursorSpotEnabled && !s.CursorRippleEnabled,
              "both features default to off", "");
        Check(s.CursorSpotKey == "Ctrl+Alt+H", "the default hotkey is Ctrl+Alt+H", s.CursorSpotKey);
        Check(s.CursorSpotRadius == 40 && s.CursorSpotRingWidth == 2,
              "the default radius/ring are 40/2",
              s.CursorSpotRadius + "/" + s.CursorSpotRingWidth);

        s.CursorSpotEnabled = true;
        s.CursorSpotRadius = 77;
        s.CursorSpotRingWidth = 5;
        s.CursorSpotFillAlpha = 33;
        s.CursorSpotRingAlpha = 222;
        s.CursorSpotSmoothing = 45;
        s.CursorSpotFillColor = "#123456";
        s.CursorSpotRingColor = "#ABCDEF";
        s.CursorRippleEnabled = true;
        s.CursorRippleSize = 222;
        s.CursorRippleLeftColor = "#010203";
        s.CursorRippleRightColor = "#040506";
        s.CursorRippleMiddleColor = "#070809";
        s.CursorSpotKey = "Ctrl+Alt+F9";

        bool saved = SettingsStore.Save(s);
        Check(saved, "the settings file can be written", cfgPath);
        Settings back = SettingsStore.Load();
        Check(back.CursorSpotEnabled && back.CursorSpotRadius == 77 &&
              back.CursorSpotRingWidth == 5 && back.CursorSpotSmoothing == 45,
              "spot geometry survives the file round-trip",
              back.CursorSpotRadius + "/" + back.CursorSpotRingWidth + "/" + back.CursorSpotSmoothing);
        Check(back.CursorSpotFillAlpha == 33 && back.CursorSpotRingAlpha == 222 &&
              back.CursorSpotFillColor == "#123456" && back.CursorSpotRingColor == "#ABCDEF",
              "spot colours and alphas survive",
              back.CursorSpotFillColor + "@" + back.CursorSpotFillAlpha + " / " +
              back.CursorSpotRingColor + "@" + back.CursorSpotRingAlpha);
        Check(back.CursorRippleEnabled && back.CursorRippleSize == 222 &&
              back.CursorRippleLeftColor == "#010203" &&
              back.CursorRippleMiddleColor == "#070809",
              "ripple settings survive",
              back.CursorRippleSize + " " + back.CursorRippleLeftColor);
        Check(back.CursorSpotKey == "Ctrl+Alt+F9", "the hotkey survives",
              back.CursorSpotKey);

        // ---- clamping --------------------------------------------------
        Settings bad = new Settings();
        bad.CursorSpotRadius = 5000;
        bad.CursorSpotRingWidth = -3;
        bad.CursorSpotFillAlpha = 999;
        bad.CursorSpotRingAlpha = -9;
        bad.CursorSpotSmoothing = 500;
        bad.CursorSpotFillColor = "not-a-colour";
        bad.CursorRippleSize = 5;
        bad.CursorSpotKey = "   ";
        bad.Clamp();
        Check(bad.CursorSpotRadius == 400 && bad.CursorSpotRingWidth == 0,
              "radius and ring width are clamped",
              bad.CursorSpotRadius + "/" + bad.CursorSpotRingWidth);
        Check(bad.CursorSpotFillAlpha == 255 && bad.CursorSpotRingAlpha == 0,
              "alphas are clamped to 0..255",
              bad.CursorSpotFillAlpha + "/" + bad.CursorSpotRingAlpha);
        Check(bad.CursorSpotSmoothing == 90, "smoothing is clamped to 0..90",
              bad.CursorSpotSmoothing.ToString());
        Check(bad.CursorSpotFillColor == "#FFCC00",
              "an unparseable colour falls back to the default", bad.CursorSpotFillColor);
        Check(bad.CursorRippleSize == 40, "ripple size is clamped", bad.CursorRippleSize.ToString());
        Check(bad.CursorSpotKey == new Settings().CursorSpotKey,
              "a blank hotkey falls back to the default", bad.CursorSpotKey);

        // ---- the dialog page -------------------------------------------
        Settings d = new Settings();
        d.CursorSpotRadius = 66;
        d.CursorSpotRingWidth = 4;
        d.CursorSpotFillAlpha = 44;
        d.CursorSpotSmoothing = 25;
        d.CursorSpotFillColor = "#AABBCC";
        d.CursorSpotRingColor = "#DDEEFF";
        d.CursorSpotEnabled = true;
        d.CursorRippleEnabled = true;
        d.CursorRippleSize = 321;
        d.CursorSpotKey = "Ctrl+Alt+F8";
        d.Clamp();

        using (SettingsForm f = SettingsForm.CreateForTest(d))
        {
            f.PromptOverride = delegate { return DialogResult.Yes; };

            Check(f.HasPageForTest("浮标"), "the dialog has a 浮标 page", "");
            Check(f.NavCountForTest == 7, "the nav rail lists 7 pages",
                  f.NavCountForTest.ToString());
            Check(f.SpotHotkeyForTest == "Ctrl+Alt+F8", "the 浮标 hotkey row shows the value",
                  f.SpotHotkeyForTest);
            Check(f.SpotEnabledForTest, "the enable checkbox shows the value", "");
            Check(f.SpotRadiusForTest == 66 && f.SpotRadiusTrackForTest == 66,
                  "radius box and slider agree on load",
                  f.SpotRadiusForTest + " / " + f.SpotRadiusTrackForTest);
            Check(f.SpotRingForTest == 4 && f.SpotSmoothForTest == 25,
                  "ring width and smoothing show the values",
                  f.SpotRingForTest + " / " + f.SpotSmoothForTest);
            Check(f.SpotFillColorForTest == "#AABBCC" && f.SpotRingColorForTest == "#DDEEFF",
                  "the colour buttons show the values",
                  f.SpotFillColorForTest + " / " + f.SpotRingColorForTest);
            Check(f.RippleEnabledForTest && f.RippleSizeForTest == 321,
                  "the ripple controls show the values",
                  f.RippleSizeForTest.ToString());

            // ---- live preview fires on every change --------------------
            int previews = 0;
            int lastRadius = -1;
            f.SpotPreviewChanged = delegate(Settings draft)
            {
                previews++;
                lastRadius = draft.CursorSpotRadius;
            };

            f.SetSpotRadiusForTest(120);
            Check(previews > 0, "changing the radius fires the live preview",
                  "previews=" + previews);
            Check(lastRadius == 120, "the preview carries the new radius",
                  lastRadius.ToString());
            Check(f.SpotRadiusTrackForTest == 120,
                  "the slider followed the box", f.SpotRadiusTrackForTest.ToString());

            int before = previews;
            f.SetSpotFillAlphaForTest(7);
            Check(previews > before, "changing an alpha fires the preview too",
                  "previews=" + previews);

            f.SetRippleEnabledForTest(false);
            Check(!f.DraftForTest.CursorRippleEnabled,
                  "the ripple checkbox writes into the draft", "");

            f.SetSpotRingForTest(9);
            f.SetSpotSmoothForTest(70);
            Check(f.DraftForTest.CursorSpotRingWidth == 9 && f.DraftForTest.CursorSpotSmoothing == 70,
                  "ring width and smoothing reach the draft",
                  f.DraftForTest.CursorSpotRingWidth + "/" + f.DraftForTest.CursorSpotSmoothing);
            Check(f.DraftForTest.CursorSpotRadius == 120 && f.DraftForTest.CursorSpotFillAlpha == 7,
                  "radius and alpha reach the draft",
                  f.DraftForTest.CursorSpotRadius + "/" + f.DraftForTest.CursorSpotFillAlpha);
        }

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 8. controller wiring (tray tick + hotkey toggle) --");

        AppContext2 ctx = null;
        try
        {
            // Section 7 left "enabled = true" on disk. Clear it so the
            // controller starts from the default, off, state.
            try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { }

            ctx = new AppContext2(true);
            CursorSpotOverlay live = ctx.CursorSpotForTest;
            Check(live != null, "the controller owns a spot overlay", "");
            Check(!live.IsRunning, "it stays idle while the setting is off", "");
            Check(!ctx.CursorSpotTrayCheckedForTest, "the tray tick starts clear", "");

            ctx.ToggleCursorSpotForTest(true);
            Pump(200);
            Check(live.IsRunning, "toggling from the tray starts the loop", "");
            Check(ctx.CursorSpotTrayCheckedForTest, "the tray tick follows the toggle", "");

            ctx.ToggleCursorSpotForTest(true);
            Pump(150);
            Check(!live.IsRunning, "toggling again stops it", "");
            Check(!ctx.CursorSpotTrayCheckedForTest, "…and clears the tick", "");

            // The toggle must persist, or the circle would come back off after a
            // restart and the tray tick would lie.
            ctx.ToggleCursorSpotForTest(true);
            Pump(100);
            Settings afterToggle = SettingsStore.Load();
            Check(afterToggle.CursorSpotEnabled,
                  "the toggle is written to the settings file", "");
        }
        finally
        {
            if (ctx != null)
            {
                try { ctx.Quit(); } catch { }
                try { ctx.Dispose(); } catch { }
            }
        }

        // Leave no state behind for the next run.
        try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { }

        Console.WriteLine();
        if (fails == 0)
        {
            Console.WriteLine("ALL CURSOR PROBES PASSED");
            return 0;
        }
        Console.WriteLine(fails + " check(s) failed");
        return 1;
    }
}
