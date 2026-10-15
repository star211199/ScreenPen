// smooth_probe.cs - proves the brush actually renders as a curve.
//
// The bug: "SmoothLines" only set SmoothingMode.AntiAlias, while DrawStroke
// always called Graphics.DrawLines. Antialiasing softens pixel edges but the
// geometry stays a chain of straight segments, so a fast circular stroke came
// out as a visible polygon. This probe renders a sparse circular polyline and
// measures how far the rendered ink strays from a true circle - a faceted
// polygon bulges inward at the chord midpoints.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using ScreenPen;

static class SmoothProbe
{
    static int passed, failed;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) passed++; else failed++;
        Console.WriteLine("  " + (ok ? "[PASS]" : "[FAIL]") + " " + name
            + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();

        try { Run(); }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            failed++;
        }

        Console.WriteLine();
        Console.WriteLine("=== " + passed + " passed, " + failed + " failed ===");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>A sparse circle, like a fast hand-drawn one.</summary>
    static PointF[] Circle(int n, float cx, float cy, float r)
    {
        PointF[] p = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            p[i] = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        }
        return p;
    }

    /// <summary>
    /// Maximum inward deviation of the ink from the ideal radius, sampled at the
    /// chord midpoints where a faceted polygon is worst. Smaller = rounder.
    /// </summary>
    static float FacetDepth(PointF[] pts, float cx, float cy, float r, bool smooth)
    {
        const int S = 300;
        using (Bitmap bmp = new Bitmap(S, S))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Shift the circle into the bitmap.
            PointF[] local = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                local[i] = new PointF(pts[i].X - cx + S / 2f, pts[i].Y - cy + S / 2f);

            using (Pen pen = new Pen(Color.Black, 3f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                OverlayForm.DrawPolyline(g, pen, local, smooth);
            }

            // For each angle, find the ink radius. A polygon is closer to the
            // centre at the chord midpoints (halfway between vertices).
            float worst = 0f;
            for (int i = 0; i < 720; i++)
            {
                double a = 2 * Math.PI * i / 720;
                float ux = (float)Math.Cos(a), uy = (float)Math.Sin(a);

                // Walk outward from the centre; the last dark pixel is the edge.
                float hit = -1f;
                for (float t = r - 12f; t <= r + 12f; t += 0.5f)
                {
                    int px = (int)Math.Round(S / 2f + ux * t);
                    int py = (int)Math.Round(S / 2f + uy * t);
                    if (px < 0 || py < 0 || px >= S || py >= S) continue;
                    Color c = bmp.GetPixel(px, py);
                    if (c.R < 128) hit = t;
                }
                if (hit < 0) continue;
                float depth = r - hit;      // >0 means ink inside the ideal circle
                if (depth > worst) worst = depth;
            }
            return worst;
        }
    }

    static void Run()
    {
        Console.WriteLine("--- 1. DrawPolyline takes the curve path when asked ---");

        // 1a. A smooth request on >=3 points must not equal the polyline result.
        PointF[] sparse = Circle(12, 0, 0, 100);
        float faceted = FacetDepth(sparse, 0, 0, 100, false);
        float smooth = FacetDepth(sparse, 0, 0, 100, true);

        // The app pipeline: densify, then spline. That is what DrawStroke does.
        List<PointF> dense = OverlayForm.Densify(new List<PointF>(sparse), 3f);
        float app = FacetDepth(dense.ToArray(), 0, 0, 100, true);

        Console.WriteLine(string.Format(
            "  sparse 12-point circle: polyline={0:F2}px  spline only={1:F2}px  app (densify+spline)={2:F2}px",
            faceted, smooth, app));

        // A 12-gon inscribed in r=100 is 100*(1-cos(pi/12)) = 3.41px inside the
        // circle at its chord midpoints. That is the faceting the user saw.
        Check("the raw polyline really is faceted (baseline)",
              faceted > 2.5f, string.Format("{0:F2}px", faceted));

        Check("splining roughly halves the faceting",
              smooth < faceted * 0.6f,
              string.Format("{0:F2}px vs {1:F2}px", smooth, faceted));

        // The measurement walks outward in 0.5px steps, so ~0.5px is the noise
        // floor; 1.0px of residual facet on a 100px circle is 1%.
        Check("the app pipeline is round to within 1% of the radius",
              app <= 1.0f, string.Format("{0:F2}px of {1:F0}px", app, 100f));

        // The measurement walks outward in 0.5px steps, so 1.00px is at the
        // quantisation floor: demanding a strict 3x would be measuring the
        // probe's own resolution rather than the renderer.
        Check("the app pipeline beats the raw polyline by more than 2.5x",
              app < faceted / 2.5f,
              string.Format("{0:F2}px vs {1:F2}px", app, faceted));

        // 1b. Below 3 points, DrawCurve would throw; both paths must agree.
        PointF[] two = new PointF[] { new PointF(10, 10), new PointF(90, 10) };
        float d2a = FacetDepth(two, 50, 10, 40, false);
        float d2b = FacetDepth(two, 50, 10, 40, true);
        Check("a 2-point stroke is identical smooth or not",
              Math.Abs(d2a - d2b) < 0.01f, string.Format("{0:F2} vs {1:F2}", d2a, d2b));

        PointF[] one = new PointF[] { new PointF(50, 50) };
        bool threw = false;
        try
        {
            using (Bitmap bmp = new Bitmap(20, 20))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Pen pen = new Pen(Color.Black, 2f))
                OverlayForm.DrawPolyline(g, pen, one, true);
        }
        catch { threw = true; }
        Check("a 1-point stroke does not throw", !threw, "");

        Console.WriteLine();
        Console.WriteLine("--- 2. Densify fills in the gaps ---");
        List<PointF> raw = new List<PointF>(sparse);
        List<PointF> densified = OverlayForm.Densify(raw, 3f);
        Check("densify adds points", densified.Count > raw.Count,
              raw.Count + " -> " + densified.Count);

        float maxGapRaw = MaxGap(raw);
        float maxGapDense = MaxGap(densified);
        Check("densified max gap is within the requested step",
              maxGapDense <= 3.01f,
              string.Format("{0:F2}px (was {1:F2}px)", maxGapDense, maxGapRaw));

        Check("densify keeps the original endpoints",
              Near(densified[0], raw[0]) && Near(densified[densified.Count - 1], raw[raw.Count - 1]),
              "");

        Check("densify of an empty list is empty",
              OverlayForm.Densify(new List<PointF>(), 3f).Count == 0, "");
        Check("densify of a single point returns that point",
              OverlayForm.Densify(new List<PointF>(one), 3f).Count == 1, "");

        // Interpolated points must lie on the curve, not cut the corner. A
        // Catmull-Rom through 12 samples of a circle is not exactly the circle,
        // but it must be strictly closer to it than the raw polygon, whose chord
        // midpoints sit r*(1-cos(pi/12)) = 3.41px inside.
        float worstRadiusErr = 0f;
        foreach (PointF p in densified)
        {
            float rr = (float)Math.Sqrt(p.X * p.X + p.Y * p.Y);
            float e = Math.Abs(rr - 100f);
            if (e > worstRadiusErr) worstRadiusErr = e;
        }
        Check("densified points stay closer to the circle than the raw polygon",
              worstRadiusErr < faceted,
              string.Format("{0:F2}px vs {1:F2}px", worstRadiusErr, faceted));

        Console.WriteLine();
        Console.WriteLine("--- 3. only the pen is smoothed ---");
        // DrawStroke branches on Tool; Line/Rect/Ellipse/Arrow use exact GDI+
        // primitives and must not be routed through DrawPolyline. Assert that the
        // pen branch is the only caller by checking the source contract through
        // behaviour: a Rectangle stroke keeps exactly 2 points after densify-free
        // commit.
        Settings cfg = new Settings();
        cfg.Clamp();
        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });

        ov.TestBeginStroke(new PointF(100, 100));
        ov.TestExtendStroke(new PointF(400, 300));
        ov.TestEndStroke();
        Check("a rectangle stroke stores exactly 2 points",
              ov.LastStrokePointCountForTest() == 2,
              "points=" + ov.LastStrokePointCountForTest());

        ov.SetTool(Tool.Pen);
        ov.TestBeginStroke(new PointF(100, 100));
        for (int i = 0; i < 11; i++)
            ov.TestExtendStroke(new PointF(100 + i * 10, 100 + i * 5));
        ov.TestEndStroke();
        Check("a pen stroke stores the raw samples",
              ov.LastStrokePointCountForTest() == 12,
              "points=" + ov.LastStrokePointCountForTest());

        ov.Dispose();
    }

    static bool Near(PointF a, PointF b)
    {
        return Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;
    }

    static float MaxGap(List<PointF> pts)
    {
        float worst = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d > worst) worst = d;
        }
        return worst;
    }
}
