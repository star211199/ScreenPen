// Standalone check of BrushCursor's SVG parsing + rasterisation.
// Compiles BrushCursor.cs on its own with a tiny driver so we can assert the
// glyph is non-empty without launching the GUI.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class BrushProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    static int[,] SampleAlpha(Bitmap bmp)
    {
        int w = Math.Min(bmp.Width, 128);
        int[,] grid = new int[16, 16];
        for (int gy = 0; gy < 16; gy++)
        {
            for (int gx = 0; gx < 16; gx++)
            {
                int x = gx * bmp.Width / 16;
                int y = gy * bmp.Height / 16;
                Color c = bmp.GetPixel(Math.Min(x, bmp.Width - 1), Math.Min(y, bmp.Height - 1));
                grid[gy, gx] = c.A > 40 ? 1 : 0;
            }
        }
        return grid;
    }

    static void Dump(int[,] g, string title)
    {
        Console.WriteLine("--- " + title + " ---");
        for (int y = 0; y < 16; y++)
        {
            var sb = new System.Text.StringBuilder();
            for (int x = 0; x < 16; x++) sb.Append(g[y, x] == 1 ? '#' : '.');
            Console.WriteLine(sb.ToString());
        }
    }

    [STAThread]
    static int Main()
    {
        int failures = 0;
        Type t = typeof(ScreenPen.BrushCursor);

        // --- 1. SvgPath is reported ---
        string svg = ScreenPen.BrushCursor.SvgPath;
        Console.WriteLine("SvgPath = " + svg);
        if (string.IsNullOrEmpty(svg)) { Console.WriteLine("FAIL: SvgPath empty"); failures++; }

        // --- 2. Glyph renders with visible coverage ---
        using (Bitmap bmp = ScreenPen.BrushCursor.RenderGlyph(96, Color.Black))
        {
            if (bmp == null) { Console.WriteLine("FAIL: RenderGlyph null"); return 1; }
            Console.WriteLine("RenderGlyph = " + bmp.Width + "x" + bmp.Height);

            int opaque = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).A > 40) opaque++;

            double coverage = (double)opaque / (bmp.Width * bmp.Height);
            Console.WriteLine("opaque px = " + opaque + "  coverage = " + coverage.ToString("F3"));
            if (coverage < 0.03) { Console.WriteLine("FAIL: glyph coverage too low"); failures++; }
            if (coverage > 0.97) { Console.WriteLine("WARN: glyph looks solid"); }

            Dump(SampleAlpha(bmp), "RenderGlyph(96) alpha");
        }

        // --- 3. Halo adds a contrasting outline ---
        using (Bitmap plain = ScreenPen.BrushCursor.RenderGlyph(96, Color.Black))
        using (Bitmap halo = ScreenPen.BrushCursor.RenderGlyphWithHalo(96, Color.Black))
        {
            int pa = 0, ha = 0;
            for (int y = 0; y < 96; y++)
                for (int x = 0; x < 96; x++)
                {
                    if (plain.GetPixel(x, y).A > 40) pa++;
                    if (halo.GetPixel(x, y).A > 40) ha++;
                }
            Console.WriteLine("plain opaque = " + pa + "  halo opaque = " + ha);
            if (ha <= pa) { Console.WriteLine("FAIL: halo did not grow the silhouette"); failures++; }
        }

        // --- 4. Hotspot is inside the bitmap and near the lower-left tip ---
        Point hot = ScreenPen.BrushCursor.Hotspot(96);
        Console.WriteLine("Hotspot(96) = " + hot.X + "," + hot.Y);
        if (hot.X < 0 || hot.Y < 0 || hot.X >= 96 || hot.Y >= 96)
        { Console.WriteLine("FAIL: hotspot out of bounds"); failures++; }
        if (hot.X > 48 || hot.Y < 48)
        { Console.WriteLine("FAIL: hotspot not at the lower-left brush tip"); failures++; }

        // --- 5. Cursor creation succeeds and is not the default arrow ---
        try
        {
            using (Cursor cur = ScreenPen.BrushCursor.CreateCursor(48, Color.Red))
            {
                if (cur == null) { Console.WriteLine("FAIL: CreateCursor null"); failures++; }
                else
                {
                    bool isDefault = object.ReferenceEquals(cur, Cursors.Default);
                    Console.WriteLine("CreateCursor -> " + (isDefault ? "Cursors.Default (FALLBACK)" : "custom hcursor"));
                    if (isDefault) { Console.WriteLine("FAIL: cursor fell back to default"); failures++; }
                    // Cursor.Size reports the *system* slot (SM_CXCURSOR), not the
                    // bitmap we handed over, so only sanity-check it here.
                    Console.WriteLine("cursor slot = " + cur.Size.Width + "x" + cur.Size.Height
                                      + " (SM_CXCURSOR; oversized glyphs are clamped to this)");
                    if (cur.Size.Width <= 0) { Console.WriteLine("FAIL: cursor size invalid"); failures++; }
                    if (cur.Handle == IntPtr.Zero) { Console.WriteLine("FAIL: cursor handle null"); failures++; }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("FAIL: CreateCursor threw " + ex.Message); failures++; }

        // --- 5b. The cursor hotspot must be the brush TIP, not the centre ---
        // ICONINFO.fIcon must be false. When true, Windows treats the handle as
        // an icon and silently recentres the hotspot to (size/2, size/2), which
        // throws away the brush tip and makes the pointer feel offset.
        try
        {
            using (Cursor cur = ScreenPen.BrushCursor.CreateCursor(32, Color.Black))
            {
                ICONINFO info = default(ICONINFO);
                if (GetIconInfo(cur.Handle, ref info))
                {
                    Point want = ScreenPen.BrushCursor.Hotspot(32);
                    Console.WriteLine("cursor hotspot = " + info.xHotspot + "," + info.yHotspot
                                      + "   expected tip = " + want.X + "," + want.Y);
                    if (info.xHotspot != want.X || info.yHotspot != want.Y)
                    {
                        Console.WriteLine("FAIL: hotspot is not the brush tip (fIcon regression?)");
                        failures++;
                    }
                    int centre = 16;
                    if (info.xHotspot == centre && info.yHotspot == centre)
                    {
                        Console.WriteLine("FAIL: hotspot collapsed to the centre");
                        failures++;
                    }
                    if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                    if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                }
                else { Console.WriteLine("FAIL: GetIconInfo failed"); failures++; }
            }
        }
        catch (Exception ex) { Console.WriteLine("FAIL: hotspot check threw " + ex.Message); failures++; }

        // --- 6. Degenerate sizes must not throw ---
        foreach (int s in new int[] { 0, 1, 2, 4096 })
        {
            try
            {
                using (Bitmap b = ScreenPen.BrushCursor.RenderGlyph(s, Color.Blue)) { }
                Point h = ScreenPen.BrushCursor.Hotspot(s);
            }
            catch (Exception ex) { Console.WriteLine("FAIL: size " + s + " threw " + ex.Message); failures++; }
        }
        Console.WriteLine("degenerate sizes: ok");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL BRUSH CHECKS PASSED" : ("FAILURES: " + failures));
        return failures == 0 ? 0 : 1;
    }
}
