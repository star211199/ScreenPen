// Diagnostic: dump the actual block structure of a GifBitmapEncoder output.
//
// gif_delay_probe.cs proved the encoder loses per-frame delays and writes no
// NETSCAPE2.0 loop block. Before choosing between "patch the bytes" and "write
// our own GIF89a encoder", we need to know what the encoder DOES emit: are there
// Graphic Control Extension blocks at all (with delay 0), or none?
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

static class GifDump
{
    static Bitmap Frame(int i)
    {
        Bitmap bmp = new Bitmap(64, 40);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(i == 0 ? Brushes.Red : Brushes.Blue, 4 + i * 12, 4, 10, 10);
        }
        return bmp;
    }

    static void Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "screenpen_gif_dump.gif");
        GifBitmapEncoder enc = new GifBitmapEncoder();
        for (int i = 0; i < 3; i++)
        {
            using (Bitmap bmp = Frame(i))
            using (MemoryStream png = new MemoryStream())
            {
                bmp.Save(png, ImageFormat.Png);
                png.Position = 0;
                BitmapDecoder d = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame bf = d.Frames[0];
                BitmapMetadata md = new BitmapMetadata("gif");
                md.SetQuery("/grctlext/Delay", (ushort)(10 * (i + 1)));
                enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
            }
        }
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            enc.Save(fs);

        byte[] b = File.ReadAllBytes(path);
        Console.WriteLine("total bytes = " + b.Length);
        Console.WriteLine("signature   = " + System.Text.Encoding.ASCII.GetString(b, 0, 6));
        Console.WriteLine();
        Console.WriteLine("--- block walk ---");

        int p = 6;                                  // after "GIF89a"
        int lsd = p;
        int w = b[p] | (b[p + 1] << 8), h = b[p + 2] | (b[p + 3] << 8);
        byte packed = b[p + 4];
        bool gct = (packed & 0x80) != 0;
        int gctSize = 2 << (packed & 7);
        Console.WriteLine(string.Format("LSD: {0}x{1} packed=0x{2:X2} GCT={3} GCTsize={4}", w, h, packed, gct, gctSize));
        p += 7;
        if (gct) { Console.WriteLine("  GCT occupies " + (gctSize * 3) + " bytes"); p += gctSize * 3; }

        int frame = 0, gce = 0;
        while (p < b.Length)
        {
            byte blk = b[p];
            if (blk == 0x3B) { Console.WriteLine(string.Format("[{0}] 0x3B TRAILER", p)); break; }
            if (blk == 0x21)                   // extension
            {
                byte label = b[p + 1];
                if (label == 0xF9)             // Graphic Control Extension
                {
                    gce++;
                    int size = b[p + 2];
                    int flags = b[p + 3];
                    int delay = b[p + 4] | (b[p + 5] << 8);
                    int tidx = b[p + 6];
                    Console.WriteLine(string.Format(
                        "[{0}] 0x21 0xF9 GCE size={1} disposal={2} delay={3} (={4} ms) transparent={5} tidx={6}",
                        p, size, (flags >> 2) & 7, delay, delay * 10, (flags & 1) != 0, tidx));
                    p += 3 + size;             // 0x21, 0xF9, size, data[size]
                    if (b[p] == 0) p++;        // block terminator
                }
                else
                {
                    int size = b[p + 2];
                    string tag = label == 0xFF
                        ? System.Text.Encoding.ASCII.GetString(b, p + 3, Math.Min(size, 11))
                        : "label 0x" + label.ToString("X2");
                    Console.WriteLine(string.Format("[{0}] 0x21 EXT {1} size={2}", p, tag, size));
                    p += 2;
                    while (p < b.Length && b[p] != 0) p += b[p] + 1;
                    p++;
                }
            }
            else if (blk == 0x2C)              // image descriptor
            {
                frame++;
                int fx = b[p + 1] | (b[p + 2] << 8), fy = b[p + 3] | (b[p + 4] << 8);
                int fw = b[p + 5] | (b[p + 6] << 8), fh = b[p + 7] | (b[p + 8] << 8);
                byte ip = b[p + 9];
                bool lct = (ip & 0x80) != 0;
                bool interlaced = (ip & 0x40) != 0;
                Console.WriteLine(string.Format(
                    "[{0}] 0x2C IMAGE #{1} at {2},{3} size {4}x{5} LCT={6} interlaced={7}",
                    p, frame, fx, fy, fw, fh, lct, interlaced));
                p += 10;
                if (lct) p += (2 << (ip & 7)) * 3;
                p++;                            // LZW min code size
                while (p < b.Length && b[p] != 0) p += b[p] + 1;
                p++;
            }
            else
            {
                Console.WriteLine(string.Format("[{0}] UNKNOWN 0x{1:X2} -- stopping", p, blk));
                break;
            }
        }
        Console.WriteLine();
        Console.WriteLine("frames=" + frame + "  GCE blocks=" + gce);
        Console.WriteLine("loop block present = " +
            System.Text.Encoding.ASCII.GetString(b).Contains("NETSCAPE2.0"));
        try { File.Delete(path); } catch { }
    }
}
