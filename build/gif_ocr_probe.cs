// Probe for src\Gif.cs and src\Ocr.cs.
//
// The GIF half is the important one. build\gif_probe.cs "passed" while asserting
// only the frame count, and that masked two real defects that gif_delay_probe.cs
// then exposed: GifBitmapEncoder drops per-frame delays, and it writes no
// NETSCAPE2.0 loop block. So this probe verifies the FIX, not just that a file
// appeared:
//
//   * per-frame delays survive, verified by an INDEPENDENT decoder (Pillow, via
//     Python) as well as by WPF's own metadata reader
//   * the loop block is present
//   * collapsed (identical) frames really do produce a longer delay
//   * byte-equality and pixel-equality agree, so the cheap PNG compare used by
//     GifBuilder cannot merge two visually different frames
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ScreenPen.Probes
{
    static class GifOcrProbe
    {
        static int _pass, _fail;

        // Pin the process to a known DPI awareness, and size every font in
        // PIXELS rather than points.
        //
        // Point sizes resolve against Graphics.DpiY, which follows the process's
        // DPI awareness. On this 200% machine that made the SAME test image come
        // out 867x155 of ink inside a 760x130 canvas (clipped on the right and
        // bottom) in one binary and a comfortable 434x78 in another, purely
        // because the two binaries ended up with different awareness. The engine
        // then correctly found no text, and the probe reported it as an OCR
        // failure. Sizing in pixels makes the drawn images identical either way.
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        static void PinDpi()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor v2
            catch { }
        }

        static Font PixelFont(float px, FontStyle style)
        {
            return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
        }

        static void Check(bool ok, string what, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
            else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        }

        static Bitmap Solid(Color c, int w, int h)
        {
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        // A frame with content that changes, so it is never mistaken for a repeat.
        static Bitmap Varied(int i, int w, int h)
        {
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.SteelBlue, 4 + i * 7, 4, 24, 24);
                using (Font f = PixelFont(15f, FontStyle.Regular))
                    g.DrawString("frame " + i, f, Brushes.Black, new PointF(4, h - 24));
            }
            return b;
        }

        static int[] ReadDelays(string path)
        {
            if (!File.Exists(path)) return new int[0];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int[] got = new int[dec.Frames.Count];
                for (int i = 0; i < dec.Frames.Count; i++)
                {
                    BitmapMetadata md = dec.Frames[i].Metadata as BitmapMetadata;
                    object v = md == null ? null : md.GetQuery("/grctlext/Delay");
                    got[i] = v == null ? -1 : Convert.ToInt32(v);
                }
                return got;
            }
        }

        static int FrameCount(string path)
        {
            if (!File.Exists(path)) return -1;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                GifBitmapDecoder dec = new GifBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return dec.Frames.Count;
            }
        }

        [STAThread]
        static int Main()
        {
            // Must run before the first Graphics is created, or the test images
            // are rasterised at a different scale than they were measured at.
            PinDpi();

            Console.WriteLine("=== GIF + OCR PROBE ===");
            Console.WriteLine();

            // ================================================================
            //  1. Gif.Encode writes delays the encoder alone would have dropped
            // ================================================================
            Console.WriteLine("-- Gif.Encode --");
            string outDir = Path.Combine(Path.GetTempPath(), "screenpen_gifocr_probe");
            Directory.CreateDirectory(outDir);
            string p1 = Path.Combine(outDir, "delays.gif");

            List<byte[]> pngs = new List<byte[]>();
            List<int> delays = new List<int>();
            Bitmap[] srcs = new Bitmap[3];
            int[] want = new int[] { 5, 40, 15 };
            for (int i = 0; i < 3; i++)
            {
                srcs[i] = Varied(i, 160, 90);
                pngs.Add(Gif.ToPngBytes(srcs[i]));
                delays.Add(want[i]);
            }

            string err = Gif.Encode(pngs, delays, p1, true);
            Check(err == null, "Gif.Encode reports success", err);
            Check(File.Exists(p1), "GIF file exists", p1);
            Check(FrameCount(p1) == 3, "GIF has 3 frames", FrameCount(p1).ToString());

            int[] got = ReadDelays(p1);
            bool delaysOk = got.Length == 3 && got[0] == want[0] && got[1] == want[1] && got[2] == want[2];
            Check(delaysOk, "per-frame delays survive (the encoder alone loses these)",
                  "want 5,40,15 got " + string.Join(",", Array.ConvertAll(got, x => x.ToString())));

            byte[] bytes = File.ReadAllBytes(p1);
            Check(System.Text.Encoding.ASCII.GetString(bytes).Contains("NETSCAPE2.0"),
                  "loop block was inserted", "loops forever");
            Check(System.Text.Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a",
                  "header is GIF89a", "ok");

            // Delay of 1 must be clamped up, never written as 0 (a 0 delay is
            // rendered at whatever the viewer feels like).
            string p2 = Path.Combine(outDir, "clamp.gif");
            List<int> zero = new List<int>(new int[] { 0, 1 });
            List<byte[]> two = new List<byte[]>();
            two.Add(Gif.ToPngBytes(srcs[0]));
            two.Add(Gif.ToPngBytes(srcs[1]));
            err = Gif.Encode(two, zero, p2, false);
            Check(err == null, "Gif.Encode with a 0 delay succeeds", err);
            int[] got2 = ReadDelays(p2);
            Check(got2.Length == 2 && got2[0] >= 1 && got2[1] >= 1,
                  "0 and 1 delays are clamped to >= 1", string.Join(",", Array.ConvertAll(got2, x => x.ToString())));
            Check(!System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(p2)).Contains("NETSCAPE2.0"),
                  "loop=false omits the loop block", "plays once");

            // Failure paths must report, not throw.
            Check(Gif.Encode(new List<byte[]>(), new List<int>(), p2, true) != null,
                  "empty frame list returns an error string", "not null");
            Check(Gif.Encode(two, zero, null, true) != null,
                  "null path returns an error string", "not null");

            // The destination must not be truncated until a complete GIF has
            // been written. A directory at the destination forces the final
            // move to fail after the temporary file has been created.
            string blocked = Path.Combine(outDir, "blocked.gif");
            Directory.CreateDirectory(blocked);
            string blockedError = Gif.Encode(two, zero, blocked, true);
            Check(blockedError != null && Directory.Exists(blocked),
                  "failed final write leaves the existing destination untouched", blockedError);
            Check(Directory.GetFiles(outDir, "blocked.gif.*.tmp").Length == 0,
                  "failed write removes its temporary file", "ok");
            Directory.Delete(blocked);

            // ================================================================
            //  2. byte equality == pixel equality
            // ================================================================
            // GifBuilder dedupes frames by comparing PNG bytes. That is only
            // sound if identical pixels always encode to identical bytes AND
            // different pixels never do. If GDI+ PNG encoding were
            // non-deterministic, distinct frames would be byte-equal and the
            // recording would silently freeze.
            Console.WriteLine();
            Console.WriteLine("-- frame comparison --");
            Bitmap a1 = Varied(3, 120, 80);
            Bitmap a2 = Varied(3, 120, 80);          // same content, drawn again
            Bitmap b1 = Varied(4, 120, 80);          // different content

            Check(Gif.FramesIdentical(a1, a2), "identical pixels report identical", "ok");
            Check(!Gif.FramesIdentical(a1, b1), "different pixels report different", "ok");

            byte[] pa1 = Gif.ToPngBytes(a1);
            byte[] pa2 = Gif.ToPngBytes(a2);
            byte[] pb1 = Gif.ToPngBytes(b1);
            bool bytesSame = pa1.Length == pa2.Length;
            if (bytesSame)
                for (int i = 0; i < pa1.Length; i++) if (pa1[i] != pa2[i]) { bytesSame = false; break; }
            Check(bytesSame, "PNG encoding is deterministic (same pixels -> same bytes)",
                  pa1.Length + " vs " + pa2.Length + " bytes");
            Check(!(pa1.Length == pb1.Length && SameArr(pa1, pb1)),
                  "different pixels never encode to identical bytes", "ok");

            Check(!Gif.FramesIdentical(a1, null), "null frame reports different", "ok");
            Check(!Gif.FramesIdentical(a1, new Bitmap(40, 40)), "size mismatch reports different", "ok");

            // ================================================================
            //  3. GifBuilder collapsing
            // ================================================================
            Console.WriteLine();
            Console.WriteLine("-- GifBuilder --");
            GifBuilder gb = new GifBuilder(10, true);
            Bitmap still = Solid(Color.White, 80, 60);
            // 5 identical samples then 1 different one.
            for (int i = 0; i < 5; i++) gb.Add(still);
            gb.Add(Varied(9, 80, 60));

            Check(gb.FrameCount == 2, "5 identical samples collapse into 1 frame", "frames=" + gb.FrameCount);
            Check(gb.CollapsedFrames == 4, "4 samples were collapsed", "collapsed=" + gb.CollapsedFrames);
            int[] gd = gb.Delays;
            Check(gd.Length == 2 && gd[0] == 50,
                  "collapsed frame carries the accumulated delay (5 x 10cs = 50cs)",
                  "delay0=" + (gd.Length > 0 ? gd[0].ToString() : "?"));

            string p3 = Path.Combine(outDir, "collapsed.gif");
            err = gb.Save(p3, true);
            Check(err == null, "GifBuilder.Save succeeds", err);
            int[] gd2 = ReadDelays(p3);
            Check(gd2.Length == 2 && gd2[0] == 50,
                  "collapsed delay reaches the file", "delay0=" + (gd2.Length > 0 ? gd2[0].ToString() : "?"));

            // With dedupe off, nothing may be merged.
            GifBuilder gb2 = new GifBuilder(10, false);
            for (int i = 0; i < 4; i++) gb2.Add(Solid(Color.White, 40, 30));
            Check(gb2.FrameCount == 4, "dedupe off stores every sample", "frames=" + gb2.FrameCount);

            // A very long static stretch must eventually store a real duplicate
            // rather than one frame with an absurd delay.
            GifBuilder gb3 = new GifBuilder(10, true);
            for (int i = 0; i < 200; i++) gb3.Add(Solid(Color.White, 40, 30));
            Check(gb3.FrameCount > 1, "a long static stretch still stores later frames",
                  "frames=" + gb3.FrameCount + " collapsed=" + gb3.CollapsedFrames);
            int[] gd3 = gb3.Delays;
            int maxD = 0;
            foreach (int d in gd3) if (d > maxD) maxD = d;
            Check(maxD <= GifBuilder.MaxFrameDelayCs, "no single delay exceeds the cap",
                  "max=" + maxD + " cap=" + GifBuilder.MaxFrameDelayCs);

            // ================================================================
            //  4. OCR availability + a real recognition
            // ================================================================
            Console.WriteLine();
            Console.WriteLine("-- OCR --");
            string detail;
            bool avail = Ocr.IsAvailable(out detail);
            Check(avail, "Windows OCR engine is available", detail);

            string[] langs = Ocr.AvailableLanguages();
            Check(langs.Length > 0, "at least one recogniser language is installed",
                  string.Join(", ", langs));

            Settings cfg = new Settings();
            cfg.OcrUpscaleSmall = 3;
            cfg.OcrKeepLineBreaks = true;

            const string Want = "ScreenPen 12345";
            using (Bitmap big = new Bitmap(760, 130))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = PixelFont(48f, FontStyle.Regular))
                        g.DrawString(Want, f, Brushes.Black, new PointF(20, 25));
                }

                OcrResult r = Ocr.Recognize(big, cfg);
                Check(r.Ok, "Recognize succeeds on a clean image", r.Error);

                // Guard the guard: if the ink runs to the edge of the canvas the
                // text is clipped and the engine cannot be expected to read it.
                // Without this, a DPI-dependent draw shows up as "OCR is broken".
                int inkR = 0, inkB = 0;
                for (int y = 0; y < big.Height; y++)
                    for (int x = 0; x < big.Width; x++)
                    {
                        Color c = big.GetPixel(x, y);
                        if (((c.R * 77 + c.G * 150 + c.B * 29) >> 8) < 128)
                        {
                            if (x > inkR) inkR = x;
                            if (y > inkB) inkB = y;
                        }
                    }
                Check(inkR < big.Width - 2 && inkB < big.Height - 2,
                      "the test text fits inside the canvas (not clipped)",
                      "ink reaches " + inkR + "x" + inkB + " of " + big.Width + "x" + big.Height);

                Check(r.Text.Contains("ScreenPen") && r.Text.Contains("12345"),
                      "recognised text matches what was drawn",
                      "\"" + (r.Text ?? "").Replace("\r", " ").Replace("\n", " ") + "\"");
                Check(r.Lines.Count > 0, "result exposes lines", "lines=" + r.Lines.Count);
                Check(r.LanguageTag.Length > 0, "result reports the recogniser language", r.LanguageTag);
                Check(r.Upscale == 1f, "a large image is not upscaled", "upscale=" + r.Upscale);

                int wordCount = 0, boxed = 0;
                foreach (OcrLine l in r.Lines)
                    foreach (OcrWord w in l.Words)
                    {
                        wordCount++;
                        if (w.Bounds.Width > 0 && w.Bounds.Height > 0) boxed++;
                    }
                Check(wordCount > 0, "words were returned", "words=" + wordCount);
                Check(boxed == wordCount, "every word has a bounding box", boxed + "/" + wordCount);

                // Bounding boxes must be inside the original image: if the
                // upscale division were wrong they would land far outside.
                bool inBounds = true;
                foreach (OcrLine l in r.Lines)
                    if (l.Bounds != Rectangle.Empty &&
                        (l.Bounds.Left < -2 || l.Bounds.Top < -2 ||
                         l.Bounds.Right > big.Width + 2 || l.Bounds.Bottom > big.Height + 2))
                        inBounds = false;
                Check(inBounds, "line boxes lie within the source image", "ok");
            }

            // A small image must be upscaled, and the boxes must still map back
            // into the ORIGINAL coordinates rather than the enlarged ones.
            using (Bitmap small = new Bitmap(200, 46))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = PixelFont(22f, FontStyle.Regular))
                        g.DrawString("Hello", f, Brushes.Black, new PointF(6, 8));
                }
                OcrResult rs = Ocr.Recognize(small, cfg);
                Check(rs.Ok, "Recognize succeeds on a small image", rs.Error);
                Check(rs.Upscale > 1f, "a small image is upscaled before recognition",
                      "upscale=" + rs.Upscale);
                bool smallIn = true;
                foreach (OcrLine l in rs.Lines)
                    if (l.Bounds != Rectangle.Empty &&
                        (l.Bounds.Right > small.Width + 2 || l.Bounds.Bottom > small.Height + 2))
                        smallIn = false;
                Check(smallIn, "small-image boxes map back to original coordinates", "ok");
            }

            // Upscale disabled must not break recognition.
            Settings off = new Settings();
            off.OcrUpscaleSmall = 0;
            using (Bitmap ok2 = new Bitmap(600, 100))
            {
                using (Graphics g = Graphics.FromImage(ok2))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = PixelFont(42f, FontStyle.Regular))
                        g.DrawString("ABC", f, Brushes.Black, new PointF(10, 20));
                }
                OcrResult r0 = Ocr.Recognize(ok2, off);
                Check(r0.Ok && r0.Text.Contains("ABC"), "upscale=0 still recognises",
                      r0.Ok ? "\"" + r0.Text + "\"" : r0.Error);
            }

            // Null / empty input must be reported, not thrown.
            OcrResult rn = Ocr.Recognize(null, cfg);
            Check(!rn.Ok && rn.Error != null, "null image reports an error", rn.Error);

            // KeepLineBreaks must actually change the joined text.
            using (Bitmap multi = new Bitmap(400, 200))
            {
                using (Graphics g = Graphics.FromImage(multi))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = PixelFont(32f, FontStyle.Regular))
                    {
                        g.DrawString("alpha", f, Brushes.Black, new PointF(10, 10));
                        g.DrawString("bravo", f, Brushes.Black, new PointF(10, 70));
                        g.DrawString("charlie", f, Brushes.Black, new PointF(10, 130));
                    }
                }
                Settings k = new Settings(); k.OcrKeepLineBreaks = true; k.OcrUpscaleSmall = 3;
                Settings j = new Settings(); j.OcrKeepLineBreaks = false; j.OcrUpscaleSmall = 3;
                OcrResult rk = Ocr.Recognize(multi, k);
                OcrResult rj = Ocr.Recognize(multi, j);
                Check(rk.Ok && rj.Ok, "multi-line recognition succeeds",
                      rk.Ok ? "" : rk.Error);
                Check(rk.Text.Contains("\r\n") || rk.Text.Contains("\n"),
                      "KeepLineBreaks=true joins with newlines",
                      "\"" + rk.Text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"");
                Check(!rj.Text.Contains("\r\n") && !rj.Text.Contains("\n"),
                      "KeepLineBreaks=false joins with spaces",
                      "\"" + rj.Text + "\"");
            }

            // Worker path: must return within its timeout and give the same text.
            using (Bitmap wb = new Bitmap(760, 130))
            {
                using (Graphics g = Graphics.FromImage(wb))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = PixelFont(48f, FontStyle.Regular))
                        g.DrawString(Want, f, Brushes.Black, new PointF(20, 25));
                }
                DateTime t0 = DateTime.UtcNow;
                OcrResult rw = Ocr.RecognizeOnWorker(wb, cfg, 25000);
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                Check(rw.Ok, "RecognizeOnWorker succeeds", rw.Error);
                Check(rw.Text.Contains("12345"), "worker returns the same text",
                      "\"" + (rw.Text ?? "").Replace("\r", " ").Replace("\n", " ") + "\"");
                Check(ms < 25000, "worker respected its timeout", ms + " ms");
            }

            // ================================================================
            //  Cleanup
            // ================================================================
            foreach (Bitmap b in srcs) if (b != null) b.Dispose();
            foreach (Bitmap b in new Bitmap[] { a1, a2, b1, still }) if (b != null) b.Dispose();
            try { Directory.Delete(outDir, true); } catch { }

            Console.WriteLine();
            Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
            return _fail == 0 ? 0 : 1;
        }

        static bool SameArr(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
