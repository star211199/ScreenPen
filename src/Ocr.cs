using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ScreenPen
{
    /// <summary>A single recognised word and where it was found.</summary>
    public sealed class OcrWord
    {
        public string Text = "";
        /// <summary>Bounding box in the ORIGINAL captured image's coordinates.</summary>
        public Rectangle Bounds;
    }

    /// <summary>A recognised line: its text, its box, and its words.</summary>
    public sealed class OcrLine
    {
        public string Text = "";
        public Rectangle Bounds;
        public List<OcrWord> Words = new List<OcrWord>();
    }

    /// <summary>The outcome of one recognition.</summary>
    public sealed class OcrResult
    {
        /// <summary>The full text, joined according to the caller's settings.</summary>
        public string Text = "";
        /// <summary>
        /// The engine's own text, before any line/word parsing. Kept so a
        /// recognition that succeeds but yields no usable lines can be told apart
        /// from one that genuinely found nothing.
        /// </summary>
        public string RawText = "";
        public List<OcrLine> Lines = new List<OcrLine>();
        public string LanguageTag = "";
        public int ElapsedMs;
        /// <summary>Null on success; a human-readable reason otherwise.</summary>
        public string Error;
        /// <summary>Scale factor that was applied before recognition (1 = none).</summary>
        public float Upscale = 1f;

        public bool Ok { get { return Error == null; } }
    }

    /// <summary>
    /// Optical character recognition using the OCR engine built into Windows
    /// (<c>Windows.Media.Ocr</c>), reached entirely through late-bound reflection.
    ///
    /// <para>
    /// This is what lets ScreenPen gain OCR while keeping its two defining
    /// promises: a single self-contained EXE, and no network access. Tesseract
    /// would mean shipping a native DLL plus several megabytes of language data,
    /// and any cloud OCR would mean uploading the user's screen - unacceptable for
    /// a local screenshot tool.
    /// </para>
    /// <para>
    /// <b>Why reflection.</b> The OCR types live in Windows Runtime metadata
    /// (<c>Windows.winmd</c>), which only exists with the Windows SDK installed.
    /// This machine has no SDK, so the types cannot be referenced at compile time.
    /// <c>Type.GetType(name + ", Windows.Foundation, ContentType=WindowsRuntime")</c>
    /// resolves them at run time instead. Verified working on this machine's .NET
    /// Framework 4.8 runtime by <c>build\ocr_probe2.cs</c> (9/9).
    /// </para>
    /// <para>
    /// <b>Why the awkward generic cast.</b> A WinRT async call returns a raw
    /// <c>System.__ComObject</c> whose <c>GetInterfaces()</c> is empty, so
    /// <c>Status</c>/<c>GetResults</c> are unreachable by reflection. Casting
    /// through <see cref="CastTo{T}"/> instantiated with the call's own projected
    /// return type makes the JIT emit a <c>castclass</c>, which is a real
    /// QueryInterface, yielding a properly typed RCW. The first version of the
    /// probe failed at exactly this point.
    /// </para>
    /// </summary>
    public static class Ocr
    {
        // Windows' OCR is poor below this and OcrEngine refuses very small
        // images outright, so small regions are upscaled before recognition.
        const int MinUsefulDimension = 100;
        const int AbsoluteMinDimension = 40;
        const int DefaultMaxDimension = 10000;

        static Type _engineT;
        static bool _probed;
        static string _probeDetail = "";

        // =====================================================================
        //  WinRT plumbing
        // =====================================================================

        static Type WinRT(string typeName)
        {
            string[] asms = new string[]
            {
                "Windows.Foundation", "Windows.Storage", "Windows.Graphics",
                "Windows.Globalization", "Windows.Media"
            };
            foreach (string a in asms)
            {
                Type t = Type.GetType(typeName + ", " + a + ", ContentType=WindowsRuntime");
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Runtime-instantiated generic cast: the JIT emits a <c>castclass</c> to
        /// <c>T</c>, which for a WinRT interface performs a QueryInterface. This
        /// turns a bare <c>__ComObject</c> into something reflectable.
        /// </summary>
        static T CastTo<T>(object o) { return (T)o; }

        static object Cast(object o, Type t)
        {
            if (o == null) return null;
            if (t.IsInstanceOfType(o)) return o;
            MethodInfo mi = typeof(Ocr).GetMethod("CastTo",
                BindingFlags.NonPublic | BindingFlags.Static);
            return mi.MakeGenericMethod(t).Invoke(null, new object[] { o });
        }

        /// <summary>
        /// Waits for a WinRT <c>IAsyncOperation&lt;T&gt;</c>. <paramref name="ifaceT"/>
        /// is the closed projected interface type taken from the call's
        /// <c>ReturnType</c>, which is what makes <c>GetResults</c> findable.
        /// </summary>
        static object AwaitOp(object op, Type ifaceT, string label)
        {
            if (op == null) throw new Exception(label + ": null op");
            if (ifaceT == null) throw new Exception(label + ": no interface type");

            Type iasyncInfo = WinRT("Windows.Foundation.IAsyncInfo");
            PropertyInfo statusP = null;
            foreach (Type cand in new Type[] { ifaceT, iasyncInfo })
            {
                if (cand == null) continue;
                statusP = cand.GetProperty("Status");
                if (statusP != null) break;
            }
            if (statusP == null)
                foreach (Type i in ifaceT.GetInterfaces())
                {
                    statusP = i.GetProperty("Status");
                    if (statusP != null) break;
                }
            if (statusP == null) throw new Exception(label + ": cannot find Status");

            MethodInfo getResults = ifaceT.GetMethod("GetResults");

            // Bounded poll: the probe used 9000 x 10ms = 90s, which is far longer
            // than any real recognition needs. 20s is generous and cannot hang the
            // worker thread forever.
            for (int i = 0; i < 2000; i++)
            {
                int s = Convert.ToInt32(statusP.GetValue(op, null), CultureInfo.InvariantCulture);
                if (s == 1) return getResults == null ? null : getResults.Invoke(op, null);
                if (s == 2) throw new Exception(label + ": 已取消");
                if (s == 3)
                {
                    PropertyInfo err = iasyncInfo == null ? null : iasyncInfo.GetProperty("ErrorCode");
                    throw new Exception(label + ": 失败 hr=" +
                        (err != null ? Convert.ToString(err.GetValue(op, null), CultureInfo.InvariantCulture) : "?"));
                }
                Thread.Sleep(10);
            }
            throw new Exception(label + ": 超时");
        }

        /// <summary>Invokes a method and awaits it when it returns an async operation.</summary>
        static object Call(object target, MethodInfo m, object[] args, string label)
        {
            object r = m.Invoke(target, args);
            Type rt = m.ReturnType;
            if (rt != null && rt.Name.StartsWith("IAsyncOperation", StringComparison.Ordinal))
                return AwaitOp(r, rt, label);
            return r;
        }

        /// <summary>
        /// Finds a method by name and argument count. Matching on the name alone
        /// is not enough: several WinRT types have overloads, and
        /// <c>GetMethod</c> would throw <c>AmbiguousMatchException</c>.
        /// </summary>
        static MethodInfo Method(object target, string name, int argc)
        {
            foreach (MethodInfo m in target.GetType().GetMethods())
                if (m.Name == name && m.GetParameters().Length == argc) return m;
            throw new Exception("找不到方法 " + name + "/" + argc + " (" + target.GetType().Name + ")");
        }

        static MethodInfo StaticMethod(Type t, string name, int argc)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == name && m.GetParameters().Length == argc) return m;
            throw new Exception("找不到静态方法 " + name + "/" + argc + " (" + t.Name + ")");
        }

        static string StrProp(object o, string name)
        {
            if (o == null) return "";
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p == null) return "";
            object v = p.GetValue(o, null);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // =====================================================================
        //  Availability
        // =====================================================================

        /// <summary>
        /// Reports whether OCR can run here, without doing any recognition.
        /// Cheap enough to call from the settings dialog.
        /// </summary>
        public static bool IsAvailable(out string detail)
        {
            EnsureProbed();
            detail = _probeDetail;
            return _engineT != null && AvailableLanguages().Length > 0;
        }

        static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _engineT = WinRT("Windows.Media.Ocr.OcrEngine");
                if (_engineT == null)
                {
                    _probeDetail = "此系统没有 Windows OCR 引擎（需要 Windows 10 及以上）";
                    return;
                }
                int n = AvailableLanguages().Length;
                _probeDetail = n > 0
                    ? "可用，已安装 " + n + " 种识别语言"
                    : "已找到 OCR 引擎，但没有安装任何识别语言（设置 → 时间和语言 → 语言 → 添加语言，需勾选“可选语言功能”中的光学字符识别）";
            }
            catch (Exception ex)
            {
                _probeDetail = "OCR 探测失败：" + ex.Message;
            }
        }

        /// <summary>BCP-47 tags of every installed recogniser, e.g. "zh-Hans-CN".</summary>
        public static string[] AvailableLanguages()
        {
            var list = new List<string>();
            try
            {
                if (_engineT == null) _engineT = WinRT("Windows.Media.Ocr.OcrEngine");
                if (_engineT == null) return list.ToArray();

                PropertyInfo p = _engineT.GetProperty("AvailableRecognizerLanguages",
                    BindingFlags.Public | BindingFlags.Static);
                if (p == null) return list.ToArray();

                object langs = p.GetValue(null, null);
                if (langs == null) return list.ToArray();
                foreach (object l in (System.Collections.IEnumerable)langs)
                {
                    string tag = StrProp(l, "LanguageTag");
                    if (!string.IsNullOrEmpty(tag)) list.Add(tag);
                }
            }
            catch { }
            return list.ToArray();
        }

        // =====================================================================
        //  Recognition
        // =====================================================================

        /// <summary>
        /// Recognises the text in <paramref name="image"/>. Never throws: failures
        /// come back in <see cref="OcrResult.Error"/> so the caller can show them
        /// in the UI rather than crashing the annotation overlay.
        ///
        /// <para>
        /// This blocks for the duration of recognition (typically 100-600 ms), so
        /// the UI should call <see cref="RecognizeOnWorker"/> instead.
        /// </para>
        /// </summary>
        public static OcrResult Recognize(Bitmap image, Settings cfg)
        {
            OcrResult res = new OcrResult();
            DateTime started = DateTime.UtcNow;
            if (image == null || image.Width < 1 || image.Height < 1)
            {
                res.Error = "没有可识别的图像";
                return res;
            }

            string tmp = null;
            Bitmap prepared = null;
            try
            {
                EnsureProbed();
                if (_engineT == null) { res.Error = _probeDetail; return res; }

                string[] langs = AvailableLanguages();
                if (langs.Length == 0) { res.Error = _probeDetail; return res; }

                object engine = CreateEngine(cfg, langs, res);
                if (engine == null) return res;

                prepared = Prepare(image, cfg, out res.Upscale);

                // The file-based route is used because it is exactly what the
                // feasibility probe validated end to end; it also keeps the
                // reflection surface small.
                tmp = Path.Combine(Path.GetTempPath(),
                    "screenpen_ocr_" + Guid.NewGuid().ToString("N") + ".png");
                prepared.Save(tmp, ImageFormat.Png);

                object softwareBitmap = LoadSoftwareBitmap(tmp);
                if (softwareBitmap == null) { res.Error = "无法解码图像"; return res; }

                MethodInfo rec = Method(engine, "RecognizeAsync", 1);
                object result = Call(engine, rec, new object[] { softwareBitmap }, "RecognizeAsync");
                if (result == null) { res.Error = "识别没有返回结果"; return res; }

                res.RawText = StrProp(result, "Text");

                res.LanguageTag = EngineLanguageTag(engine);

                ReadLines(result, res, res.Upscale, cfg);

                // The engine can return text while the line/word walk yields
                // nothing usable (for example if a projection detail differs on
                // another Windows build). The engine's own text is authoritative,
                // so never report an empty result when it found something.
                if (res.Lines.Count == 0 && !string.IsNullOrEmpty(res.RawText))
                {
                    OcrLine only = new OcrLine();
                    only.Text = res.RawText;
                    res.Lines.Add(only);
                }

                res.Text = JoinLines(res.Lines, cfg);
                if ((res.Text == null || res.Text.Length == 0) &&
                    !string.IsNullOrEmpty(res.RawText))
                    res.Text = res.RawText;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }
            finally
            {
                if (prepared != null) { try { prepared.Dispose(); } catch { } }
                if (tmp != null) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
            res.ElapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            return res;
        }

        /// <summary>
        /// Runs <see cref="Recognize"/> on a dedicated STA thread so the caller's
        /// UI stays responsive. A dedicated thread (rather than the thread pool)
        /// keeps the apartment identical to the one the feasibility probe was
        /// verified under.
        /// </summary>
        public static OcrResult RecognizeOnWorker(Bitmap image, Settings cfg, int timeoutMs)
        {
            OcrResult boxed = null;
            // Copy first: the caller may dispose its bitmap as soon as this
            // returns, and the worker outlives that.
            Bitmap copy = null;
            try { copy = image == null ? null : new Bitmap(image); }
            catch { copy = null; }

            Thread t = new Thread(delegate()
            {
                try { boxed = Recognize(copy, cfg); }
                catch (Exception ex) { boxed = new OcrResult(); boxed.Error = ex.Message; }
            });
            t.IsBackground = true;
            try { t.SetApartmentState(ApartmentState.STA); } catch { }
            t.Start();

            if (!t.Join(timeoutMs <= 0 ? 30000 : timeoutMs))
            {
                OcrResult to = new OcrResult();
                to.Error = "识别超时";
                boxed = to;
            }
            if (copy != null) { try { copy.Dispose(); } catch { } }
            return boxed ?? new OcrResult();
        }

        /// <summary>
        /// Reads <c>OcrEngine.RecognizerLanguage</c>, which is a
        /// <c>Windows.Globalization.Language</c> object rather than a string.
        /// </summary>
        static string EngineLanguageTag(object engine)
        {
            try
            {
                PropertyInfo p = engine.GetType().GetProperty("RecognizerLanguage");
                if (p == null) return "";
                object lang = p.GetValue(engine, null);
                return StrProp(lang, "LanguageTag");
            }
            catch { return ""; }
        }

        static object CreateEngine(Settings cfg, string[] langs, OcrResult res)
        {
            try
            {
                // An explicit language wins when it is actually installed;
                // otherwise fall back to the user's profile languages, which is
                // what the feasibility probe validated.
                string want = cfg == null ? "" : (cfg.OcrLanguage ?? "").Trim();
                if (want.Length > 0)
                {
                    bool installed = false;
                    foreach (string l in langs)
                        if (string.Equals(l, want, StringComparison.OrdinalIgnoreCase)) { installed = true; break; }

                    if (installed)
                    {
                        Type langT = WinRT("Windows.Globalization.Language");
                        if (langT != null)
                        {
                            object lang = Activator.CreateInstance(langT, new object[] { want });
                            MethodInfo from = StaticMethod(_engineT, "TryCreateFromLanguage", 1);
                            object eng = from.Invoke(null, new object[] { lang });
                            if (eng != null) return eng;
                        }
                    }
                }

                MethodInfo m = StaticMethod(_engineT, "TryCreateFromUserProfileLanguages", 0);
                object e = m.Invoke(null, null);
                if (e == null)
                    res.Error = "系统没有可用的 OCR 识别语言，请在 Windows 设置中添加语言的“光学字符识别”功能";
                return e;
            }
            catch (Exception ex)
            {
                res.Error = "创建 OCR 引擎失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Scales small regions up before recognition. Windows' recogniser is
        /// trained on document-sized text and both refuses very small images and
        /// mis-reads small glyphs, so a 60px-tall selection is enlarged rather
        /// than rejected.
        /// </summary>
        static Bitmap Prepare(Bitmap src, Settings cfg, out float scale)
        {
            scale = 1f;
            int maxDim = DefaultMaxDimension;
            try
            {
                PropertyInfo p = _engineT == null ? null
                    : _engineT.GetProperty("MaxImageDimension", BindingFlags.Public | BindingFlags.Static);
                if (p != null) maxDim = Convert.ToInt32(p.GetValue(null, null), CultureInfo.InvariantCulture);
            }
            catch { }

            int up = cfg == null ? 0 : cfg.OcrUpscaleSmall;
            int minSide = Math.Min(src.Width, src.Height);

            // Grow until the short side is comfortable, but never past the
            // configured factor.
            if (up > 0 && minSide < MinUsefulDimension)
            {
                for (int i = 0; i < up; i++)
                {
                    if (Math.Min(src.Width, src.Height) * (scale + 1f) > MinUsefulDimension * 2f) break;
                    scale += 1f;
                    if (minSide * scale >= MinUsefulDimension) break;
                }
            }
            // OcrEngine rejects anything below roughly 40px on a side.
            while (Math.Min(src.Width, src.Height) * scale < AbsoluteMinDimension) scale += 1f;

            // Guard the other end too: a full 4K selection is fine, but a
            // multi-monitor stitched capture can exceed the engine's limit.
            int outW = (int)Math.Round(src.Width * scale);
            int outH = (int)Math.Round(src.Height * scale);
            if (outW > maxDim || outH > maxDim)
            {
                float shrink = Math.Min((float)maxDim / outW, (float)maxDim / outH);
                scale = scale * shrink;
                outW = (int)Math.Round(src.Width * scale);
                outH = (int)Math.Round(src.Height * scale);
            }
            if (outW < 1) outW = 1;
            if (outH < 1) outH = 1;

            if (Math.Abs(scale - 1f) < 0.01f)
            {
                scale = 1f;
                // Still copy: the caller owns the original and may dispose it.
                return new Bitmap(src);
            }

            Bitmap dst = new Bitmap(outW, outH);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(src, new Rectangle(0, 0, outW, outH));
            }
            return dst;
        }

        static object LoadSoftwareBitmap(string path)
        {
            Type fileT = WinRT("Windows.Storage.StorageFile");
            if (fileT == null) throw new Exception("找不到 Windows.Storage.StorageFile");

            MethodInfo getFile = StaticMethod(fileT, "GetFileFromPathAsync", 1);
            object file = Call(null, getFile, new object[] { path }, "GetFileFromPathAsync");
            if (file == null) throw new Exception("无法打开临时图像");

            MethodInfo open = Method(file, "OpenAsync", 1);
            object mode = Enum.Parse(open.GetParameters()[0].ParameterType, "Read");
            object stream = Call(file, open, new object[] { mode }, "OpenAsync");
            if (stream == null) throw new Exception("无法读取临时图像");

            Type decT = WinRT("Windows.Graphics.Imaging.BitmapDecoder");
            if (decT == null) throw new Exception("找不到 BitmapDecoder");
            MethodInfo create = null;
            foreach (MethodInfo m in decT.GetMethods())
                if (m.Name == "CreateAsync" && m.GetParameters().Length == 1) { create = m; break; }
            if (create == null) throw new Exception("找不到 BitmapDecoder.CreateAsync");

            object decoder = Call(null, create, new object[] { stream }, "BitmapDecoder.CreateAsync");
            if (decoder == null) throw new Exception("无法解码图像");

            MethodInfo getSbm = Method(decoder, "GetSoftwareBitmapAsync", 0);
            object sbm = Call(decoder, getSbm, new object[0], "GetSoftwareBitmapAsync");
            if (sbm == null) throw new Exception("无法转换为 SoftwareBitmap");
            return sbm;
        }

        /// <summary>
        /// Reads the result's lines and words. Bounding boxes come back in the
        /// coordinates of the image that was actually recognised, so they are
        /// divided by the upscale factor to land back on the caller's bitmap.
        /// </summary>
        static void ReadLines(object result, OcrResult res, float scale, Settings cfg)
        {
            object linesObj = null;
            try
            {
                PropertyInfo lp = result.GetType().GetProperty("Lines");
                if (lp != null) linesObj = lp.GetValue(result, null);
            }
            catch (Exception ex)
            {
                // Surface this rather than returning an empty result: a failure
                // here looks exactly like "no text found", which would silently
                // hide a broken recognition path from the user.
                res.Error = "读取识别结果失败：" + ex.Message;
                return;
            }

            if (linesObj == null)
            {
                res.Error = "识别结果中没有文本行（结果类型 " + result.GetType().Name + "）";
                return;
            }

            var enumerable = linesObj as System.Collections.IEnumerable;
            if (enumerable == null)
            {
                res.Error = "识别结果不是可枚举的文本行集合";
                return;
            }

            foreach (object line in enumerable)
            {
                OcrLine ol = new OcrLine();
                ol.Text = StrProp(line, "Text");

                Rectangle lineBox = Rectangle.Empty;
                bool haveLineBox = false;

                PropertyInfo wp = line.GetType().GetProperty("Words");
                object wordsObj = wp == null ? null : wp.GetValue(line, null);
                var words = wordsObj as System.Collections.IEnumerable;
                if (words != null)
                {
                    foreach (object w in words)
                    {
                        OcrWord ow = new OcrWord();
                        ow.Text = StrProp(w, "Text");

                        PropertyInfo bp = w.GetType().GetProperty("BoundingRect");
                        if (bp != null)
                        {
                            object rect = bp.GetValue(w, null);
                            if (rect != null)
                            {
                                Rectangle r = ToRect(rect, scale);
                                ow.Bounds = r;
                                lineBox = haveLineBox ? Rectangle.Union(lineBox, r) : r;
                                haveLineBox = true;
                            }
                        }
                        ol.Words.Add(ow);
                    }
                }

                ol.Bounds = haveLineBox ? lineBox : Rectangle.Empty;
                if (ol.Text.Length > 0 || ol.Words.Count > 0) res.Lines.Add(ol);
            }
        }

        static Rectangle ToRect(object winrtRect, float scale)
        {
            double x = NumProp(winrtRect, "X");
            double y = NumProp(winrtRect, "Y");
            double w = NumProp(winrtRect, "Width");
            double h = NumProp(winrtRect, "Height");
            if (scale <= 0f) scale = 1f;
            return new Rectangle(
                (int)Math.Round(x / scale),
                (int)Math.Round(y / scale),
                Math.Max(1, (int)Math.Round(w / scale)),
                Math.Max(1, (int)Math.Round(h / scale)));
        }

        static double NumProp(object o, string name)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name);
                if (p == null) return 0;
                return Convert.ToDouble(p.GetValue(o, null), CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        static string JoinLines(List<OcrLine> lines, Settings cfg)
        {
            bool keep = cfg == null || cfg.OcrKeepLineBreaks;
            var parts = new List<string>();
            foreach (OcrLine l in lines)
            {
                string s = l.Text == null ? "" : l.Text.Trim();
                if (s.Length == 0) continue;
                parts.Add(s);
            }
            return string.Join(keep ? "\r\n" : " ", parts.ToArray());
        }
    }
}
