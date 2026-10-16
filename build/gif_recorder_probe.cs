// Probe for src\GifRecorder.cs.
//
// GifRecorder drives a WinForms timer that samples the screen. The parts worth
// verifying without a human watching are:
//   * a recording runs, samples frames, and writes a decodable GIF
//   * the configured frame rate and the max-duration cap are honoured
//   * the chrome (border/status pill) is either excluded from capture or not
//     drawn at all -- it must never appear inside the recorded frames
//   * Cancel writes nothing
//   * the output lands in the configured folder with a .gif extension
//
// The chrome check matters most: a border baked into every frame is the classic
// screen-recorder bug, and it is invisible in a file listing.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ScreenPen.Probes
{
    static class GifRecorderProbe
    {
        static int _pass, _fail;

        static void Check(bool ok, string what, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
            else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        }

        static string _dir;

        // Frames of a finished GIF, decoded with WPF (an independent path from
        // the encoder we used to write it).
        static List<Bitmap> DecodeFrames(string path)
        {
            List<Bitmap> frames = new List<Bitmap>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                System.Windows.Media.Imaging.GifBitmapDecoder dec =
                    new System.Windows.Media.Imaging.GifBitmapDecoder(fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                foreach (System.Windows.Media.Imaging.BitmapFrame bf in dec.Frames)
                {
                    System.Windows.Media.Imaging.FormatConvertedBitmap conv =
                        new System.Windows.Media.Imaging.FormatConvertedBitmap(
                            bf, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    int w = conv.PixelWidth, h = conv.PixelHeight;
                    int stride = w * 4;
                    byte[] buf = new byte[stride * h];
                    conv.CopyPixels(buf, stride, 0);
                    Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
                    bmp.UnlockBits(bd);
                    frames.Add(bmp);
                }
            }
            return frames;
        }

        static void Pump(int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount < end)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
        }

        [STAThread]
        static int Main()
        {
            Console.WriteLine("=== GIF RECORDER PROBE ===");
            Console.WriteLine();

            _dir = Path.Combine(Path.GetTempPath(), "screenpen_gifrec_probe");
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            Directory.CreateDirectory(_dir);

            // A region on the primary screen, away from the taskbar.
            Rectangle vs = Native.VirtualScreen();
            Rectangle region = new Rectangle(vs.X + 40, vs.Y + 40, 240, 160);

            Settings cfg = new Settings();
            cfg.ScreenshotFolder = _dir;
            cfg.GifFps = 10;
            cfg.GifMaxSeconds = 30;
            cfg.GifLoop = true;
            cfg.GifSkipIdenticalFrames = true;
            cfg.GifShowBorder = true;
            cfg.GifCountdownSeconds = 0;      // start immediately

            // ---------------------------------------------------------------
            Console.WriteLine("-- basic recording --");
            GifRecorder rec = new GifRecorder(cfg);
            Check(!rec.IsRecording, "a fresh recorder is idle", "ok");

            int ticks = 0;
            bool finished = false;
            rec.Tick += delegate { ticks++; };
            rec.Finished += delegate { finished = true; };

            rec.Start(region, null);
            Check(rec.IsRecording, "Start begins sampling", "recording=" + rec.IsRecording);

            // A static desktop legitimately collapses to one frame, so drive
            // visible change inside the recorded region while sampling.
            using (Form mover = new Form())
            {
                mover.FormBorderStyle = FormBorderStyle.None;
                mover.ShowInTaskbar = false;
                mover.StartPosition = FormStartPosition.Manual;
                mover.Bounds = new Rectangle(region.X + 10, region.Y + 10, 90, 70);
                mover.BackColor = Color.White;
                mover.TopMost = true;
                mover.Show();

                int end = Environment.TickCount + 1200;
                int step = 0;
                while (Environment.TickCount < end)
                {
                    mover.Left = region.X + 10 + ((step++ * 17) % 120);
                    mover.Top = region.Y + 10 + ((step * 7) % 60);
                    mover.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                }
                mover.Hide();
            }
            Pump(150);

            string path = rec.Stop(null);
            Pump(100);

            Check(finished, "Finished fires when recording stops", "ok");
            Check(!rec.IsRecording, "the recorder is idle after Stop", "ok");
            Check(rec.FrameCount > 0, "frames were captured",
                  "frames=" + rec.FrameCount + " grabs=" + (ticks) +
                  " grabErrors=" + rec.GrabErrors +
                  " firstError=" + (rec.FirstGrabError ?? "<none>"));
            Check(ticks > 0, "Tick fires for stored frames", "ticks=" + ticks);
            Check(path != null, "Stop returns the written path", path == null ? "null" : path);

            if (path != null)
            {
                Check(File.Exists(path), "the GIF file exists", path);
                Check(path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase),
                      "the output has a .gif extension", Path.GetExtension(path));
                Check(string.Equals(Path.GetDirectoryName(path), _dir,
                      StringComparison.OrdinalIgnoreCase),
                      "the output uses the configured folder", Path.GetDirectoryName(path));

                List<Bitmap> frames = DecodeFrames(path);
                Check(frames.Count > 1, "the written GIF has multiple frames",
                      "frames=" + frames.Count);
                Check(frames.Count == rec.FrameCount,
                      "the frame count matches what was captured",
                      frames.Count + " decoded vs " + rec.FrameCount + " captured");
                if (frames.Count > 0)
                {
                    Check(frames[0].Width == region.Width && frames[0].Height == region.Height,
                          "frames are the size of the recorded region",
                          frames[0].Width + "x" + frames[0].Height +
                          " vs region " + region.Width + "x" + region.Height);

                    // The red chrome border is (235,64,52). If it leaked into a
                    // frame it would appear as a dense run of that colour along
                    // an edge. Sample the outermost pixels.
                    int redEdge = 0, sampled = 0;
                    foreach (Bitmap f in frames)
                    {
                        for (int x = 0; x < f.Width; x += 3)
                        {
                            sampled++;
                            Color a = f.GetPixel(x, 0);
                            Color b = f.GetPixel(x, f.Height - 1);
                            if (IsChromeRed(a)) redEdge++;
                            if (IsChromeRed(b)) redEdge++;
                        }
                    }
                    Check(redEdge * 100 < sampled,
                          "the recording border is not baked into the frames",
                          redEdge + " chrome-red edge samples of " + sampled * 2);
                }
                foreach (Bitmap f in frames) f.Dispose();
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- max duration cap --");
            {
                Settings cap = new Settings();
                cap.ScreenshotFolder = _dir;
                cap.GifFps = 10;
                cap.GifMaxSeconds = 1;
                cap.GifShowBorder = false;
                cap.GifCountdownSeconds = 0;   // otherwise the default countdown eats the window

                GifRecorder r2 = new GifRecorder(cap);
                r2.Start(region, null);
                Check(r2.IsRecording, "the capped recorder is sampling before the cap",
                      "recording=" + r2.IsRecording);

                // Wait past the cap while driving visible change, so the timer
                // really fires and the elapsed clock advances.
                using (Form mover = new Form())
                {
                    mover.FormBorderStyle = FormBorderStyle.None;
                    mover.ShowInTaskbar = false;
                    mover.StartPosition = FormStartPosition.Manual;
                    mover.Bounds = new Rectangle(region.X + 10, region.Y + 10, 90, 70);
                    mover.BackColor = Color.White;
                    mover.TopMost = true;
                    mover.Show();

                    int end = Environment.TickCount + 2200;
                    int step = 0;
                    while (Environment.TickCount < end)
                    {
                        mover.Left = region.X + 10 + ((step++ * 19) % 120);
                        mover.Refresh();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(20);
                    }
                    mover.Hide();
                }

                Check(!r2.IsRecording, "the max-duration cap stops recording on its own",
                      "recording=" + r2.IsRecording + " elapsed=" + r2.ElapsedSeconds + "s" +
                      " frames=" + r2.FrameCount);
                Check(r2.StopReason != null, "a stop reason is recorded for the cap",
                      r2.StopReason ?? "<null>");
                Check(r2.ElapsedSeconds >= 1 && r2.ElapsedSeconds <= 4,
                      "the cap stops near the configured limit",
                      "elapsed=" + r2.ElapsedSeconds + "s");
                string p2 = r2.LastFile;
                Check(p2 != null && File.Exists(p2), "the capped recording still wrote a file",
                      p2 == null ? "null" : Path.GetFileName(p2));
                r2.Dispose();
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- cancel --");
            {
                string[] before = Directory.GetFiles(_dir, "*.gif");
                GifRecorder r3 = new GifRecorder(cfg);
                r3.Start(region, null);
                Pump(500);
                r3.Cancel();
                Pump(100);
                Check(!r3.IsRecording, "Cancel stops the recorder", "ok");
                Check(r3.LastFile == null, "Cancel writes no file", "lastFile=" + (r3.LastFile ?? "null"));
                string[] after = Directory.GetFiles(_dir, "*.gif");
                Check(after.Length == before.Length, "Cancel leaves the folder unchanged",
                      before.Length + " -> " + after.Length);
                r3.Dispose();
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- countdown delays sampling --");
            {
                Settings c2 = new Settings();
                c2.ScreenshotFolder = _dir;
                c2.GifFps = 5;
                c2.GifShowBorder = false;
                c2.GifCountdownSeconds = 2;
                Check(c2.GifCountdownSeconds == 2, "the countdown is configurable",
                      "seconds=" + c2.GifCountdownSeconds);
                GifRecorder r4 = new GifRecorder(c2);
                r4.Start(region, null);
                Check(r4.IsCountingDown, "a countdown blocks sampling until it finishes",
                      "counting=" + r4.IsCountingDown + " recording=" + r4.IsRecording);
                Check(!r4.IsRecording, "no frames are sampled during the countdown",
                      "frames=" + r4.FrameCount);
                Pump(2500);
                Check(!r4.IsCountingDown, "the countdown finishes", "ok");
                Check(r4.IsRecording, "sampling starts after the countdown",
                      "recording=" + r4.IsRecording);
                r4.Cancel();
                r4.Dispose();
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- tiny region is refused --");
            {
                Settings c3 = new Settings();
                c3.ScreenshotFolder = _dir;
                GifRecorder r5 = new GifRecorder(c3);
                r5.Start(new Rectangle(vs.X + 5, vs.Y + 5, 1, 1), null);
                Check(!r5.IsRecording, "a 1x1 region is refused", "ok");
                Check(r5.StopReason != null, "the refusal explains itself", r5.StopReason);
                r5.Dispose();
            }

            try { Directory.Delete(_dir, true); } catch { }

            Console.WriteLine();
            Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
            return _fail == 0 ? 0 : 1;
        }

        static bool IsChromeRed(Color c)
        {
            return c.R > 200 && c.G < 110 && c.B < 110;
        }
    }
}
