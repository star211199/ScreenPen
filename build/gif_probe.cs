// Feasibility probe: can a plain .NET Framework 4.0 csc.exe build write an
// ANIMATED GIF with no NuGet package and no bundled ffmpeg?
//
// Rationale: "record a region to an animated GIF" is one of the headline
// features of ShareX / ScreenToGif. Bundling ffmpeg would break ScreenPen's
// "single green exe, zero dependencies" promise (and add ~70 MB + licensing).
// But .NET Framework ships WPF's System.Windows.Media.Imaging.GifBitmapEncoder
// in PresentationCore.dll, which can build multi-frame GIFs natively.
//
// The catch this probe checks: GifBitmapEncoder merges frames into one metadata
// block, and delay times need a BitmapMetadata query string. Also, if the two
// frames are identical the encoder may collapse them, so we draw visibly
// different frames and verify the byte size grows and the frame count is 2.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

static class GifProbe
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    static Bitmap Frame(int i)
    {
        Bitmap bmp = new Bitmap(240, 120);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (Font f = new Font("Segoe UI", 22f))
                g.DrawString("frame " + i, f, Brushes.Black, new PointF(10, 10));
            g.FillRectangle(Brushes.Red, 10 + i * 60, 70, 50, 30);
        }
        return bmp;
    }

    static byte[] PngBytes(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static void Main()
    {
        Console.WriteLine("=== ANIMATED GIF FEASIBILITY PROBE (no ffmpeg, no NuGet) ===");
        Console.WriteLine("runtime = " + Environment.Version);
        Console.WriteLine();

        // ---- 1. is the WPF encoder reachable at all? -------------------------
        GifBitmapEncoder enc = null;
        try { enc = new GifBitmapEncoder(); }
        catch (Exception ex) { Check(false, "instantiate GifBitmapEncoder", ex.Message); }
        Check(enc != null, "System.Windows.Media.Imaging.GifBitmapEncoder available",
              enc == null ? "null" : "ok");
        if (enc == null) { Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ==="); return; }

        // ---- 2. add frames with per-frame delay ------------------------------
        const int DelayMs = 120;
        int framesAdded = 0;
        try
        {
            for (int i = 0; i < 2; i++)
            {
                using (Bitmap bmp = Frame(i))
                {
                    byte[] png = PngBytes(bmp);
                    BitmapFrame bf;
                    using (MemoryStream ms = new MemoryStream(png))
                    {
                        BitmapDecoder dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        bf = dec.Frames[0];
                    }
                    // GifBitmapEncoder coalesces per-frame metadata into a single
                    // block; the delay lives in the /grctlext/Delay query.
                    BitmapMetadata md = new BitmapMetadata("gif");
                    md.SetQuery("/grctlext/Delay", (ushort)(DelayMs / 10)); // GIF delay is in 1/100 s
                    enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
                    framesAdded++;
                }
            }
        }
        catch (Exception ex) { Check(false, "add frames with metadata", ex.Message); }
        Check(framesAdded == 2, "added 2 frames to the encoder", "added=" + framesAdded);

        // ---- 3. save it ------------------------------------------------------
        string outPath = Path.Combine(Path.GetTempPath(), "screenpen_gif_probe.gif");
        long size = 0;
        try
        {
            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
            size = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { Check(false, "save GIF", ex.Message); }
        Check(size > 0, "wrote a GIF file", size + " bytes");

        // ---- 4. read it back and count frames --------------------------------
        int frameCount = 0;
        int width = 0, height = 0;
        try
        {
            using (FileStream fs = new FileStream(outPath, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frameCount = dec.Frames.Count;
                width = dec.Frames[0].PixelWidth;
                height = dec.Frames[0].PixelHeight;
            }
        }
        catch (Exception ex) { Check(false, "re-read GIF", ex.Message); }
        Check(frameCount >= 2, "GIF contains multiple frames (is animated)", "frames=" + frameCount);
        Check(width == 240 && height == 120, "GIF frame size preserved", width + "x" + height);

        // ---- 5. is the on-disk header really GIF89a (animation-capable)? -----
        byte[] head = new byte[6];
        try
        {
            using (FileStream fs = new FileStream(outPath, FileMode.Open, FileAccess.Read))
                fs.Read(head, 0, 6);
        }
        catch { }
        string sig = System.Text.Encoding.ASCII.GetString(head);
        Check(sig == "GIF89a", "file header is GIF89a", "got \"" + sig + "\"");

        // ---- 6. how big is a realistic capture? ------------------------------
        // 800x600 x 30 frames of screen content, to see if this is viable at all
        // for real screen recording (not just a 2-frame toy).
        long real = 0;
        try
        {
            GifBitmapEncoder big = new GifBitmapEncoder();
            for (int i = 0; i < 30; i++)
            {
                using (Bitmap bmp = new Bitmap(800, 600))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(255, 240, 240, 240));
                        using (Font f = new Font("Segoe UI", 18f))
                            g.DrawString("t=" + i, f, Brushes.Black, new PointF(20 + i * 8, 20));
                        g.FillRectangle(Brushes.SteelBlue, 100 + i * 15, 200, 120, 90);
                    }
                    byte[] png = PngBytes(bmp);
                    BitmapFrame bf;
                    using (MemoryStream ms = new MemoryStream(png))
                        bf = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    BitmapMetadata md = new BitmapMetadata("gif");
                    md.SetQuery("/grctlext/Delay", (ushort)10);
                    big.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
                }
            }
            using (FileStream fs = new FileStream(outPath + ".big.gif", FileMode.Create))
                big.Save(fs);
            real = new FileInfo(outPath + ".big.gif").Length;
        }
        catch (Exception ex) { Check(false, "30-frame 800x600 GIF", ex.Message); }
        Check(real > 0, "produced a 30-frame 800x600 GIF", (real / 1024) + " KB (~" + (real / 1024 / 30) + " KB/frame)");

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
        try { File.Delete(outPath); } catch { }
        try { File.Delete(outPath + ".big.gif"); } catch { }
    }
}
