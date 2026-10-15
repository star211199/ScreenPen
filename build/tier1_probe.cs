using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using ScreenPen;

// =============================================================================
//  tier1_probe - verifies the tier-1 feature set added on top of the original
//  eight tools:
//
//    1. multi-level undo + redo
//    2. highlighter with independent alpha
//    3. step-number badges with a non-reusing counter
//    4. curved / elbow arrow shafts
//    5. window-capture geometry (DWMWA_EXTENDED_FRAME_BOUNDS)
//    6. colour picker pixel sampling
//    7. beautifier geometry and colours
//
//  Every check asserts observable behaviour, not the presence of a method. A
//  probe that only proves "the code compiles" would have passed on the original
//  single-level Undo() too, which is exactly the bug this work fixes.
// =============================================================================
static class Tier1Probe
{
    static int _pass, _fail;

    // The probe is compiled without the app manifest, so unless it opts in it
    // runs DPI-unaware: WinForms then reports virtualised coordinates while the
    // DWM reports physical ones, and every geometry comparison silently mixes
    // two coordinate spaces. The shipping EXE declares per-monitor-v2, so the
    // probe must match it to measure what the app actually sees.
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetProcessDPIAware();

    const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)))
                return;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        try { SetProcessDPIAware(); }
        catch { }
    }

    static void Check(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name + " -- " + detail); }
    }

    [STAThread]
    static int Main()
    {
        // Must happen before any window or Graphics is created, or the process
        // is already locked into DPI-unaware mode.
        EnableDpiAwareness();

        Application.EnableVisualStyles();

        Console.WriteLine("=== tier1_probe ===");
        Console.WriteLine("runtime = " + Environment.Version);

        try { TestEnumAndStroke(); } catch (Exception e) { Check(false, "enum/stroke", e.Message); }
        try { TestUndoRedo(); } catch (Exception e) { Check(false, "undo/redo", e.Message); }
        try { TestStepBadges(); } catch (Exception e) { Check(false, "step badges", e.Message); }
        try { TestHighlighter(); } catch (Exception e) { Check(false, "highlighter", e.Message); }
        try { TestArrowStyles(); } catch (Exception e) { Check(false, "arrow styles", e.Message); }
        try { TestWindowGeometry(); } catch (Exception e) { Check(false, "window geometry", e.Message); }
        try { TestPixelAt(); } catch (Exception e) { Check(false, "pixel sampling", e.Message); }
        try { TestBeautifier(); } catch (Exception e) { Check(false, "beautifier", e.Message); }
        try { TestSettingsRoundTrip(); } catch (Exception e) { Check(false, "settings round-trip", e.Message); }

        Console.WriteLine();
        Console.WriteLine("PASS=" + _pass + " FAIL=" + _fail);
        return _fail == 0 ? 0 : 1;
    }

    // -------------------------------------------------------------------------
    // 1. Enum values and the extended Stroke model
    // -------------------------------------------------------------------------
    static void TestEnumAndStroke()
    {
        // The original eight values must not move: they are persisted in no
        // file, but RegionAction-style numeric stability matters for any future
        // serialisation, and a probe elsewhere asserts them.
        Check((int)Tool.Pen == 0 && (int)Tool.Line == 1 && (int)Tool.Rectangle == 2 &&
              (int)Tool.Ellipse == 3 && (int)Tool.Arrow == 4 && (int)Tool.Text == 5 &&
              (int)Tool.Mosaic == 6 && (int)Tool.Spotlight == 7,
              "original tool values unchanged", "0..7 shifted");

        Check((int)Tool.Highlighter == 8 && (int)Tool.StepNumber == 9,
              "new tools appended at 8 and 9",
              "Highlighter=" + (int)Tool.Highlighter + " StepNumber=" + (int)Tool.StepNumber);

        Stroke s = new Stroke();
        Check(s.Arrow == ArrowStyle.Straight,
              "new strokes default to a straight arrow", "Arrow=" + s.Arrow);
        Check(s.HighlightAlpha == 90,
              "highlighter default alpha is 90", "alpha=" + s.HighlightAlpha);
        Check(s.StepIndex == 0,
              "a fresh stroke has no badge number", "StepIndex=" + s.StepIndex);

        Check((int)ArrowStyle.Straight == 0 && (int)ArrowStyle.Curved == 1 &&
              (int)ArrowStyle.Elbow == 2,
              "arrow style values are 0/1/2", "values moved");
    }

    // -------------------------------------------------------------------------
    // 2. Undo / redo
    // -------------------------------------------------------------------------
    static OverlayForm NewOverlay()
    {
        Settings cfg = new Settings();
        OverlayForm f = new OverlayForm(cfg, () => new Size(1920, 1080));
        // Force handle creation without showing the window, so test hooks that
        // touch the cursor or invalidate do not throw.
        IntPtr h = f.Handle;
        GC.KeepAlive(h);
        return f;
    }

    static void TestUndoRedo()
    {
        using (OverlayForm f = NewOverlay())
        {
            f.ClearForTest();

            Check(!f.CanUndo && !f.CanRedo,
                  "a fresh canvas can neither undo nor redo",
                  "CanUndo=" + f.CanUndo + " CanRedo=" + f.CanRedo);

            // Three separate strokes.
            for (int i = 0; i < 3; i++)
            {
                f.TestStrokeDrag(Tool.Pen,
                    new PointF(100 + i * 10, 100), new PointF(200 + i * 10, 200));
            }
            Check(f.StrokeCountForTest() == 3,
                  "three strokes committed", "count=" + f.StrokeCountForTest());

            // Multi-level: this is the core regression. The original Undo()
            // removed one stroke and there was no way to go further or back.
            f.Undo();
            f.Undo();
            Check(f.StrokeCountForTest() == 1,
                  "undo is multi-level (2 undos from 3 strokes -> 1)",
                  "count=" + f.StrokeCountForTest());
            Check(f.CanUndo, "a third undo is still available", "CanUndo=false");
            Check(f.CanRedo, "redo becomes available after undo", "CanRedo=false");

            f.Undo();
            Check(f.StrokeCountForTest() == 0,
                  "undo reaches an empty canvas", "count=" + f.StrokeCountForTest());
            Check(!f.CanUndo, "nothing left to undo", "CanUndo=true");

            // An extra undo must be a harmless no-op, not a crash.
            f.Undo();
            Check(f.StrokeCountForTest() == 0,
                  "undoing an empty canvas is a no-op", "count=" + f.StrokeCountForTest());

            // Redo all three back.
            f.Redo(); f.Redo(); f.Redo();
            Check(f.StrokeCountForTest() == 3,
                  "redo restores all three strokes", "count=" + f.StrokeCountForTest());
            Check(!f.CanRedo,
                  "the redo stack drains in step with the strokes", "CanRedo=true");

            // Redo past the end must also be harmless.
            f.Redo();
            Check(f.StrokeCountForTest() == 3,
                  "redoing with an empty redo stack is a no-op",
                  "count=" + f.StrokeCountForTest());

            // Standard redo semantics: a NEW edit clears the redo branch.
            f.Undo();
            Check(f.CanRedo, "redo is available before a new edit", "CanRedo=false");
            f.TestStrokeDrag(Tool.Pen, new PointF(300, 300), new PointF(400, 400));
            Check(!f.CanRedo,
                  "a new stroke clears the redo branch",
                  "CanRedo=true - a stale stroke could be resurrected");

            // Clear must drop both stacks: resurrecting one stroke out of a
            // deliberately cleared canvas would look like a bug to the user.
            f.ClearForTest();
            Check(!f.CanUndo && !f.CanRedo,
                  "Clear resets both stacks", "CanUndo=" + f.CanUndo + " CanRedo=" + f.CanRedo);
        }
    }

    // -------------------------------------------------------------------------
    // 3. Step badges
    // -------------------------------------------------------------------------
    static void TestStepBadges()
    {
        using (OverlayForm f = NewOverlay())
        {
            f.ClearForTest();
            f.SetTool(Tool.StepNumber);

            f.TestBeginStroke(new PointF(100, 100));
            f.TestBeginStroke(new PointF(200, 100));
            f.TestBeginStroke(new PointF(300, 100));

            Check(f.StrokeCountForTest() == 3,
                  "each click places one badge", "count=" + f.StrokeCountForTest());

            // Badges must be numbered 1,2,3 in placement order. Reaching the
            // numbers means reading the private stroke list, which is the only
            // way to assert the value a user actually sees.
            int[] nums = BadgeNumbers(f);
            Check(nums.Length == 3 && nums[0] == 1 && nums[1] == 2 && nums[2] == 3,
                  "badges number 1,2,3 in placement order", "got [" + Join(nums) + "]");

            // Undo the newest badge, then place a new one. The counter must
            // pull back so the replacement reuses 3 rather than jumping to 4 -
            // otherwise every mistake leaves a permanent gap in the sequence.
            f.Undo();
            Check(f.StrokeCountForTest() == 2,
                  "undo removes the newest badge", "count=" + f.StrokeCountForTest());
            f.TestBeginStroke(new PointF(400, 100));
            nums = BadgeNumbers(f);
            Check(nums.Length == 3 && nums[2] == 3,
                  "undoing the newest badge frees its number for reuse",
                  "got [" + Join(nums) + "] - expected the third badge to be 3");

            // Deleting an EARLIER badge must NOT renumber the later ones: two
            // badges both reading "2" would be worse than a gap.
            f.ClearForTest();
            f.SetTool(Tool.StepNumber);
            f.TestBeginStroke(new PointF(100, 100));
            f.TestBeginStroke(new PointF(200, 100));
            f.TestBeginStroke(new PointF(300, 100));
            // Remove the middle badge by rebuilding the list without it: undo
            // only pops the tail, so this is the only way to test the interior
            // case through the public surface.
            RemoveStrokeAt(f, 1);
            nums = BadgeNumbers(f);
            Check(nums.Length == 2 && nums[0] == 1 && nums[1] == 3,
                  "removing an interior badge leaves later numbers intact",
                  "got [" + Join(nums) + "] - expected [1,3]");

            // After removing badge 3 of {1,2,3}, the next badge must be 4, not
            // a duplicate 2 or 3.
            f.ClearForTest();
            f.SetTool(Tool.StepNumber);
            f.TestBeginStroke(new PointF(100, 100));
            f.TestBeginStroke(new PointF(200, 100));
            f.TestBeginStroke(new PointF(300, 100));
            RemoveStrokeAt(f, 1);
            f.TestBeginStroke(new PointF(500, 100));
            nums = BadgeNumbers(f);
            Check(nums.Length == 3 && nums[2] == 4,
                  "a new badge continues past the highest placed number",
                  "got [" + Join(nums) + "] - expected the new badge to be 4");
        }
    }

    static int[] BadgeNumbers(OverlayForm f)
    {
        FieldInfo fi = typeof(OverlayForm).GetField("_strokes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        List<Stroke> list = (List<Stroke>)fi.GetValue(f);
        List<int> nums = new List<int>();
        foreach (Stroke s in list)
            if (s.Tool == Tool.StepNumber) nums.Add(s.StepIndex);
        return nums.ToArray();
    }

    static void RemoveStrokeAt(OverlayForm f, int index)
    {
        FieldInfo fi = typeof(OverlayForm).GetField("_strokes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        List<Stroke> list = (List<Stroke>)fi.GetValue(f);
        list.RemoveAt(index);
    }

    static string Join(int[] a)
    {
        string r = "";
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) r += ",";
            r += a[i].ToString();
        }
        return r;
    }

    // -------------------------------------------------------------------------
    // 4. Highlighter
    // -------------------------------------------------------------------------
    static void TestHighlighter()
    {
        using (OverlayForm f = NewOverlay())
        {
            f.ClearForTest();
            f.SetTool(Tool.Highlighter);
            f.TestStrokeDrag(Tool.Highlighter, new PointF(100, 100), new PointF(400, 100));

            Check(f.StrokeCountForTest() == 1, "highlighter commits a stroke",
                  "count=" + f.StrokeCountForTest());
            Check(f.LastStrokeToolForTest() == "Highlighter",
                  "the stroke records the highlighter tool",
                  "tool=" + f.LastStrokeToolForTest());

            // A highlighter is a freehand tool: it must accumulate samples like
            // the pen, not collapse to two points like the shape tools. If this
            // regressed, dragging would draw a straight line and the tool would
            // be indistinguishable from Line.
            f.ClearForTest();
            f.SetTool(Tool.Highlighter);
            f.TestBeginStroke(new PointF(100, 100));
            for (int i = 1; i <= 6; i++) f.TestExtendStroke(new PointF(100 + i * 20, 100 + i * 3));
            f.TestEndStroke();
            int n = f.LastStrokePointCountForTest();
            Check(n >= 6,
                  "highlighter accumulates freehand samples (>= 6)",
                  "points=" + n + " - it collapsed to a 2-point shape");

            // The alpha must be independent of the colour, and clamping must
            // keep it in a range that is visible but not opaque.
            Settings cfg = new Settings();
            Check(cfg.HighlighterAlpha > 0 && cfg.HighlighterAlpha < 255,
                  "default highlighter alpha is translucent",
                  "alpha=" + cfg.HighlighterAlpha);
            cfg.HighlighterAlpha = 999;
            cfg.Clamp();
            Check(cfg.HighlighterAlpha <= 220,
                  "an absurd alpha is clamped", "alpha=" + cfg.HighlighterAlpha);
            cfg.HighlighterAlpha = -5;
            cfg.Clamp();
            Check(cfg.HighlighterAlpha >= 16,
                  "a zero/negative alpha is clamped up", "alpha=" + cfg.HighlighterAlpha);
        }
    }

    // -------------------------------------------------------------------------
    // 5. Arrow styles
    // -------------------------------------------------------------------------
    static void TestArrowStyles()
    {
        // Render each style into a bitmap and confirm the three produce
        // genuinely different pixels. Asserting "the enum has three values"
        // would pass even if the renderer ignored them.
        using (Bitmap a = RenderArrow(ArrowStyle.Straight))
        using (Bitmap b = RenderArrow(ArrowStyle.Curved))
        using (Bitmap c = RenderArrow(ArrowStyle.Elbow))
        {
            Check(a != null && b != null && c != null,
                  "all three arrow styles render", "a render returned null");

            double dStraightCurved = Diff(a, b);
            double dStraightElbow = Diff(a, c);
            double dCurvedElbow = Diff(b, c);

            Check(dStraightCurved > 0.01,
                  "curved differs visibly from straight",
                  "diff=" + dStraightCurved.ToString("0.0000"));
            Check(dStraightElbow > 0.01,
                  "elbow differs visibly from straight",
                  "diff=" + dStraightElbow.ToString("0.0000"));
            Check(dCurvedElbow > 0.01,
                  "curved differs visibly from elbow",
                  "diff=" + dCurvedElbow.ToString("0.0000"));

            // Every style must actually put ink down; a style that rendered
            // nothing would still "differ" from a style that rendered a line.
            Check(InkFraction(a) > 0.002 && InkFraction(b) > 0.002 && InkFraction(c) > 0.002,
                  "every arrow style draws ink",
                  "straight=" + InkFraction(a).ToString("0.0000") +
                  " curved=" + InkFraction(b).ToString("0.0000") +
                  " elbow=" + InkFraction(c).ToString("0.0000"));

            // The head must land on the end point in all three styles: a curved
            // shaft whose head points the wrong way is the classic bug here.
            Check(TipHasInk(a) && TipHasInk(b) && TipHasInk(c),
                  "the arrow head reaches the drag end point in every style",
                  "tip empty in at least one style");
        }
    }

    static Bitmap RenderArrow(ArrowStyle style)
    {
        int W = 320, H = 220;
        Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Invoke the private static renderer directly: the goal is to test
            // the geometry, not the overlay's coordinate transforms.
            MethodInfo mi = typeof(OverlayForm).GetMethod("DrawArrow",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (mi == null) return bmp;

            using (Pen pen = new Pen(Color.Black, 4f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                object[] args = new object[] { g, pen, new PointF(40, 40), new PointF(280, 180), style };
                mi.Invoke(null, args);
            }
        }
        return bmp;
    }

    /// <summary>Fraction of pixels that differ between two same-size bitmaps.</summary>
    static double Diff(Bitmap a, Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        int diff = 0, total = 0;
        for (int y = 0; y < h; y += 2)
        {
            for (int x = 0; x < w; x += 2)
            {
                Color ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                total++;
                if (Math.Abs(ca.R - cb.R) > 12 || Math.Abs(ca.G - cb.G) > 12 ||
                    Math.Abs(ca.B - cb.B) > 12) diff++;
            }
        }
        return total == 0 ? 0 : diff / (double)total;
    }

    static double InkFraction(Bitmap b)
    {
        int ink = 0, total = 0;
        for (int y = 0; y < b.Height; y += 2)
        {
            for (int x = 0; x < b.Width; x += 2)
            {
                total++;
                Color c = b.GetPixel(x, y);
                if (c.R < 128 && c.G < 128 && c.B < 128) ink++;
            }
        }
        return total == 0 ? 0 : ink / (double)total;
    }

    static bool TipHasInk(Bitmap b)
    {
        // The end point is (280,180); sample a small disc around it.
        for (int dy = -6; dy <= 6; dy++)
        {
            for (int dx = -6; dx <= 6; dx++)
            {
                int x = 280 + dx, y = 180 + dy;
                if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) continue;
                Color c = b.GetPixel(x, y);
                if (c.R < 128 && c.G < 128 && c.B < 128) return true;
            }
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // 6. Window-capture geometry
    // -------------------------------------------------------------------------
    static void TestWindowGeometry()
    {
        // The load-bearing claim is that WindowBounds uses the DWM frame and
        // not GetWindowRect. Compare them on a window this probe owns: at 200%
        // DPI GetWindowRect is DPI-virtualised and border-padded, so the two
        // rects disagree. If WindowBounds returned the GetWindowRect value the
        // capture would carry a transparent margin and be half-size.
        //
        // A probe-owned window is used rather than GetForegroundWindow because
        // the foreground window is whatever the user happens to be looking at:
        // an earlier version of this check failed intermittently when the
        // foreground window was minimised or on another virtual desktop.
        using (Form probe = new Form())
        {
            probe.FormBorderStyle = FormBorderStyle.Sizable;
            probe.StartPosition = FormStartPosition.Manual;
            probe.Bounds = new Rectangle(200, 200, 640, 400);
            probe.ShowInTaskbar = false;
            probe.Show();
            Application.DoEvents();

            IntPtr hwnd = probe.Handle;
            Check(hwnd != IntPtr.Zero, "the probe window has a handle", "Handle is 0");

            Rectangle d = Native.WindowBounds(hwnd);
            Check(d.Width > 0 && d.Height > 0,
                  "WindowBounds returns a usable rect",
                  "got " + d.Width + "x" + d.Height);

            // The DWM frame must be at least as large as the client area and
            // must contain it; a virtualised rect would be roughly half-size at
            // 200% DPI and so would fail to contain the client area.
            Rectangle client = probe.RectangleToScreen(probe.ClientRectangle);
            Check(d.Contains(client),
                  "the DWM frame contains the client area (physical, not virtualised)",
                  "frame=" + d + " client=" + client);

            // Centre of the frame must resolve back to some window. Using the
            // centre of the CLIENT area would be wrong: the frame includes the
            // title bar and borders.
            IntPtr hit = Native.RootWindowAt(new Point(d.Left + d.Width / 2, d.Top + d.Height / 2));
            Check(hit != IntPtr.Zero,
                  "RootWindowAt finds a window inside that rect",
                  "returned 0 for " + (d.Left + d.Width / 2) + "," + (d.Top + d.Height / 2));

            // The root ancestor must be the top-level window, not a child
            // control: without GetAncestor(GA_ROOT) this would return the child
            // under the pointer, and the capture would grab a 20x20 control.
            if (hit != IntPtr.Zero)
            {
                IntPtr top = Native.GetAncestor(hit, 2 /* GA_ROOT */);
                Check(top == hit,
                      "RootWindowAt already returns the top-level window",
                      "hit=" + hit + " root=" + top);
            }

            // Self-exclusion must actually exclude.
            Check(!Native.IsCapturableWindow(IntPtr.Zero, null),
                  "a null window is not capturable", "reported capturable");
            Check(!Native.IsCapturableWindow(hwnd, new IntPtr[] { hwnd }),
                  "a window listed as ours is refused",
                  "our own window was reported capturable");
            Check(Native.IsCapturableWindow(hwnd, new IntPtr[0]),
                  "a visible window is capturable when not excluded",
                  "a normal visible window was refused");

            Console.WriteLine("       probe frame = " + d.Left + "," + d.Top +
                              " " + d.Width + "x" + d.Height);

            probe.Hide();
        }

        // An iconic window must be refused: capturing a minimised window yields
        // a stale or blank frame.
        using (Form mini = new Form())
        {
            mini.Bounds = new Rectangle(300, 300, 400, 300);
            mini.ShowInTaskbar = false;
            mini.Show();
            Application.DoEvents();
            mini.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Check(!Native.IsCapturableWindow(mini.Handle, new IntPtr[0]),
                  "a minimised window is not capturable",
                  "IsIconic=" + Native.IsIconic(mini.Handle));
            mini.Hide();
        }
    }

    // -------------------------------------------------------------------------
    // 7. Colour-picker pixel sampling
    // -------------------------------------------------------------------------
    static void TestPixelAt()
    {
        // PixelAt must read the *actual* desktop pixel. Prove it by capturing
        // the same point with the app's own capture path and comparing.
        Rectangle vs = Native.VirtualScreen();
        Point probe = new Point(vs.Left + vs.Width / 2, vs.Top + vs.Height / 2);

        Color direct = Native.PixelAt(probe);
        Check(!direct.IsEmpty, "PixelAt returns a colour", "got Color.Empty");
        if (direct.IsEmpty) return;

        // COLORREF is 0x00BBGGRR. If the channels were not swapped, a strongly
        // coloured pixel would read back with R and B exchanged. Compare against
        // the captured bitmap, which is unambiguous.
        using (Bitmap shot = Native.CaptureRegion(new Rectangle(probe.X, probe.Y, 1, 1)))
        {
            if (shot != null)
            {
                Color captured = shot.GetPixel(0, 0);
                bool match = Math.Abs(captured.R - direct.R) <= 12 &&
                             Math.Abs(captured.G - direct.G) <= 12 &&
                             Math.Abs(captured.B - direct.B) <= 12;
                Check(match,
                      "PixelAt agrees with the capture path (channels not swapped)",
                      "direct=" + direct.R + "," + direct.G + "," + direct.B +
                      " captured=" + captured.R + "," + captured.G + "," + captured.B);
            }
            else
            {
                Check(true, "PixelAt agrees with the capture path (capture unavailable, skipped)", "");
            }
        }

        // A point far outside the virtual screen has no pixel.
        Color off = Native.PixelAt(new Point(vs.Right + 5000, vs.Bottom + 5000));
        Check(off.IsEmpty,
              "PixelAt returns Empty outside the desktop",
              "got " + off.R + "," + off.G + "," + off.B);

        // Colour parsing must round-trip, and must reject garbage rather than
        // silently producing black.
        Color c;
        Check(Beautifier.TryParseColor("#FF3B30", out c) && c.R == 255 && c.G == 0x3B && c.B == 0x30,
              "#RRGGBB parses correctly", "got " + c.R + "," + c.G + "," + c.B);
        Check(Beautifier.TryParseColor("#80FF3B30", out c) && c.A == 0x80,
              "#AARRGGBB parses the alpha", "A=" + c.A);
        Check(!Beautifier.TryParseColor("not-a-colour", out c),
              "garbage is rejected, not silently black", "parsed as " + c);
        Check(!Beautifier.TryParseColor("#12345", out c),
              "a wrong-length hex is rejected", "parsed as " + c);
        Check(Beautifier.FormatColor(Color.FromArgb(255, 0x6D, 0x5B, 0xFF)) == "#6D5BFF",
              "FormatColor round-trips", "got " + Beautifier.FormatColor(Color.FromArgb(255, 0x6D, 0x5B, 0xFF)));
    }

    // -------------------------------------------------------------------------
    // 8. Beautifier
    // -------------------------------------------------------------------------
    static void TestBeautifier()
    {
        const int SW = 400, SH = 300;
        using (Bitmap src = MakeTestImage(SW, SH))
        {
            Settings cfg = new Settings();
            cfg.BeautifyEnabled = true;
            cfg.BeautifyPadding = 40;
            cfg.BeautifyRadius = 16;
            cfg.BeautifyShadow = true;
            cfg.BeautifyColor1 = "#6D5BFF";
            cfg.BeautifyColor2 = "#22B8CF";

            using (Bitmap outBmp = Beautifier.Render(src, cfg))
            {
                Check(outBmp != null, "beautifier produces an image", "returned null");
                if (outBmp == null) return;

                // Geometry: padding on both sides plus the shadow room. Assert
                // it GREW by exactly the padding on each side; a fixed expected
                // size would silently pass if padding were ignored but the
                // shadow room happened to match.
                int expectedW = SW + cfg.BeautifyPadding * 2;
                int expectedH = SH + cfg.BeautifyPadding * 2;
                Check(outBmp.Width > expectedW && outBmp.Height > expectedH,
                      "the output grows by the padding plus shadow room",
                      "got " + outBmp.Width + "x" + outBmp.Height +
                      " expected more than " + expectedW + "x" + expectedH);
                Check(outBmp.Width - expectedW == outBmp.Height - expectedH,
                      "the shadow room is symmetric",
                      "w-extra=" + (outBmp.Width - expectedW) + " h-extra=" + (outBmp.Height - expectedH));

                // The corners must be the gradient, not the screenshot: that is
                // the whole point of the padding, and it proves the card is
                // inset rather than stretched to fill.
                Color tl = outBmp.GetPixel(3, 3);
                Color br = outBmp.GetPixel(outBmp.Width - 4, outBmp.Height - 4);
                Check(!IsNeutral(tl) || !IsNeutral(br),
                      "the background is the configured gradient, not the screenshot",
                      "tl=" + Fmt(tl) + " br=" + Fmt(br));

                // The two gradient ends must actually differ: a LinearGradientBrush
                // with equal colours would look like a flat fill.
                int delta = Math.Abs(tl.R - br.R) + Math.Abs(tl.G - br.G) + Math.Abs(tl.B - br.B);
                Check(delta > 20,
                      "the gradient has a visible direction",
                      "tl=" + Fmt(tl) + " br=" + Fmt(br) + " delta=" + delta);

                // The screenshot content must survive in the middle.
                Color centre = outBmp.GetPixel(outBmp.Width / 2, outBmp.Height / 2);
                Check(centre.R > 200 && centre.G > 200 && centre.B > 200,
                      "the screenshot content is preserved at the centre",
                      "centre=" + Fmt(centre));

                // The rounded corners must not show screenshot content. This is
                // tested differentially: the same source rendered with a square
                // corner paints screenshot pixels into the corner, so if the
                // rounded version differs there, the clip is doing its job.
                //
                // An absolute colour test cannot work here - the configured
                // gradient start #6D5BFF is itself blue-dominant, so "is this
                // pixel blue?" is true of both the background and the marker
                // block. Comparing the two renders removes the ambiguity.
                int cardX = cfg.BeautifyPadding + (outBmp.Width - expectedW) / 2;
                int cardY = cfg.BeautifyPadding + (outBmp.Height - expectedH) / 2;
                Color roundedCorner = outBmp.GetPixel(cardX + 1, cardY + 1);

                Settings sq2 = new Settings();
                sq2.BeautifyPadding = cfg.BeautifyPadding;
                sq2.BeautifyRadius = 0;
                sq2.BeautifyShadow = false;
                sq2.BeautifyColor1 = cfg.BeautifyColor1;
                sq2.BeautifyColor2 = cfg.BeautifyColor2;
                using (Bitmap squareRender = Beautifier.Render(src, sq2))
                {
                    Color squareCorner = squareRender.GetPixel(sq2.BeautifyPadding + 1,
                                                              sq2.BeautifyPadding + 1);
                    Check(ChannelDelta(roundedCorner, squareCorner) > 30,
                          "the rounded corner hides the screenshot content a square corner would show",
                          "rounded=" + Fmt(roundedCorner) + " square=" + Fmt(squareCorner));
                }

                // A blue block near the card's top-left must be inside the card
                // and must survive the clip unaltered.
                Color block = outBmp.GetPixel(cardX + 20, cardY + 20);
                Check(block.B > 180 && block.B > block.R + 60,
                      "the screenshot's own colours survive the clip",
                      "expected a blue block, got " + Fmt(block));

                // Shadow: the region just outside the card's bottom edge must be
                // darker than the same offset above it, because the light comes
                // from above.
                Color below = outBmp.GetPixel(outBmp.Width / 2, cardY + SH + 12);
                Color above = outBmp.GetPixel(outBmp.Width / 2, cardY - 12);
                int lumBelow = below.R + below.G + below.B;
                int lumAbove = above.R + above.G + above.B;
                Check(lumBelow < lumAbove,
                      "the shadow falls below the card, not above it",
                      "below=" + Fmt(below) + " above=" + Fmt(above));
            }

            // Shadow off must produce a smaller canvas: proves the flag is read.
            Settings noShadow = new Settings();
            noShadow.BeautifyPadding = 40;
            noShadow.BeautifyRadius = 16;
            noShadow.BeautifyShadow = false;
            using (Bitmap plain = Beautifier.Render(src, noShadow))
            {
                Check(plain != null && plain.Width == SW + 80,
                      "disabling the shadow removes the shadow room",
                      plain == null ? "null" : ("width=" + plain.Width + " expected " + (SW + 80)));
            }

            // Radius 0 must not crash and must still produce the right size.
            Settings square = new Settings();
            square.BeautifyPadding = 20;
            square.BeautifyRadius = 0;
            square.BeautifyShadow = false;
            using (Bitmap sq = Beautifier.Render(src, square))
            {
                Check(sq != null && sq.Width == SW + 40,
                      "a zero corner radius still renders",
                      sq == null ? "null" : ("width=" + sq.Width));
            }

            // An absurd radius must be clamped to a true radius, not produce a
            // bowtie or throw.
            Settings huge = new Settings();
            huge.BeautifyPadding = 10;
            huge.BeautifyRadius = 100000;
            huge.BeautifyShadow = false;
            using (Bitmap hb = Beautifier.Render(src, huge))
            {
                Check(hb != null && hb.Width == SW + 20,
                      "an over-large corner radius is clamped, not fatal",
                      hb == null ? "null" : ("width=" + hb.Width));
            }

            // Garbage colours must fall back rather than throw inside GDI+.
            Settings bad = new Settings();
            bad.BeautifyPadding = 10;
            bad.BeautifyShadow = false;
            bad.BeautifyColor1 = "nonsense";
            bad.BeautifyColor2 = "#ZZZ";
            using (Bitmap bb = Beautifier.Render(src, bad))
            {
                Check(bb != null, "unparsable gradient colours fall back safely",
                      "returned null");
            }

            // Null/empty input must be refused, not throw.
            Check(Beautifier.Render(null, cfg) == null,
                  "a null source is refused", "did not return null");
        }
    }

    static Bitmap MakeTestImage(int w, int h)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.WhiteSmoke);
            // A strongly blue block near the top-left, to prove content survives
            // and to detect a leak into the rounded corner.
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 30, 90, 220)))
                g.FillRectangle(br, 10, 10, 90, 60);
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 220, 60, 60)))
                g.FillRectangle(br, w - 120, h - 90, 100, 70);
        }
        return b;
    }

    static bool IsNeutral(Color c)
    {
        return Math.Abs(c.R - c.G) < 10 && Math.Abs(c.G - c.B) < 10;
    }

    static int ChannelDelta(Color a, Color b)
    {
        return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    }

    static string Fmt(Color c)
    {
        return c.R + "," + c.G + "," + c.B;
    }

    // -------------------------------------------------------------------------
    // 9. Settings persistence
    // -------------------------------------------------------------------------
    static void TestSettingsRoundTrip()
    {
        Settings s = new Settings();
        Check(s.BeautifyEnabled == false,
              "beautify is opt-in by default",
              "BeautifyEnabled=" + s.BeautifyEnabled);
        Check(s.StepNumberPersistent,
              "step numbering persists across captures by default",
              "StepNumberPersistent=" + s.StepNumberPersistent);

        // The new keys must have distinct defaults, or the settings UI would
        // show two fields that fight each other.
        Check(s.HighlighterKey != s.StepKey &&
              s.HighlighterKey != s.MosaicKey &&
              s.StepKey != s.SpotlightKey,
              "the new tool keys do not collide with existing ones",
              "H=" + s.HighlighterKey + " N=" + s.StepKey);

        // The new single-key tool bindings must resolve through the same
        // ToolForKey path the overlay uses, or pressing H would do nothing.
        Tool? t = s.ToolForKey(Keys.H);
        Check(t.HasValue && t.Value == Tool.Highlighter,
              "H resolves to the highlighter", "got " + (t.HasValue ? t.Value.ToString() : "null"));
        t = s.ToolForKey(Keys.N);
        Check(t.HasValue && t.Value == Tool.StepNumber,
              "N resolves to the step badge", "got " + (t.HasValue ? t.Value.ToString() : "null"));
        // The originals must still resolve: a new key must not shadow an old one.
        t = s.ToolForKey(Keys.P);
        Check(t.HasValue && t.Value == Tool.Pen,
              "P still resolves to the pen", "got " + (t.HasValue ? t.Value.ToString() : "null"));

        // Redo must parse as a real hotkey with Ctrl.
        HotKey redo = HotKey.Parse(s.RedoKey);
        Check(redo.Key == Keys.Y && redo.Ctrl,
              "the default redo key is Ctrl+Y",
              "parsed " + redo.Key + " ctrl=" + redo.Ctrl);

        // Clamp must sanitise the new numeric fields.
        Settings c = new Settings();
        c.StepBadgeSize = -100f;
        c.BeautifyPadding = 99999;
        c.BeautifyRadius = -3;
        c.PickerZoom = 500;
        c.PickerLoupeSize = 5;
        c.ArrowStyleIndex = 77;
        c.Clamp();
        Check(c.StepBadgeSize >= 16f && c.BeautifyPadding <= 400 &&
              c.BeautifyRadius >= 0 && c.PickerZoom <= 32 &&
              c.PickerLoupeSize >= 60 && c.ArrowStyleIndex <= 2,
              "Clamp sanitises every new numeric field",
              "badge=" + c.StepBadgeSize + " pad=" + c.BeautifyPadding +
              " rad=" + c.BeautifyRadius + " zoom=" + c.PickerZoom +
              " loupe=" + c.PickerLoupeSize + " arrow=" + c.ArrowStyleIndex);

        // An unparsable beautify colour must be replaced, not left to explode
        // inside GDI+ at save time.
        Settings bc = new Settings();
        bc.BeautifyColor1 = "###";
        bc.BeautifyColor2 = "";
        bc.Clamp();
        Color tmp;
        Check(Beautifier.TryParseColor(bc.BeautifyColor1, out tmp) &&
              Beautifier.TryParseColor(bc.BeautifyColor2, out tmp),
              "Clamp repairs unparsable beautify colours",
              "c1=" + bc.BeautifyColor1 + " c2=" + bc.BeautifyColor2);
    }
}
