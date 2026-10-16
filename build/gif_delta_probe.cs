// Decisive test for the GIF pipeline design.
//
// gif_dump.cs showed GifBitmapEncoder writes one GCE per frame with delay=0,
// transparent=True, tidx=2, and no NETSCAPE2.0 loop block. Two things must be
// settled before choosing an implementation:
//
//   Q1. Does the encoder use INTER-FRAME TRANSPARENCY (delta compression)?
//       If frame N encodes unchanged pixels as the transparent index, then the
//       frames are not self-contained and any post-processing that clears the
//       transparency flag (or re-quantises) would corrupt them.
//
//   Q2. Can per-frame delays be patched into the encoder's output afterwards?
//       The delay is a fixed 2-byte field at GCE+4, so patching is possible IF
//       the block structure is walkable - which gif_dump.cs proved it is.
//
// Method for Q1: make frame1 identical to frame0 except for one small patch. If
// the encoder deltas, frame1's transparent pixels cover the unchanged area.
// We then decode frame1 with the transparency flag LEFT AS-IS and confirm it
// still composites to the correct image, and separately report how many pixels
// frame1 actually stores (a tiny stored region => delta compression is in use).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

static class GifDeltaTest
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    // frame 0: white canvas + red square on the left
    // frame 1: same, plus a blue square on the right (left side unchanged)
    static Bitmap Frame(int i)
    {
        Bitmap b = new Bitmap(120, 80);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Red, 8, 8, 30, 30);
            if (i == 1) g.FillRectangle(Brushes.Blue, 80, 40, 30, 30);
        }
        return b;
    }

    static void Add(GifBitmapEncoder enc, Bitmap bmp, int delay)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            BitmapDecoder d = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame bf = d.Frames[0];
            BitmapMetadata md = new BitmapMetadata("gif");
            md.SetQuery("/grctlext/Delay", (ushort)delay);
            enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
        }
    }

    static void Main()
    {
        Console.WriteLine("=== GIF DELTA / PATCHABILITY TEST ===");
        Console.WriteLine();

        string path = Path.Combine(Path.GetTempPath(), "screenpen_gif_delta.gif");
        GifBitmapEncoder enc = new GifBitmapEncoder();
        using (Bitmap a = Frame(0)) Add(enc, a, 20);
        using (Bitmap b = Frame(1)) Add(enc, b, 20);
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write)) enc.Save(fs);

        byte[] raw = File.ReadAllBytes(path);

        // ---- walk the blocks: report each frame's stored region + GCE --------
        int p = 13;                                  // 6 sig + 7 LSD (no GCT here)
        if ((raw[10] & 0x80) != 0) p += (2 << (raw[10] & 7)) * 3;   // GCT if present
        int frameIdx = 0;
        Console.WriteLine("--- block walk ---");
        while (p < raw.Length && raw[p] != 0x3B)
        {
            if (raw[p] == 0x21 && raw[p + 1] == 0xF9)
            {
                int flags = raw[p + 3];
                int delay = raw[p + 4] | (raw[p + 5] << 8);
                Console.WriteLine(string.Format(
                    "  GCE: disposal={0} transparent={1} tidx={2} delay={3}",
                    (flags >> 2) & 7, (flags & 1) != 0, raw[p + 6], delay));
                p += 3 + raw[p + 2];
                if (raw[p] == 0) p++;
            }
            else if (raw[p] == 0x2C)
            {
                int fx = raw[p + 1] | (raw[p + 2] << 8);
                int fy = raw[p + 3] | (raw[p + 4] << 8);
                int fw = raw[p + 5] | (raw[p + 6] << 8);
                int fh = raw[p + 7] | (raw[p + 8] << 8);
                byte ip = raw[p + 9];
                Console.WriteLine(string.Format(
                    "  IMAGE #{0}: stored region {1},{2} {3}x{4}  (canvas 120x80)",
                    frameIdx, fx, fy, fw, fh));
                frameIdx++;
                p += 10;
                if ((ip & 0x80) != 0) p += (2 << (ip & 7)) * 3;
                p++;
                while (p < raw.Length && raw[p] != 0) p += raw[p] + 1;
                p++;
            }
            else break;
        }
        Console.WriteLine();

        // ---- Q1: does frame 1 decode to the CORRECT image as-is? -------------
        // The encoder's own output must be self-consistent; if it is, leaving the
        // transparency flag untouched preserves whatever delta scheme it used.
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Check(dec.Frames.Count == 2, "decoder sees 2 frames", "frames=" + dec.Frames.Count);

                // Render frame 1 on a white background using WPF's own GIF
                // compositor (it honours disposal/transparency), then compare
                // against the source bitmap.
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    dec.Frames[1], System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                byte[] px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);

                using (Bitmap src = Frame(1))
                {
                    int bad = 0, sampled = 0;
                    for (int y = 0; y < h; y += 3)
                        for (int x = 0; x < w; x += 3)
                        {
                            int o = (y * w + x) * 4;
                            Color wantCol = src.GetPixel(x, y);
                            // BGRA
                            int db = Math.Abs(px[o] - wantCol.B);
                            int dg = Math.Abs(px[o + 1] - wantCol.G);
                            int dr = Math.Abs(px[o + 2] - wantCol.R);
                            sampled++;
                            if (db > 40 || dg > 40 || dr > 40) bad++;
                        }
                    Check(bad == 0, "frame 1 decodes to the correct image as the encoder wrote it",
                          bad + "/" + sampled + " sampled pixels differ");
                }
            }
        }
        catch (Exception ex) { Check(false, "decode frame 1", ex.Message); }

        // ---- Q2: patch delays in place, then verify --------------------------
        int[] want = new int[] { 7, 33 };
        try
        {
            int q = 13;
            if ((raw[10] & 0x80) != 0) q += (2 << (raw[10] & 7)) * 3;
            int seen = 0;
            while (q < raw.Length && raw[q] != 0x3B && seen < want.Length)
            {
                if (raw[q] == 0x21 && raw[q + 1] == 0xF9)
                {
                    raw[q + 4] = (byte)(want[seen] & 0xFF);
                    raw[q + 5] = (byte)((want[seen] >> 8) & 0xFF);
                    seen++;
                    q += 3 + raw[q + 2];
                    if (raw[q] == 0) q++;
                }
                else if (raw[q] == 0x2C)
                {
                    byte ip2 = raw[q + 9];
                    q += 10;
                    if ((ip2 & 0x80) != 0) q += (2 << (ip2 & 7)) * 3;
                    q++;
                    while (q < raw.Length && raw[q] != 0) q += raw[q] + 1;
                    q++;
                }
                else break;
            }
            Check(seen == 2, "patched both GCE delay fields", "patched=" + seen);

            // Insert the NETSCAPE2.0 loop block right after the LSD (+GCT).
            int insertAt = 13;
            if ((raw[10] & 0x80) != 0) insertAt += (2 << (raw[10] & 7)) * 3;
            byte[] loop = new byte[] {
                0x21, 0xFF, 0x0B,
                (byte)'N',(byte)'E',(byte)'T',(byte)'S',(byte)'C',(byte)'A',(byte)'P',(byte)'E',
                (byte)'2',(byte)'.',(byte)'0',
                0x03, 0x01, 0x00, 0x00, 0x00
            };
            byte[] outb = new byte[raw.Length + loop.Length];
            Array.Copy(raw, 0, outb, 0, insertAt);
            Array.Copy(loop, 0, outb, insertAt, loop.Length);
            Array.Copy(raw, insertAt, outb, insertAt + loop.Length, raw.Length - insertAt);

            string outPath = Path.Combine(Path.GetTempPath(), "screenpen_gif_patched.gif");
            File.WriteAllBytes(outPath, outb);

            using (FileStream fs = new FileStream(outPath, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Check(dec.Frames.Count == 2, "patched GIF still has 2 frames", "frames=" + dec.Frames.Count);
                int[] got = new int[dec.Frames.Count];
                for (int i = 0; i < dec.Frames.Count; i++)
                {
                    BitmapMetadata md = dec.Frames[i].Metadata as BitmapMetadata;
                    object v = md == null ? null : md.GetQuery("/grctlext/Delay");
                    got[i] = v == null ? -1 : Convert.ToInt32(v);
                }
                bool ok = got.Length == 2 && got[0] == want[0] && got[1] == want[1];
                Check(ok, "patched per-frame delays read back exactly",
                      "want " + want[0] + "," + want[1] + " got " + got[0] + "," + got[1]);
            }
            Check(System.Text.Encoding.ASCII.GetString(outb).Contains("NETSCAPE2.0"),
                  "patched GIF contains the NETSCAPE2.0 loop block", "loops forever");
            try { File.Delete(outPath); } catch { }
        }
        catch (Exception ex) { Check(false, "patch + re-read", ex.Message); }

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
        try { File.Delete(path); } catch { }
    }
}
