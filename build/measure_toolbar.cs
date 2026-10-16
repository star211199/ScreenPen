// Asserts that every region-selector toolbar label actually fits its button at
// the real system DPI, in all three default-action states.
//
// Why this exists: the original toolbar hard-coded 84x32 px buttons while the
// label font renders ~2x at 200% scaling, so "复制 (Ctrl+C)" (which needs 153 px
// at 192 DPI) was silently clipped. A geometry probe that only checked
// "no overlap" and "inside the window" passed the whole time. This measures the
// text.
//
// Usage: measure_toolbar.exe
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class MeasureToolbar
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int i);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    static int fails;

    /// <summary>
    /// Index of the button the current default action commits, i.e. the one drawn
    /// bold. Mirrors RegionSelectForm.PrimaryIndex, which is private.
    /// </summary>
    static int PrimaryIndexFor(RegionAction act)
    {
        if (act == RegionAction.CopyToClipboard) return 1;
        if (act == RegionAction.PinToDesktop) return 2;
        if (act == RegionAction.Ocr) return 3;
        if (act == RegionAction.RecordGif) return 4;
        return 0;
    }

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what
            + (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    [STAThread]
    static int Main()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { }

        IntPtr dc = GetDC(IntPtr.Zero);
        int dpi = GetDeviceCaps(dc, 90);
        ReleaseDC(IntPtr.Zero, dc);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine("system DPI = " + dpi + " (scale " + (dpi / 96.0) + ")");
        Console.WriteLine();

        Settings cfg = new Settings();
        cfg.Clamp();

        // Each RegionAction so that every branch of SaveLabel/CopyLabel/PinLabel/
        // OcrLabel/GifLabel is exercised: the " (Enter)" form is the widest of
        // each pair, and it is the one that would clip first.
        RegionAction[] defaults = new RegionAction[] {
            RegionAction.SaveToFile, RegionAction.CopyToClipboard,
            RegionAction.PinToDesktop, RegionAction.Ocr, RegionAction.RecordGif
        };

        // Desktop widths to try, including one narrow enough to force shrink.
        int[][] sizes = new int[][] {
            new int[] { 2880, 1800 }, new int[] { 1920, 1080 },
            new int[] { 1280, 800 },  new int[] { 900, 700 },
            new int[] { 640, 480 },
        };

        foreach (int[] wh in sizes)
        {
            foreach (RegionAction act in defaults)
            {
                RegionSelectForm sel = new RegionSelectForm(
                    cfg, new Rectangle(0, 0, wh[0], wh[1]),
                    new Bitmap(wh[0], wh[1]), act);
                sel.StartPosition = FormStartPosition.Manual;
                sel.Location = new Point(-7000, -7000);
                sel.Show();
                Application.DoEvents();

                // Select most of the screen so the toolbar has its natural place.
                sel.SimulateDrag(new Point(wh[0] / 10, wh[1] / 10),
                                 new Point(wh[0] * 9 / 10, wh[1] * 9 / 10));
                Application.DoEvents();

                string[] labels = sel.ToolbarLabelsForTest;
                int n = sel.ToolbarButtonCountForTest;
                Rectangle[] rects = new Rectangle[n];
                for (int i = 0; i < n; i++) rects[i] = sel.ToolbarButtonBoundsForTest(i);

                Check(labels != null && labels.Length == n && n == 6,
                      wh[0] + "px " + act + ": the toolbar recorded all six labels",
                      labels == null ? "null" : labels.Length + " label(s), " + n + " button(s)");

                // The labels must fit whenever the row was laid out at its
                // natural width. On a genuinely small desktop LayoutToolbar
                // shrinks/shortens the row on purpose (the documented fallback,
                // with clipping as the backstop), so fit is only required when
                // the form itself did not have to shrink.
                bool roomy = !sel.ToolbarShrunkForTest;

                // The primary button - the one Enter commits - is drawn bold.
                int primary = PrimaryIndexFor(act);

                for (int i = 0; i < n && i < labels.Length; i++)
                {
                    using (Font f = new Font("Microsoft YaHei UI", 8.6f,
                                 i == primary ? FontStyle.Bold : FontStyle.Regular))
                    {
                        Size need = TextRenderer.MeasureText(labels[i], f,
                            new Size(int.MaxValue, int.MaxValue),
                            TextFormatFlags.NoPadding);
                        Check(!roomy || need.Width <= rects[i].Width,
                            wh[0] + "px " + act + ": label '" + labels[i] + "' fits",
                            "needs " + need.Width + "px, button " + rects[i].Width + "px");
                    }
                    Check(rects[i].Width > 0 && rects[i].Height > 0,
                        wh[0] + "px " + act + ": button " + i + " has a real size",
                        rects[i].ToString());
                }

                Rectangle client = new Rectangle(0, 0, sel.ClientSize.Width,
                                                 sel.ClientSize.Height);
                bool allInside = true, overlap = false;
                for (int i = 0; i < n; i++)
                {
                    if (!client.Contains(rects[i])) allInside = false;
                    for (int j = i + 1; j < n; j++)
                        if (rects[i].IntersectsWith(rects[j])) overlap = true;
                }
                Check(allInside, wh[0] + "px " + act + ": all " + n + " buttons inside the window", "");
                Check(!overlap, wh[0] + "px " + act + ": no two buttons overlap", "");

                // Regression guard for the reported defect: a desktop of 1280 px
                // or wider at 200% must never need to shrink the row, otherwise
                // the labels are being ellipsised on an ordinary machine.
                if (wh[0] >= 1280)
                    Check(!sel.ToolbarShrunkForTest,
                        wh[0] + "px " + act + ": a normal desktop never shrinks the toolbar",
                        "shrunk=" + sel.ToolbarShrunkForTest);

                // The painted toolbar must sit inside the window.
                Check(sel.ClientSize.Width > 0 && sel.ClientSize.Height > 0,
                      wh[0] + "px " + act + ": the window has a real client size",
                      sel.ClientSize.ToString());

                sel.Close();
                sel.Dispose();
            }
        }

        Console.WriteLine();
        if (fails == 0)
        {
            Console.WriteLine("=== ALL TOOLBAR MEASUREMENTS PASSED ===");
            return 0;
        }
        Console.WriteLine("=== " + fails + " TOOLBAR MEASUREMENT(S) FAILED ===");
        return 1;
    }
}
