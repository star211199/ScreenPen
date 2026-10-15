// Exercises the region screenshot selector directly: crop correctness, the
// copy-to-clipboard path, and the guarantee that selector chrome (dimming,
// toolbar, border) can never leak into the saved pixels.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ScreenPen;

class RegionProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    static readonly Color Chrome = Color.FromArgb(255, 232, 160, 0);

    /// <summary>Builds a backdrop of distinct solid colour blocks.</summary>
    static Bitmap MakeBackdrop(int w, int h)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(255, 10, 20, 30));
            // Four quadrants, each a unique flat colour, so a crop can be
            // located unambiguously by inspecting its pixels.
            g.FillRectangle(Brushes.Red, 0, 0, w / 2, h / 2);
            g.FillRectangle(Brushes.Lime, w / 2, 0, w - w / 2, h / 2);
            g.FillRectangle(Brushes.Blue, 0, h / 2, w / 2, h - h / 2);
            g.FillRectangle(Brushes.Yellow, w / 2, h / 2, w - w / 2, h - h / 2);
        }
        return b;
    }

    static bool HasChromeColour(Bitmap b)
    {
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                Color c = b.GetPixel(x, y);
                if (c.R == Chrome.R && c.G == Chrome.G && c.B == Chrome.B) return true;
            }
        return false;
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        Settings cfg = new Settings();

        const int W = 800, H = 600;
        Bitmap backdrop = MakeBackdrop(W, H);
        Rectangle desktop = new Rectangle(0, 0, W, H);

        RegionSelectForm f = new RegionSelectForm(cfg, desktop, backdrop);
        f.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(250);
        Application.DoEvents();

        Console.WriteLine("selector bounds = " + f.Width + "x" + f.Height);

        // ------------------------------------------------------------------
        // 1. Nothing selected yet.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- selection state ---");
        Check(!f.HasSelectionForTest, "no selection before dragging", "");
        Check(f.ActionTaken == RegionAction.Cancelled,
              "action defaults to Cancelled", f.ActionTaken.ToString());

        // A zero-area drag must not count as a selection.
        f.SimulateDrag(new Point(100, 100), new Point(100, 100));
        Check(!f.HasSelectionForTest, "a zero-area drag is not a selection",
              "hasSelection=" + f.HasSelectionForTest);

        // ------------------------------------------------------------------
        // 2. A real drag produces a normalised rectangle.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- drag normalisation ---");
        f.SimulateDrag(new Point(300, 400), new Point(100, 200));
        Rectangle sel = f.SelectionForTest;
        Console.WriteLine("  dragged (300,400)->(100,200)  selection=" + sel);
        Check(sel.X == 100 && sel.Y == 200 && sel.Width == 200 && sel.Height == 200,
              "bottom-right drag normalises to top-left origin", sel.ToString());

        // ------------------------------------------------------------------
        // 3. The crop must match the dragged region exactly.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- crop correctness ---");
        f.SimulateDrag(new Point(150, 120), new Point(450, 380));
        sel = f.SelectionForTest;
        // Mirror the production flow: the action is committed first, and the
        // crop is then read back from the (still-alive) form.
        f.SimulateFinish(RegionAction.SaveToFile);
        Application.DoEvents();
        Bitmap crop = f.CropSelection();
        Check(crop != null, "crop produced a bitmap", crop == null ? "null" : "");
        if (crop != null)
        {
            Console.WriteLine("  selection=" + sel + "  crop=" + crop.Width + "x" + crop.Height);
            Check(crop.Width == sel.Width && crop.Height == sel.Height,
                  "crop size equals the selection size",
                  crop.Width + "x" + crop.Height + " vs " + sel.Width + "x" + sel.Height);

            // Top-left of the crop is inside the red quadrant, and the pixel at
            // the same offset in the backdrop must match it exactly.
            Color a = crop.GetPixel(0, 0);
            Color b = backdrop.GetPixel(sel.X, sel.Y);
            Check(a.ToArgb() == b.ToArgb(),
                  "crop pixel (0,0) matches the backdrop at the selection origin",
                  a.Name + " vs " + b.Name);

            Color mid = crop.GetPixel(crop.Width / 2, crop.Height / 2);
            Color midRef = backdrop.GetPixel(sel.X + crop.Width / 2,
                                             sel.Y + crop.Height / 2);
            Check(mid.ToArgb() == midRef.ToArgb(),
                  "crop centre matches the backdrop",
                  mid.Name + " vs " + midRef.Name);

            // The selector draws dimming and a border over its own window; none
            // of that may appear in the crop.
            Check(!HasChromeColour(crop),
                  "selector chrome (accent colour) never leaks into the crop", "");

            // A crop spanning two quadrants must contain both colours, proving
            // it is real image data and not a flat fill.
            f.SimulateDrag(new Point(100, 100), new Point(700, 500));
            f.SimulateFinish(RegionAction.SaveToFile);
            Application.DoEvents();
            Bitmap big = f.CropSelection();
            if (big != null)
            {
                bool red = false, lime = false, blue = false, yellow = false;
                for (int y = 0; y < big.Height; y += 7)
                    for (int x = 0; x < big.Width; x += 7)
                    {
                        Color c = big.GetPixel(x, y);
                        if (c.R > 200 && c.G < 60 && c.B < 60) red = true;
                        else if (c.G > 200 && c.R < 60 && c.B < 60) lime = true;
                        else if (c.B > 200 && c.R < 60 && c.G < 60) blue = true;
                        else if (c.R > 200 && c.G > 200 && c.B < 60) yellow = true;
                    }
                Check(red && lime && blue && yellow,
                      "a full-square crop contains all four quadrants",
                      string.Format("red={0} lime={1} blue={2} yellow={3}",
                                    red, lime, blue, yellow));
                big.Dispose();
            }
            crop.Dispose();
        }

        // ------------------------------------------------------------------
        // 4. Toolbar buttons must be inside the window and not overlap.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- toolbar layout ---");
        Rectangle bs = f.SaveButtonBounds, bc = f.CopyButtonBounds;
        Console.WriteLine("  save=" + bs + "  copy=" + bc);
        Check(bs.Width > 0 && bs.Height > 0, "save button has a real size", bs.ToString());
        Check(bc.Width > 0 && bc.Height > 0, "copy button has a real size", bc.ToString());
        Check(!bs.IntersectsWith(bc), "save and copy buttons do not overlap", "");
        Rectangle client = new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height);
        Check(client.Contains(bs) && client.Contains(bc),
              "both buttons are fully inside the window", "");

        // ------------------------------------------------------------------
        // 5. Copy path: the form must yield a cropped bitmap that can be put on
        //    the clipboard (AppContext2 performs the Clipboard.SetImage call, so
        //    this proves the part the form is responsible for).
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- clipboard ---");
        f.SimulateDrag(new Point(200, 150), new Point(500, 400));
        f.SimulateFinish(RegionAction.CopyToClipboard);
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);

        bool cbOk = false;
        string cbDetail = "";
        try
        {
            Bitmap toCopy = f.CropSelection();
            if (toCopy == null)
            {
                cbDetail = "CropSelection returned null";
            }
            else
            {
                // Same construction AppContext2 uses before Clipboard.SetImage.
                using (Bitmap copy = new Bitmap(toCopy.Width, toCopy.Height,
                                               PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(toCopy, 0, 0);
                    Clipboard.SetImage(copy);
                }
                toCopy.Dispose();

                IDataObject d = Clipboard.GetDataObject();
                if (d != null && d.GetDataPresent(DataFormats.Bitmap))
                {
                    Bitmap got = d.GetData(DataFormats.Bitmap) as Bitmap;
                    if (got != null)
                    {
                        cbOk = true;
                        cbDetail = got.Width + "x" + got.Height;
                    }
                    else cbDetail = "clipboard data is not a Bitmap";
                }
                else cbDetail = "no bitmap format on the clipboard";
            }
        }
        catch (Exception ex) { cbDetail = ex.GetType().Name + ": " + ex.Message; }

        Check(cbOk, "region can be copied to the clipboard as a bitmap", cbDetail);
        Check(f.ActionTaken == RegionAction.CopyToClipboard,
              "action recorded as CopyToClipboard", f.ActionTaken.ToString());

        // ------------------------------------------------------------------
        // 6. Save path reports the correct region.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- save path ---");
        RegionSelectForm f2 = new RegionSelectForm(cfg, desktop, MakeBackdrop(W, H));
        f2.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Application.DoEvents();
        f2.SimulateDrag(new Point(120, 90), new Point(420, 330));
        Rectangle want = f2.SelectionForTest;
        f2.SimulateFinish(RegionAction.SaveToFile);
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Application.DoEvents();
        Check(f2.ActionTaken == RegionAction.SaveToFile,
              "action recorded as SaveToFile", f2.ActionTaken.ToString());
        Check(f2.ChosenScreenRect.Width == want.Width &&
              f2.ChosenScreenRect.Height == want.Height,
              "chosen region matches the drag",
              f2.ChosenScreenRect + " vs " + want);

        // ------------------------------------------------------------------
        // 7. Cancel must leave nothing behind.
        // ------------------------------------------------------------------
        Console.WriteLine("\n--- cancel path ---");
        RegionSelectForm f3 = new RegionSelectForm(cfg, desktop, MakeBackdrop(W, H));
        f3.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Application.DoEvents();
        f3.SimulateDrag(new Point(50, 50), new Point(250, 250));
        f3.SimulateFinish(RegionAction.Cancelled);
        Application.DoEvents();
        System.Threading.Thread.Sleep(200);
        Check(f3.ActionTaken == RegionAction.Cancelled,
              "cancel keeps the Cancelled action", f3.ActionTaken.ToString());

        try { f.Close(); } catch { }
        try { f2.Close(); } catch { }
        try { f3.Close(); } catch { }
        backdrop.Dispose();

        Console.WriteLine();
        if (fails == 0) { Console.WriteLine("ALL REGION CHECKS PASSED"); return 0; }
        Console.WriteLine(fails + " REGION CHECK(S) FAILED");
        return 1;
    }
}
