// Decisive test: GifBitmapEncoder emits GCE blocks with transparent=True and
// tidx=2. If that transparency is load-bearing (i.e. the LZW data really does
// use index 2 for content pixels), then simply clearing the flag or patching the
// delay would corrupt colours, and a hand-written encoder is required. If it is
// spurious, patching the encoder's output is safe and far less code.
//
// Method: encode frame0 = solid red, frame1 = solid blue, then decode in order
// with GDI+ and inspect frame1. Any red bleeding into frame1 means the previous
// frame shows through the "transparent" pixels.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

static class GifAlphaTest
{
    static byte[] Png(Bitmap b)
    {
        using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static void Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "screenpen_gif_alpha.gif");
        GifBitmapEncoder enc = new GifBitmapEncoder();
        Color[] cols = new Color[] { Color.Red, Color.Blue };
        foreach (Color c in cols)
        {
            using (Bitmap b = new Bitmap(80, 60))
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
                using (MemoryStream ms = new MemoryStream(Png(b)))
                {
                    BitmapDecoder d = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    enc.Frames.Add(d.Frames[0]);
                }
            }
        }
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write)) enc.Save(fs);

        // Decode with GDI+ in order and inspect each frame.
        using (Image img = Image.FromFile(path))
        {
            FrameDimension time = FrameDimension.Time;
            int n = img.GetFrameCount(time);
            Console.WriteLine("GDI+ reports frames = " + n);
            for (int i = 0; i < n; i++)
            {
                img.SelectActiveFrame(time, i);
                using (Bitmap b = new Bitmap(img))
                {
                    int red = 0, blue = 0, other = 0;
                    for (int y = 0; y < b.Height; y += 4)
                        for (int x = 0; x < b.Width; x += 4)
                        {
                            Color c = b.GetPixel(x, y);
                            if (c.R > 200 && c.B < 60) red++;
                            else if (c.B > 200 && c.R < 60) blue++;
                            else other++;
                        }
                    Console.WriteLine(string.Format("  frame {0}: red={1} blue={2} other={3}", i, red, blue, other));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("If frame 1 shows blue only -> the transparent flag is spurious.");
        Console.WriteLine("If frame 1 shows red bleed   -> transparency is load-bearing.");
        try { File.Delete(path); } catch { }
    }
}
