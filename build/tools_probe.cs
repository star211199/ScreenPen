// Drives the real OverlayForm drawing pipeline for every tool and reports how
// many pixels each one changes. Uses in-process test hooks, so the result
// reflects the renderer rather than the reliability of synthetic input.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ScreenPen;

class ToolsProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    /// <summary>Fraction of pixels that differ meaningfully between two shots.</summary>
    static double Diff(Bitmap a, Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        Rectangle r = new Rectangle(0, 0, w, h);
        BitmapData da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int n = 0, c = 0;
        try
        {
            int stride = da.Stride;
            byte[] ra = new byte[stride * h];
            byte[] rb = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ra, 0, ra.Length);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0, rb, 0, rb.Length);
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x * 4;
                    n++;
                    if (Math.Abs(ra[i] - rb[i]) > 20 ||
                        Math.Abs(ra[i + 1] - rb[i + 1]) > 20 ||
                        Math.Abs(ra[i + 2] - rb[i + 2]) > 20) c++;
                }
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
        return n == 0 ? 0 : (double)c / n;
    }

    /// <summary>Count of dark pixels: used to see a spotlight actually dim.</summary>
    static double MeanLuma(Bitmap b)
    {
        Rectangle r = new Rectangle(0, 0, b.Width, b.Height);
        BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        double sum = 0; int n = 0;
        try
        {
            int stride = d.Stride;
            byte[] row = new byte[stride];
            for (int y = 0; y < b.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    (IntPtr)(d.Scan0.ToInt64() + (long)y * stride), row, 0, stride);
                for (int x = 0; x < b.Width; x++)
                {
                    int i = x * 4;
                    sum += 0.114 * row[i] + 0.587 * row[i + 1] + 0.299 * row[i + 2];
                    n++;
                }
            }
        }
        finally { b.UnlockBits(d); }
        return n == 0 ? 0 : sum / n;
    }

    [STAThread]
    static int Main()
    {
        // Never let a WinForms-level exception pop the JIT debugger dialog:
        // report it as a normal failure instead.
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine("  [FAIL] unhandled: " + e.ExceptionObject);
            fails++;
        };
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
        {
            Console.WriteLine("  [FAIL] thread exception: " + e.Exception.GetType().Name +
                              ": " + e.Exception.Message);
            fails++;
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.EnableVisualStyles();

        Settings cfg = new Settings();
        cfg.ScrollFollowEnabled = true;   // must not matter for rendering

        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov.ActivateOverlay(false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(400);
        Application.DoEvents();

        Console.WriteLine("overlay bounds = " + ov.Bounds.Width + "x" + ov.Bounds.Height);

        PointF from = new PointF(600, 500);
        PointF to = new PointF(1000, 780);

        // ---------------------------------------------------------------
        // Baseline: what the untouched overlay looks like.
        // ---------------------------------------------------------------
        Bitmap clean = ov.RenderComposite(false);
        Console.WriteLine("clean = " + clean.Width + "x" + clean.Height +
                          "  meanLuma=" + MeanLuma(clean).ToString("F1"));

        // ---------------------------------------------------------------
        // 1. Every tool must render something.
        // ---------------------------------------------------------------
        Console.WriteLine("\n--- tools render ---");
        Tool[] tools = new Tool[] { Tool.Pen, Tool.Line, Tool.Rectangle,
                                    Tool.Ellipse, Tool.Arrow, Tool.Mosaic,
                                    Tool.Spotlight };
        string[] names = new string[] { "Pen", "Line", "Rectangle", "Ellipse",
                                        "Arrow", "Mosaic", "Spotlight" };
        double[] diff = new double[tools.Length];

        for (int i = 0; i < tools.Length; i++)
        {
            ov.Clear();
            ov.SelectColor(0);
            ov.TestStrokeDrag(tools[i], from, to);
            Application.DoEvents();
            Bitmap got = ov.RenderComposite(false);
            diff[i] = Diff(clean, got);
            string recorded = ov.LastStrokeToolForTest();
            Console.WriteLine("  " + names[i].PadRight(10) +
                              " diff=" + (diff[i] * 100).ToString("F2") + "%" +
                              "  strokes=" + ov.StrokeCount +
                              "  recordedTool=" + recorded);
            Check(diff[i] > 0.0005,
                  names[i] + " renders a visible change",
                  "diff=" + (diff[i] * 100).ToString("F3") + "%");
            got.Dispose();
        }

        // Spotlight must actually darken the surroundings, not just draw a ring.
        ov.Clear();
        ov.TestStrokeDrag(Tool.Spotlight, from, to);
        Application.DoEvents();
        Bitmap spot = ov.RenderComposite(false);
        double dim = MeanLuma(spot) - MeanLuma(clean);
        Console.WriteLine("  spotlight delta luma = " + dim.ToString("F1"));
        Check(dim < -3.0, "spotlight darkens the area outside the ellipse",
              "dLuma=" + dim.ToString("F1"));
        spot.Dispose();

        // Mosaic must change local detail rather than repainting identical pixels.
        ov.Clear();
        ov.TestStrokeDrag(Tool.Mosaic, from, to);
        Application.DoEvents();
        Bitmap mos = ov.RenderComposite(false);
        Check(ov.StrokeCount == 1, "mosaic stroke was committed",
              "strokes=" + ov.StrokeCount);
        mos.Dispose();

        // ---------------------------------------------------------------
        // 2. Text tool: the editor must exist, and committed text must draw.
        // ---------------------------------------------------------------
        Console.WriteLine("\n--- text tool ---");
        ov.Clear();
        ov.TestBeginText(new PointF(700, 600));
        Application.DoEvents();
        Check(ov.IsEditingTextForTest(), "text editor opens",
              "IsEditing=" + ov.IsEditingTextForTest());

        ov.TestSetTextEditorContent("标注文字测试");
        Application.DoEvents();
        Check(ov.TextEditorContentForTest() == "标注文字测试",
              "editor accepts typed content",
              "'" + ov.TextEditorContentForTest() + "'");

        ov.CommitTextEditing(true);
        Application.DoEvents();
        Check(!ov.IsEditingTextForTest(), "editor closes after commit", "");
        Check(ov.StrokeCount == 1, "text became a stroke",
              "strokes=" + ov.StrokeCount);

        Bitmap txt = ov.RenderComposite(false);
        double td = Diff(clean, txt);
        Console.WriteLine("  text diff=" + (td * 100).ToString("F2") + "%");
        Check(td > 0.0002, "committed text is drawn", "diff=" + (td * 100).ToString("F3") + "%");
        txt.Dispose();

        // Empty text must not become a stroke.
        ov.Clear();
        ov.TestBeginText(new PointF(700, 640));
        Application.DoEvents();
        ov.CommitTextEditing(true);
        Application.DoEvents();
        Check(ov.StrokeCount == 0, "empty text is discarded",
              "strokes=" + ov.StrokeCount);

        // Cancelling with Escape must also discard.
        ov.Clear();
        ov.TestBeginText(new PointF(700, 660));
        ov.TestSetTextEditorContent("丢弃");
        Application.DoEvents();
        ov.CommitTextEditing(false);
        Application.DoEvents();
        Check(ov.StrokeCount == 0, "Escape discards the text",
              "strokes=" + ov.StrokeCount);

        // ---------------------------------------------------------------
        // 3. Modifier override rules.
        // ---------------------------------------------------------------
        Console.WriteLine("\n--- modifier overrides ---");
        ov.Clear();
        ov.SetTool(Tool.Pen);
        Check(ov.EffectiveToolForModifiersForTest(false, false, false) == "Pen",
              "no modifier -> Pen",
              ov.EffectiveToolForModifiersForTest(false, false, false));
        Check(ov.EffectiveToolForModifiersForTest(true, false, false) == "Rectangle",
              "Ctrl -> Rectangle",
              ov.EffectiveToolForModifiersForTest(true, false, false));
        Check(ov.EffectiveToolForModifiersForTest(false, false, true) == "Ellipse",
              "Tab -> Ellipse",
              ov.EffectiveToolForModifiersForTest(false, false, true));
        Check(ov.EffectiveToolForModifiersForTest(false, true, false) == "Line",
              "Shift -> Line",
              ov.EffectiveToolForModifiersForTest(false, true, false));
        Check(ov.EffectiveToolForModifiersForTest(true, true, false) == "Arrow",
              "Ctrl+Shift -> Arrow (beats Ctrl alone)",
              ov.EffectiveToolForModifiersForTest(true, true, false));
        Check(ov.EffectiveToolForModifiersForTest(true, true, true) == "Arrow",
              "Ctrl+Shift+Tab -> Arrow (arrow wins)",
              ov.EffectiveToolForModifiersForTest(true, true, true));

        // With the overrides disabled the base tool must survive.
        Settings off = new Settings();
        off.ModCtrlRectangle = false;
        off.ModTabEllipse = false;
        off.ModShiftLine = false;
        off.ModCtrlShiftArrow = false;
        OverlayForm ov2 = new OverlayForm(off, delegate { return new Size(32, 32); });
        ov2.ActivateOverlay(false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);
        ov2.SetTool(Tool.Pen);
        Check(ov2.EffectiveToolForModifiersForTest(true, true, true) == "Pen",
              "overrides disabled -> Pen is kept",
              ov2.EffectiveToolForModifiersForTest(true, true, true));
        ov2.DeactivateOverlay();

        // ---------------------------------------------------------------
        // 4. A stroke must be recorded with the tool the hook selected.
        // ---------------------------------------------------------------
        Console.WriteLine("\n--- stroke bookkeeping ---");
        ov.Clear();
        ov.TestStrokeDrag(Tool.Rectangle, from, to);
        Check(ov.StrokeCount == 1 && ov.LastStrokeToolForTest() == "Rectangle",
              "drag records the intended tool",
              ov.LastStrokeToolForTest() + " strokes=" + ov.StrokeCount);
        Check(ov.LastStrokePointCountForTest() == 2,
              "shape tools keep only the two drag corners",
              "points=" + ov.LastStrokePointCountForTest());

        ov.TestStrokeDrag(Tool.Pen, from, to);
        Check(ov.LastStrokePointCountForTest() > 2,
              "pen keeps the full freehand path",
              "points=" + ov.LastStrokePointCountForTest());

        ov.Clear();
        Check(ov.StrokeCount == 0, "clear removes every stroke",
              "strokes=" + ov.StrokeCount);

        ov.DeactivateOverlay();
        clean.Dispose();

        Console.WriteLine();
        if (fails == 0)
        {
            Console.WriteLine("ALL TOOL CHECKS PASSED");
            return 0;
        }
        Console.WriteLine(fails + " TOOL CHECK(S) FAILED");
        return 1;
    }
}
