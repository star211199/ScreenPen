using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Rasterises the ScreenPen brush glyph (assets\画笔.svg) with GDI+ and turns
    /// it into a Windows cursor.
    ///
    /// The SVG uses only straight-line path commands, so a tiny hand-written
    /// parser handles the subset actually present (M m L l H h V v Z z) instead of
    /// pulling in an SVG library that does not exist on the build machine.
    ///
    /// Every public member is total: it never throws and never returns null.
    /// A malformed override file, an unreadable asset or a GDI failure downgrades
    /// to an embedded path constant, then to a generated fallback glyph.
    ///
    /// Compiled with the .NET Framework 4.0 csc (C# 5), so: no string
    /// interpolation, no expression-bodied members, no nameof, no null-conditional
    /// operators, no auto-property initialisers.
    /// </summary>
    public static class BrushCursor
    {
        // ------------------------------------------------------------------
        // Embedded glyph: the fallback and the source of truth when
        // <exedir>\assets\画笔.svg is missing or unparsable.
        // ------------------------------------------------------------------
        const string EmbeddedSvg =
            "<path d=\"M173.013333 882.56l72.106667-203.733333L741.546667 132.266667 789.333333 129.92l76.373334 69.333333 2.346666 47.786667-496.64 546.773333z m109.44-181.333333l-35.84 101.76 99.2-44.586667 478.72-526.933333v-12.586667l-50.773333-45.44h-12.373333z\"/>" +
            "<path d=\"M678.229333 202.026667l126.336 114.752-28.693333 31.573333-126.336-114.730667z\"/>";

        /// <summary>Coordinate space of the SVG viewBox (viewBox="0 0 1024 1024").</summary>
        const float ViewBoxSize = 1024f;

        /// <summary>Breathing room around the glyph, as a fraction of the bitmap, per side.</summary>
        const float PaddingFraction = 0.04f;

        /// <summary>Brush tip position within the glyph, as a fraction of the bitmap.</summary>
        const float TipFractionX = 0.17f;
        const float TipFractionY = 0.86f;

        /// <summary>Largest bitmap we will ever allocate for a cursor glyph.</summary>
        const int MaxSize = 1024;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        static string _svgPath;

        /// <summary>
        /// Optional override file next to the EXE: &lt;exedir&gt;\assets\画笔.svg.
        /// The path is reported whether or not the file exists; callers use
        /// <see cref="File.Exists"/> themselves. Never throws.
        /// </summary>
        public static string SvgPath
        {
            get
            {
                if (_svgPath == null)
                {
                    _svgPath = ComputeSvgPath();
                }
                return _svgPath;
            }
        }

        /// <summary>
        /// Renders the glyph into a new size x size 32bpp ARGB bitmap filled with
        /// <paramref name="fill"/>. Never null, never throws.
        /// </summary>
        public static Bitmap RenderGlyph(int size, Color fill)
        {
            size = ClampSize(size);
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (GraphicsPath path = BuildGlyphPath())
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        g.FillPath(brush, Transform(path, size, 0f, 0f));
                    }
                }
                return bmp;
            }
            catch
            {
                if (bmp != null)
                {
                    try { bmp.Dispose(); }
                    catch { }
                }
                return FallbackGlyph(size, fill);
            }
        }

        /// <summary>
        /// The brush tip, in pixels, for the cursor hotspot. Always inside the
        /// bitmap. Never throws.
        /// </summary>
        public static Point Hotspot(int size)
        {
            try
            {
                size = ClampSize(size);
                int x = (int)Math.Round(TipFractionX * size, MidpointRounding.AwayFromZero);
                int y = (int)Math.Round(TipFractionY * size, MidpointRounding.AwayFromZero);
                return new Point(ClampInt(x, 0, size - 1), ClampInt(y, 0, size - 1));
            }
            catch
            {
                return new Point(0, 0);
            }
        }

        /// <summary>
        /// The glyph drawn over a contrasting outline so the cursor stays legible
        /// on any background. Never null, never throws.
        /// </summary>
        public static Bitmap RenderGlyphWithHalo(int size, Color fill)
        {
            size = ClampSize(size);
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    Color halo = HaloColorFor(fill);
                    int radius = Math.Max(1, (int)Math.Round(size / 16.0, MidpointRounding.AwayFromZero));

                    using (GraphicsPath path = BuildGlyphPath())
                    {
                        // Silhouette stamped in 8 directions, then the glyph on top.
                        using (SolidBrush haloBrush = new SolidBrush(halo))
                        {
                            for (int dy = -radius; dy <= radius; dy += radius)
                            {
                                for (int dx = -radius; dx <= radius; dx += radius)
                                {
                                    if (dx == 0 && dy == 0)
                                    {
                                        continue;
                                    }
                                    g.FillPath(haloBrush, Transform(path, size, dx, dy));
                                }
                            }
                        }
                        using (SolidBrush fillBrush = new SolidBrush(fill))
                        {
                            g.FillPath(fillBrush, Transform(path, size, 0f, 0f));
                        }
                    }
                }
                return bmp;
            }
            catch
            {
                if (bmp != null)
                {
                    try { bmp.Dispose(); }
                    catch { }
                }
                return FallbackGlyph(size, fill);
            }
        }

        /// <summary>
        /// Alpha-blended cursor built from <see cref="RenderGlyphWithHalo"/>.
        /// Never null, never throws: falls back to the standard arrow cursor.
        ///
        /// The bitmap is clamped to the system cursor slot (SM_CXCURSOR, 32 px
        /// on a standard DPI setup): Windows scales anything larger down to fit,
        /// which would blur the glyph instead of sharpening it.
        /// </summary>
        public static Cursor CreateCursor(int size, Color fill)
        {
            // Rasterise at the requested size so the glyph itself stays crisp,
            // then shrink to the slot the OS actually displays.
            size = ClampSize(size);
            int slot = SystemCursorSize();
            int renderSize = size;
            if (slot > 0 && size > slot)
            {
                renderSize = slot;
            }
            IntPtr hIcon = IntPtr.Zero;
            IntPtr hbmColor = IntPtr.Zero;
            IntPtr hbmMask = IntPtr.Zero;
            IntPtr hdc = IntPtr.Zero;
            IntPtr bits = IntPtr.Zero;
            try
            {
                using (Bitmap bmp = RenderGlyphWithHalo(renderSize, fill))
                {
                    hdc = CreateCompatibleDC(IntPtr.Zero);
                    if (hdc == IntPtr.Zero)
                    {
                        return Cursors.Default;
                    }

                    // ---- colour plane: a top-down 32bpp DIB with straight alpha
                    BITMAPINFO bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                    bmi.bmiHeader.biWidth = renderSize;
                    bmi.bmiHeader.biHeight = -renderSize;   // negative == top-down
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0;     // BI_RGB
                    bmi.bmiHeader.biSizeImage = (uint)(renderSize * renderSize * 4);

                    hbmColor = CreateDIBSection(hdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                    if (hbmColor == IntPtr.Zero || bits == IntPtr.Zero)
                    {
                        return Cursors.Default;
                    }

                    CopyBitmapToDib(bmp, bits, renderSize);

                    // ---- mask plane: all zeroes, so the alpha channel decides
                    // what is transparent (a non-zero mask would punch holes or
                    // blacken the icon entirely).
                    int maskStride = ((renderSize + 15) / 16) * 2;
                    byte[] zeroMask = new byte[maskStride * renderSize];
                    GCHandle pinned = GCHandle.Alloc(zeroMask, GCHandleType.Pinned);
                    try
                    {
                        hbmMask = CreateBitmap(renderSize, renderSize, 1, 1, pinned.AddrOfPinnedObject());
                    }
                    finally
                    {
                        pinned.Free();
                    }
                    if (hbmMask == IntPtr.Zero)
                    {
                        return Cursors.Default;
                    }

                    Point hot = Hotspot(renderSize);
                    ICONINFO info = new ICONINFO();
                    // false == this handle is a CURSOR, so Windows honours
                    // xHotspot/yHotspot. Passing true makes the system treat it as
                    // an icon and silently recentre the hotspot at
                    // (renderSize/2, renderSize/2), discarding the brush tip.
                    info.fIcon = false;
                    info.xHotspot = hot.X;
                    info.yHotspot = hot.Y;
                    info.hbmColor = hbmColor;
                    info.hbmMask = hbmMask;

                    hIcon = CreateIconIndirect(ref info);
                    if (hIcon == IntPtr.Zero)
                    {
                        return Cursors.Default;
                    }

                    Cursor cursor = new Cursor(hIcon);
                    hIcon = IntPtr.Zero;      // owned by the Cursor from here on
                    return cursor;
                }
            }
            catch
            {
                return Cursors.Default;
            }
            finally
            {
                // The icon owns copies of both bitmaps, so the originals can go.
                if (hbmMask != IntPtr.Zero) { try { DeleteObject(hbmMask); } catch { } }
                if (hbmColor != IntPtr.Zero) { try { DeleteObject(hbmColor); } catch { } }
                if (hdc != IntPtr.Zero) { try { DeleteDC(hdc); } catch { } }
            }
        }

        // ------------------------------------------------------------------
        // SVG loading + parsing
        // ------------------------------------------------------------------

        /// <summary>Path of the override file. Never throws.</summary>
        static string ComputeSvgPath()
        {
            try
            {
                string dir = null;
                try
                {
                    Assembly asm = Assembly.GetEntryAssembly();
                    if (asm != null)
                    {
                        dir = Path.GetDirectoryName(asm.Location);
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(dir))
                {
                    try { dir = AppDomain.CurrentDomain.BaseDirectory; }
                    catch { }
                }
                if (string.IsNullOrEmpty(dir))
                {
                    dir = ".";
                }
                return Path.Combine(Path.Combine(dir, "assets"), "画笔.svg");
            }
            catch
            {
                return "画笔.svg";
            }
        }

        /// <summary>
        /// The path data to rasterise: every d="" attribute of the override file,
        /// or the embedded constant if the file is absent or unusable.
        /// </summary>
        static string LoadPathData(out string source)
        {
            source = "embedded";
            string overridePath = null;
            try
            {
                overridePath = SvgPath;
                if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                {
                    string text = File.ReadAllText(overridePath, Encoding.UTF8);
                    StringBuilder sb = new StringBuilder();
                    foreach (Match m in PathAttributeRegex.Matches(text))
                    {
                        if (m.Groups.Count > 1 && m.Groups[1].Value.Length > 0)
                        {
                            sb.Append(m.Groups[1].Value);
                            sb.Append(' ');
                        }
                    }
                    if (sb.Length > 0)
                    {
                        source = overridePath;
                        return sb.ToString();
                    }
                }
            }
            catch
            {
                // fall through to the embedded constant
            }
            return ExtractEmbeddedPaths(EmbeddedSvg);
        }

        static readonly Regex PathAttributeRegex = new Regex(
            "<path[^>]*?\\bd\\s*=\\s*[\"']([^\"']*)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>Pulls d="" attributes out of the embedded SVG constant.</summary>
        static string ExtractEmbeddedPaths(string svg)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (Match m in PathAttributeRegex.Matches(svg))
                {
                    if (m.Groups.Count > 1)
                    {
                        sb.Append(m.Groups[1].Value);
                        sb.Append(' ');
                    }
                }
                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }
            catch { }
            return string.Empty;
        }

        // ---- tokeniser ----------------------------------------------------

        struct Token
        {
            public bool IsCommand;
            public char Command;
            public float Value;
        }

        /// <summary>
        /// Splits path data into command letters and numbers. Numbers may run
        /// straight into a command letter ("882.56l72.1"), be separated by commas
        /// or whitespace, or carry a leading '-' as their only separator
        /// ("101.76-35.84" is two numbers).
        /// </summary>
        static System.Collections.Generic.List<Token> Tokenize(string data)
        {
            System.Collections.Generic.List<Token> tokens =
                new System.Collections.Generic.List<Token>();
            if (string.IsNullOrEmpty(data))
            {
                return tokens;
            }

            int i = 0;
            int n = data.Length;
            while (i < n)
            {
                char c = data[i];
                if (IsCommandChar(c))
                {
                    Token t = new Token();
                    t.IsCommand = true;
                    t.Command = c;
                    tokens.Add(t);
                    i++;
                    continue;
                }
                if (c == ',' || char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i;
                if (c == '+' || c == '-')
                {
                    i++;
                }
                while (i < n && data[i] >= '0' && data[i] <= '9')
                {
                    i++;
                }
                if (i < n && data[i] == '.')
                {
                    i++;
                    while (i < n && data[i] >= '0' && data[i] <= '9')
                    {
                        i++;
                    }
                }
                // exponent, if any
                if (i < n && (data[i] == 'e' || data[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < n && (data[i] == '+' || data[i] == '-'))
                    {
                        i++;
                    }
                    if (i < n && data[i] >= '0' && data[i] <= '9')
                    {
                        while (i < n && data[i] >= '0' && data[i] <= '9')
                        {
                            i++;
                        }
                    }
                    else
                    {
                        i = save;   // not an exponent after all
                    }
                }

                if (i == start)
                {
                    i++;            // unrecognised character: skip it
                    continue;
                }

                string raw = data.Substring(start, i - start);
                float value;
                if (!float.TryParse(raw, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out value))
                {
                    continue;
                }
                Token num = new Token();
                num.IsCommand = false;
                num.Value = value;
                tokens.Add(num);
            }
            return tokens;
        }

        static bool IsCommandChar(char c)
        {
            return c == 'M' || c == 'm' || c == 'L' || c == 'l' || c == 'H' ||
                   c == 'h' || c == 'V' || c == 'v' || c == 'Z' || c == 'z';
        }

        /// <summary>
        /// Parses the straight-line subset of SVG path data into closed
        /// subpaths, one list of viewBox-space points each.
        /// </summary>
        static System.Collections.Generic.List<System.Collections.Generic.List<PointF>>
            ParseSubpaths(string data)
        {
            System.Collections.Generic.List<System.Collections.Generic.List<PointF>> subpaths =
                new System.Collections.Generic.List<System.Collections.Generic.List<PointF>>();
            System.Collections.Generic.List<Token> tokens = Tokenize(data);
            if (tokens.Count == 0)
            {
                return subpaths;
            }

            System.Collections.Generic.List<PointF> current = null;
            float cx = 0f, cy = 0f;      // current point
            float sx = 0f, sy = 0f;      // start of the current subpath
            char cmd = 'M';
            int i = 0;

            while (i < tokens.Count)
            {
                Token t = tokens[i];
                if (t.IsCommand)
                {
                    cmd = t.Command;
                    i++;

                    if (cmd == 'Z' || cmd == 'z')
                    {
                        if (current != null)
                        {
                            current.Add(new PointF(sx, sy));
                            subpaths.Add(current);
                            current = null;
                        }
                        // After a close the current point is the subpath start.
                        cx = sx;
                        cy = sy;
                        continue;
                    }

                    if (cmd == 'M' || cmd == 'm')
                    {
                        System.Collections.Generic.List<float> nums = ReadNumbers(tokens, ref i);
                        if (nums.Count < 2)
                        {
                            continue;
                        }
                        float bx = (cmd == 'm') ? cx : 0f;
                        float by = (cmd == 'm') ? cy : 0f;
                        cx = nums[0] + bx;
                        cy = nums[1] + by;
                        sx = cx;
                        sy = cy;

                        if (current != null)
                        {
                            subpaths.Add(current);
                        }
                        current = new System.Collections.Generic.List<PointF>();
                        current.Add(new PointF(cx, cy));

                        // Extra coordinate pairs after M/m are implicit linetos.
                        for (int k = 2; k + 1 < nums.Count; k += 2)
                        {
                            cx = nums[k] + bx;
                            cy = nums[k + 1] + by;
                            current.Add(new PointF(cx, cy));
                        }
                        cmd = (cmd == 'm') ? 'l' : 'L';
                        continue;
                    }

                    if (cmd == 'L' || cmd == 'l')
                    {
                        while (i + 1 < tokens.Count && !tokens[i].IsCommand)
                        {
                            float nx = tokens[i].Value;
                            float ny = tokens[i + 1].Value;
                            i += 2;
                            if (cmd == 'l')
                            {
                                cx += nx;
                                cy += ny;
                            }
                            else
                            {
                                cx = nx;
                                cy = ny;
                            }
                            current = AppendPoint(current, cx, cy);
                        }
                        continue;
                    }

                    if (cmd == 'H' || cmd == 'h')
                    {
                        while (i < tokens.Count && !tokens[i].IsCommand)
                        {
                            float nx = tokens[i].Value;
                            i++;
                            cx = (cmd == 'h') ? cx + nx : nx;
                            current = AppendPoint(current, cx, cy);
                        }
                        continue;
                    }

                    if (cmd == 'V' || cmd == 'v')
                    {
                        while (i < tokens.Count && !tokens[i].IsCommand)
                        {
                            float ny = tokens[i].Value;
                            i++;
                            cy = (cmd == 'v') ? cy + ny : ny;
                            current = AppendPoint(current, cx, cy);
                        }
                        continue;
                    }

                    // Unknown command letter: ignore it and keep scanning.
                    continue;
                }

                i++;    // stray number with no pending command
            }

            if (current != null)
            {
                subpaths.Add(current);
            }
            return subpaths;
        }

        static System.Collections.Generic.List<float> ReadNumbers(
            System.Collections.Generic.List<Token> tokens, ref int i)
        {
            System.Collections.Generic.List<float> nums =
                new System.Collections.Generic.List<float>();
            while (i < tokens.Count && !tokens[i].IsCommand)
            {
                nums.Add(tokens[i].Value);
                i++;
            }
            return nums;
        }

        static System.Collections.Generic.List<PointF> AppendPoint(
            System.Collections.Generic.List<PointF> current, float x, float y)
        {
            if (current == null)
            {
                current = new System.Collections.Generic.List<PointF>();
            }
            current.Add(new PointF(x, y));
            return current;
        }

        // ------------------------------------------------------------------
        // Path construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the glyph as a single GraphicsPath in viewBox coordinates.
        ///
        /// FillMode.Alternate is deliberate: the first &lt;path&gt; carries an outer
        /// silhouette (winding +) and a nested inner contour (winding -), so the
        /// even-odd rule cuts the inner contour out and the glyph renders as the
        /// intended hollow brush outline rather than a solid blob.
        /// </summary>
        static GraphicsPath BuildGlyphPath()
        {
            try
            {
                string source;
                string data = LoadPathData(out source);
                System.Collections.Generic.List<System.Collections.Generic.List<PointF>> subs =
                    ParseSubpaths(data);

                GraphicsPath path = new GraphicsPath(FillMode.Alternate);
                bool added = false;
                for (int s = 0; s < subs.Count; s++)
                {
                    System.Collections.Generic.List<PointF> sub = subs[s];
                    if (sub == null || sub.Count < 3)
                    {
                        continue;
                    }
                    path.AddPolygon(sub.ToArray());
                    added = true;
                }
                if (added)
                {
                    return path;
                }
                path.Dispose();
            }
            catch { }
            return FallbackPath();
        }

        /// <summary>ViewBox-space fallback: a simple diagonal brush silhouette.</summary>
        static GraphicsPath FallbackPath()
        {
            GraphicsPath path = new GraphicsPath(FillMode.Alternate);
            try
            {
                path.AddPolygon(new PointF[]
                {
                    new PointF(200f, 830f), new PointF(730f, 250f),
                    new PointF(840f, 350f), new PointF(310f, 930f)
                });
                path.AddPolygon(new PointF[]
                {
                    new PointF(90f, 960f), new PointF(330f, 900f), new PointF(170f, 700f)
                });
            }
            catch { }
            return path;
        }

        /// <summary>Maps viewBox space into a size x size bitmap, with padding and an optional offset.</summary>
        static GraphicsPath Transform(GraphicsPath source, int size, float offsetX, float offsetY)
        {
            GraphicsPath copy = (GraphicsPath)source.Clone();
            try
            {
                float pad = size * PaddingFraction;
                float scale = (size - 2f * pad) / ViewBoxSize;
                using (Matrix m = new Matrix())
                {
                    m.Scale(scale, scale, MatrixOrder.Append);
                    m.Translate(pad + offsetX, pad + offsetY, MatrixOrder.Append);
                    copy.Transform(m);
                }
            }
            catch { }
            return copy;
        }

        /// <summary>The last-resort glyph, generated with plain GDI+ primitives.</summary>
        static Bitmap FallbackGlyph(int size, Color fill)
        {
            size = ClampSize(size);
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath path = FallbackPath())
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        g.FillPath(brush, Transform(path, size, 0f, 0f));
                    }
                }
            }
            catch
            {
                // Even the fallback failed: return the (possibly transparent)
                // bitmap anyway rather than throwing or returning null.
            }
            return bmp;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static Color HaloColorFor(Color fill)
        {
            double luminance = (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255.0;
            return luminance > 0.6 ? Color.Black : Color.White;
        }

        static int ClampSize(int size)
        {
            if (size < 1) return 1;
            if (size > MaxSize) return MaxSize;
            return size;
        }

        /// <summary>
        /// The width of the cursor slot Windows will actually display
        /// (SM_CXCURSOR). Returns 0 if the metric is unavailable, in which case
        /// the caller keeps the requested size.
        /// </summary>
        static int SystemCursorSize()
        {
            try
            {
                int v = GetSystemMetrics(SM_CXCURSOR);
                return v > 0 ? v : 0;
            }
            catch
            {
                return 0;
            }
        }

        static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>Copies a 32bpp ARGB bitmap into a top-down DIB, row by row.</summary>
        static void CopyBitmapToDib(Bitmap bmp, IntPtr dibBits, int size)
        {
            BitmapData data = null;
            try
            {
                Rectangle rect = new Rectangle(0, 0, size, size);
                data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int rowBytes = size * 4;
                for (int y = 0; y < size; y++)
                {
                    IntPtr src = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    IntPtr dst = new IntPtr(dibBits.ToInt64() + (long)y * rowBytes);
                    CopyMemory(dst, src, new UIntPtr((uint)rowBytes));
                }
            }
            finally
            {
                if (data != null)
                {
                    bmp.UnlockBits(data);
                }
            }
        }

        // ------------------------------------------------------------------
        // P/Invoke
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
                                              out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes,
                                          uint nBitCount, IntPtr lpvBits);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        /// <summary>SM_CXCURSOR: width of the system cursor slot, in pixels.</summary>
        const int SM_CXCURSOR = 13;

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
    }
}
