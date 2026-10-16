// Probe: does GifBitmapEncoder actually preserve PER-FRAME delays, and does the
// output loop forever?
//
// Why this matters before writing any recorder code: the cheapest way to keep a
// screen recording small is to collapse runs of identical frames into one frame
// with a longer delay. That requires variable per-frame delays. A comment in
// gif_probe.cs warns that GifBitmapEncoder "coalesces per-frame metadata into a
// single block", which if true would make variable delays impossible and force a
// constant frame rate with duplicated frames.
//
// This probe settles it by writing three frames with three DIFFERENT delays and
// reading them back, then scanning the raw bytes for the NETSCAPE2.0 loop block.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

static class GifDelayProbe
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    static Bitmap Frame(int i)
    {
        Bitmap bmp = new Bitmap(200, 100);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (Font f = new Font("Segoe UI", 20f))
                g.DrawString("f" + i, f, Brushes.Black, new PointF(8, 8));
            g.FillRectangle(Brushes.Blue, 10 + i * 40, 60, 30, 25);
        }
        return bmp;
    }

    static void Main()
    {
        Console.WriteLine("=== GIF PER-FRAME DELAY PROBE ===");
        Console.WriteLine();

        // Deliberately distinct and NOT multiples of each other, so a coalesced
        // value cannot accidentally match one of them.
        int[] want = new int[] { 5, 40, 15 };   // GIF units = 1/100 s -> 50/400/150 ms

        string path = Path.Combine(Path.GetTempPath(), "screenpen_gif_delay.gif");
        try
        {
            GifBitmapEncoder enc = new GifBitmapEncoder();
            for (int i = 0; i < want.Length; i++)
            {
                using (Bitmap bmp = Frame(i))
                using (MemoryStream png = new MemoryStream())
                {
                    bmp.Save(png, ImageFormat.Png);
                    png.Position = 0;
                    BitmapDecoder d = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame bf = d.Frames[0];
                    BitmapMetadata md = new BitmapMetadata("gif");
                    md.SetQuery("/grctlext/Delay", (ushort)want[i]);
                    enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
                }
            }
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
        }
        catch (Exception ex)
        {
            Check(false, "write 3-frame GIF with distinct delays", ex.Message);
            Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
            return;
        }

        // ---- read the delays back -------------------------------------------
        int[] got = null;
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                got = new int[dec.Frames.Count];
                for (int i = 0; i < dec.Frames.Count; i++)
                {
                    BitmapMetadata md = dec.Frames[i].Metadata as BitmapMetadata;
                    object v = md == null ? null : md.GetQuery("/grctlext/Delay");
                    got[i] = v == null ? -1 : Convert.ToInt32(v);
                }
            }
        }
        catch (Exception ex) { Check(false, "read delays back", ex.Message); }

        if (got != null)
        {
            Console.WriteLine("  wrote delays : " + string.Join(", ", Array.ConvertAll(want, x => x.ToString())));
            Console.WriteLine("  read  delays : " + string.Join(", ", Array.ConvertAll(got, x => x.ToString())));
            Console.WriteLine();

            bool preserved = got.Length == want.Length;
            if (preserved)
                for (int i = 0; i < want.Length; i++)
                    if (got[i] != want[i]) preserved = false;

            Check(preserved, "per-frame delays survive the encoder round-trip",
                  preserved ? "exact match" : "MISMATCH -> variable delays unusable");
        }

        // ---- does it loop forever? ------------------------------------------
        // The loop is an Application Extension block holding "NETSCAPE2.0".
        bool netscape = false;
        try
        {
            byte[] all = File.ReadAllBytes(path);
            string ascii = System.Text.Encoding.ASCII.GetString(all);
            netscape = ascii.Contains("NETSCAPE2.0");
        }
        catch { }
        Check(netscape, "output contains a NETSCAPE2.0 loop block",
              netscape ? "loops forever" : "NO LOOP BLOCK -> plays once and stops");

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
        try { File.Delete(path); } catch { }
    }
}
