using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ScreenPen
{
    /// <summary>
    /// Writes animated GIFs using the WPF imaging stack that ships with the .NET
    /// Framework, so the application keeps its "no external dependency" promise
    /// (no FFmpeg, no gifski - gifski is AGPL-3.0 and would force a licence
    /// change on the whole project).
    ///
    /// <para>
    /// <b>Why this is not just <c>new GifBitmapEncoder()</c>.</b>
    /// <c>GifBitmapEncoder</c> was measured to silently discard per-frame delay
    /// metadata: three frames written with delays 5/40/15 came back as 0/0/0
    /// (<c>build\gif_delay_probe.cs</c>). It also emits no NETSCAPE2.0
    /// application extension, so the result plays once and stops. A GIF built the
    /// obvious way would have no timing at all and no looping - the old
    /// <c>gif_probe.cs</c> missed this because it only asserted the frame count.
    /// </para>
    /// <para>
    /// The encoder's byte output is nevertheless usable, and
    /// <c>build\gif_delta_probe.cs</c> established the two facts that make
    /// post-processing safe: every frame is stored full-size (the encoder does
    /// <i>not</i> delta-compress against the previous frame, so no transparency
    /// is load-bearing and clearing or ignoring it cannot corrupt colours), and
    /// the block structure is fully walkable, so the 2-byte delay field of each
    /// Graphic Control Extension can be patched in place. <see cref="PatchDelays"/>
    /// does that, and <see cref="PatchLoop"/> inserts the loop block.
    /// </para>
    /// </summary>
    public static class Gif
    {
        const byte BLOCK_EXTENSION = 0x21;
        const byte BLOCK_IMAGE = 0x2C;
        const byte BLOCK_TRAILER = 0x3B;
        const byte EXT_GRAPHIC_CONTROL = 0xF9;

        // =====================================================================
        //  Frame capture
        // =====================================================================

        /// <summary>Encodes a frame as PNG bytes; the recorder's unit of storage.</summary>
        public static byte[] ToPngBytes(Bitmap frame)
        {
            if (frame == null) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    frame.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// True when two frames are pixel-identical, compared as raw scanlines.
        ///
        /// <para>
        /// A 1280x720 frame is nearly a million pixels, so a GetPixel loop is far
        /// too slow to run between captured frames; LockBits plus a byte compare
        /// is the same trick ShareX uses for its scrolling capture. The recorder
        /// normally takes the cheaper PNG-bytes route (see
        /// <see cref="GifBuilder.Add"/>), and this is the reference
        /// implementation that proves byte equality really does imply pixel
        /// equality.
        /// </para>
        /// </summary>
        public static bool FramesIdentical(Bitmap a, Bitmap b)
        {
            if (a == null || b == null) return false;
            if (a.Width != b.Width || a.Height != b.Height) return false;

            BitmapData da = null, db = null;
            try
            {
                Rectangle r = new Rectangle(0, 0, a.Width, a.Height);
                da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int lenA = Math.Abs(da.Stride) * a.Height;
                int lenB = Math.Abs(db.Stride) * b.Height;
                if (lenA != lenB) return false;

                const int Chunk = 16384;
                byte[] bufA = new byte[Chunk];
                byte[] bufB = new byte[Chunk];
                int offset = 0;
                while (offset < lenA)
                {
                    int n = Math.Min(Chunk, lenA - offset);
                    Marshal.Copy(new IntPtr(da.Scan0.ToInt64() + offset), bufA, 0, n);
                    Marshal.Copy(new IntPtr(db.Scan0.ToInt64() + offset), bufB, 0, n);
                    for (int i = 0; i < n; i++)
                        if (bufA[i] != bufB[i]) return false;
                    offset += n;
                }
                return true;
            }
            catch
            {
                // Never silently drop a frame on a comparison failure: report
                // "different" so the frame is kept.
                return false;
            }
            finally
            {
                if (da != null) { try { a.UnlockBits(da); } catch { } }
                if (db != null) { try { b.UnlockBits(db); } catch { } }
            }
        }

        // =====================================================================
        //  Encoding
        // =====================================================================

        /// <summary>
        /// Encodes PNG-encoded frames to a GIF file. Returns null on success or a
        /// human-readable reason on failure, so the caller can surface it.
        /// </summary>
        /// <param name="pngFrames">One PNG blob per stored frame, in play order.</param>
        /// <param name="delays">Per-frame delay in 1/100 s; same length as pngFrames.</param>
        /// <param name="path">Destination file.</param>
        /// <param name="loop">Loop forever rather than playing once.</param>
        public static string Encode(IList<byte[]> pngFrames, IList<int> delays,
                                   string path, bool loop)
        {
            if (pngFrames == null || pngFrames.Count == 0) return "没有可写入的帧";
            if (string.IsNullOrEmpty(path)) return "输出路径为空";

            string tmp = null;
            try
            {
                GifBitmapEncoder enc = new GifBitmapEncoder();
                for (int i = 0; i < pngFrames.Count; i++)
                {
                    byte[] png = pngFrames[i];
                    if (png == null) continue;

                    using (MemoryStream ms = new MemoryStream(png))
                    {
                        BitmapDecoder dec = BitmapDecoder.Create(ms,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);
                        BitmapFrame bf = dec.Frames[0];

                        // Set the delay here for intent, then fix the file up
                        // afterwards: the encoder drops this value.
                        int cs = DelayFor(delays, i);
                        BitmapMetadata md = new BitmapMetadata("gif");
                        md.SetQuery("/grctlext/Delay", (ushort)cs);

                        enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts));
                    }
                }

                if (enc.Frames.Count == 0) return "没有可写入的帧";

                // Encode to memory first: the delay/loop patch rewrites bytes, and
                // a file still being written cannot be patched.
                byte[] bytes;
                using (MemoryStream outMs = new MemoryStream())
                {
                    enc.Save(outMs);
                    bytes = outMs.ToArray();
                }

                int patched = PatchDelays(bytes, delays);
                if (patched == 0) return "无法写入帧延时（GIF 结构未识别）";

                if (loop) bytes = PatchLoop(bytes);

                // Write beside the destination so the final rename stays on the
                // same volume. A failed write/replace leaves any previous GIF
                // intact instead of truncating it into a corrupt partial file.
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew,
                                                       FileAccess.Write, FileShare.None))
                    fs.Write(bytes, 0, bytes.Length);

                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                tmp = null;
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (tmp != null)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                }
            }
        }

        static int DelayFor(IList<int> delays, int i)
        {
            int cs = (delays != null && i < delays.Count) ? delays[i] : 10;
            if (cs < 1) cs = 1;
            if (cs > 65535) cs = 65535;
            return cs;
        }

        /// <summary>
        /// Walks the GIF block structure and writes each frame's delay into its
        /// Graphic Control Extension. Returns how many delays were written; zero
        /// means the structure was not recognised and the file must not be
        /// trusted, so <see cref="Encode"/> treats it as a failure rather than
        /// shipping a GIF with no timing.
        ///
        /// <para>
        /// The walk mirrors the one validated in <c>build\gif_dump.cs</c>: skip
        /// the logical screen descriptor and the optional global colour table,
        /// then step block by block. Image data is skipped by following the LZW
        /// sub-block chain (length-prefixed, terminated by a zero byte), which is
        /// what makes this robust against the compressed payload containing bytes
        /// that happen to look like block introducers.
        /// </para>
        /// </summary>
        internal static int PatchDelays(byte[] bytes, IList<int> delays)
        {
            if (bytes == null || bytes.Length < 14) return 0;

            // 6-byte signature + 7-byte logical screen descriptor = 13; the
            // global colour table, if flagged, follows immediately.
            int p = 13;
            if ((bytes[10] & 0x80) != 0)                // global colour table?
                p += (2 << (bytes[10] & 0x07)) * 3;

            int frameIndex = 0;
            int written = 0;
            while (p < bytes.Length)
            {
                byte blk = bytes[p];

                if (blk == BLOCK_TRAILER) break;

                if (blk == BLOCK_EXTENSION)
                {
                    if (p + 1 >= bytes.Length) break;
                    byte label = bytes[p + 1];

                    if (label == EXT_GRAPHIC_CONTROL)
                    {
                        // 0x21 0xF9 <size=4> <flags> <delay lo> <delay hi> <tidx> 0x00
                        if (p + 7 >= bytes.Length) break;
                        int cs = DelayFor(delays, frameIndex);
                        bytes[p + 4] = (byte)(cs & 0xFF);
                        bytes[p + 5] = (byte)((cs >> 8) & 0xFF);
                        written++;
                        p += 3 + bytes[p + 2];          // 0x21, 0xF9, size, data
                        if (p < bytes.Length && bytes[p] == 0) p++;   // terminator
                    }
                    else
                    {
                        p += 2;
                        p = SkipSubBlocks(bytes, p);
                    }
                }
                else if (blk == BLOCK_IMAGE)
                {
                    if (p + 9 >= bytes.Length) break;
                    byte ip = bytes[p + 9];
                    frameIndex++;
                    p += 10;
                    if ((ip & 0x80) != 0)               // local colour table?
                        p += (2 << (ip & 0x07)) * 3;
                    p++;                                // LZW minimum code size
                    p = SkipSubBlocks(bytes, p);
                }
                else
                {
                    // Unknown block: stop rather than guess at offsets and risk
                    // corrupting the image data.
                    break;
                }
            }
            return written;
        }

        /// <summary>Steps over a chain of length-prefixed sub-blocks.</summary>
        static int SkipSubBlocks(byte[] bytes, int p)
        {
            while (p < bytes.Length)
            {
                int n = bytes[p];
                p++;
                if (n == 0) break;
                p += n;
            }
            return p;
        }

        /// <summary>
        /// Inserts the NETSCAPE2.0 application extension that makes the GIF loop
        /// forever. It belongs immediately after the global colour table, before
        /// the first frame.
        /// </summary>
        internal static byte[] PatchLoop(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 14) return bytes;

            // The loop block belongs immediately after the LSD (+GCT), before
            // the first frame: 6 signature + 7 descriptor = 13.
            int at = 13;
            if ((bytes[10] & 0x80) != 0)
                at += (2 << (bytes[10] & 0x07)) * 3;

            // 0x21 0xFF 0x0B "NETSCAPE2.0" 0x03 0x01 <loop lo> <loop hi> 0x00
            byte[] loop = new byte[]
            {
                0x21, 0xFF, 0x0B,
                (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C',
                (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
                0x03, 0x01, 0x00, 0x00, 0x00
            };

            byte[] outb = new byte[bytes.Length + loop.Length];
            Array.Copy(bytes, 0, outb, 0, at);
            Array.Copy(loop, 0, outb, at, loop.Length);
            Array.Copy(bytes, at, outb, at + loop.Length, bytes.Length - at);
            return outb;
        }
    }

    /// <summary>
    /// Accumulates captured frames for one recording.
    ///
    /// <para>
    /// Frames are stored as compressed PNG blobs rather than as <c>Bitmap</c>
    /// objects. A 30-second recording at 10 fps is 300 frames, and 300 live
    /// 1280x720 bitmaps would be roughly 1.1 GB; as PNGs the same recording is a
    /// few tens of megabytes. Only the current and previous frames are ever held
    /// decoded.
    /// </para>
    /// </summary>
    public sealed class GifBuilder
    {
        readonly List<byte[]> _pngs = new List<byte[]>();
        readonly List<int> _delays = new List<int>();
        readonly int _tickCs;
        readonly bool _skipIdentical;
        readonly int _maxDelayCs;

        byte[] _lastPng;
        int _collapsed;

        /// <summary>
        /// Caps the delay a single stored frame may carry. When the screen has
        /// been static for longer than this, a duplicate frame is stored instead
        /// so the recording keeps real-time pacing instead of drifting.
        /// </summary>
        public const int MaxFrameDelayCs = 1000;    // 10 seconds

        /// <summary>Refuses to grow without bound if a caller never stops.</summary>
        public const int MaxStoredFrames = 4000;

        public GifBuilder(int fps, bool skipIdenticalFrames)
        {
            if (fps < 1) fps = 1;
            if (fps > 50) fps = 50;
            _tickCs = Math.Max(1, (int)Math.Round(100.0 / fps));
            _skipIdentical = skipIdenticalFrames;
            _maxDelayCs = MaxFrameDelayCs;
        }

        /// <summary>Number of frames that will actually be written.</summary>
        public int FrameCount { get { return _pngs.Count; } }

        /// <summary>How many samples were folded into an earlier frame's delay.</summary>
        public int CollapsedFrames { get { return _collapsed; } }

        /// <summary>Delay assigned to each stored frame, in 1/100 s.</summary>
        public int[] Delays { get { return _delays.ToArray(); } }

        public bool IsFull { get { return _pngs.Count >= MaxStoredFrames; } }

        /// <summary>
        /// Offers a frame to the recording. Returns true when it was stored as a
        /// new frame and false when it was folded into the previous frame's delay
        /// (or could not be encoded).
        /// </summary>
        public bool Add(Bitmap frame)
        {
            byte[] png = Gif.ToPngBytes(frame);
            if (png == null) return false;
            if (_pngs.Count >= MaxStoredFrames) return false;

            if (_skipIdentical && _lastPng != null && SameBytes(_lastPng, png))
            {
                int last = _delays.Count - 1;
                // Extend the previous frame's on-screen time rather than storing
                // a byte-identical copy. Once the cap is reached a real duplicate
                // is stored so playback timing does not drift.
                if (last >= 0 && _delays[last] + _tickCs <= _maxDelayCs)
                {
                    _delays[last] = _delays[last] + _tickCs;
                    _collapsed++;
                    return false;
                }
            }

            _pngs.Add(png);
            _delays.Add(_tickCs);
            _lastPng = png;
            return true;
        }

        /// <summary>Writes the accumulated recording. Null on success.</summary>
        public string Save(string path, bool loop)
        {
            return Gif.Encode(_pngs, _delays, path, loop);
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
