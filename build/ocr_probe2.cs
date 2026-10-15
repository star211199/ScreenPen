// Feasibility probe v2: OCR with ZERO external dependencies from a plain
// .NET Framework 4.0 csc.exe build.
//
// v1 resolved the OCR engine fine but failed at the first async WinRT call:
// MethodInfo.Invoke on a WinRT object hands back a raw System.__ComObject whose
// GetInterfaces() is empty, so Status/GetResults were unreachable.
//
// Fix: we know each call's *projected interface type* from the MethodInfo's
// ReturnType (e.g. IAsyncOperation<StorageFile>). A generic cast helper
// instantiated at run time with that type makes the CLR perform a real QI, so
// we get a properly typed RCW we can reflect over.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;

static class OcrProbe2
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    static Type WinRT(string typeName)
    {
        string[] asms = new string[] { "Windows.Foundation", "Windows.Storage", "Windows.Graphics", "Windows.Globalization", "Windows.Media" };
        foreach (string a in asms)
        {
            Type t = Type.GetType(typeName + ", " + a + ", ContentType=WindowsRuntime");
            if (t != null) return t;
        }
        return null;
    }

    // Runtime-instantiated generic cast. The JIT emits a castclass to T, which
    // for a WinRT interface means a QueryInterface. This is what turns a bare
    // __ComObject into something reflectable.
    static T CastTo<T>(object o) { return (T)o; }

    static object Cast(object o, Type t)
    {
        if (o == null) return null;
        if (t.IsInstanceOfType(o)) return o;
        MethodInfo mi = typeof(OcrProbe2).GetMethod("CastTo", BindingFlags.NonPublic | BindingFlags.Static);
        return mi.MakeGenericMethod(t).Invoke(null, new object[] { o });
    }

    // Await a WinRT IAsyncOperation<T>: op is the raw result, ifaceT is the
    // closed projected interface type from the call's ReturnType.
    static object AwaitOp(object op, Type ifaceT, string label)
    {
        if (op == null) throw new Exception(label + ": null op");

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
        for (int i = 0; i < 9000; i++)
        {
            int s = Convert.ToInt32(statusP.GetValue(op, null));
            if (s == 1) return getResults == null ? null : getResults.Invoke(op, null);
            if (s == 2) throw new Exception(label + ": cancelled");
            if (s == 3)
            {
                PropertyInfo err = null;
                if (iasyncInfo != null) err = iasyncInfo.GetProperty("ErrorCode");
                throw new Exception(label + ": error hr=" + (err != null ? Convert.ToString(err.GetValue(op, null)) : "?"));
            }
            Thread.Sleep(10);
        }
        throw new Exception(label + ": timed out");
    }

    // Invoke a method and, if it returns an IAsyncOperation, wait for it.
    static object Call(object target, MethodInfo m, object[] args, string label)
    {
        object r = m.Invoke(target, args);
        Type rt = m.ReturnType;
        if (rt != null && rt.Name.StartsWith("IAsyncOperation")) return AwaitOp(r, rt, label);
        return r;
    }

    static MethodInfo Method(object target, string name, int argc)
    {
        foreach (MethodInfo m in target.GetType().GetMethods())
            if (m.Name == name && m.GetParameters().Length == argc) return m;
        throw new Exception("no method " + name + "/" + argc + " on " + target.GetType().Name);
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("=== OCR FEASIBILITY PROBE v2 (no SDK, no NuGet, no Tesseract) ===");
        Console.WriteLine("runtime = " + Environment.Version + " / " + IntPtr.Size * 8 + "-bit");
        Console.WriteLine();

        Type engineT = WinRT("Windows.Media.Ocr.OcrEngine");
        Check(engineT != null, "resolve OcrEngine via ContentType=WindowsRuntime", engineT == null ? "NOT FOUND" : "ok");
        if (engineT == null) { Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ==="); return 1; }

        object langs = null;
        try { langs = engineT.GetProperty("AvailableRecognizerLanguages", BindingFlags.Public | BindingFlags.Static).GetValue(null, null); }
        catch (Exception ex) { Check(false, "AvailableRecognizerLanguages", ex.Message); }
        int n = 0; string tags = "";
        if (langs != null)
            foreach (object l in (System.Collections.IEnumerable)langs)
            {
                n++;
                PropertyInfo lt = l.GetType().GetProperty("LanguageTag");
                tags += (tags.Length > 0 ? ", " : "") + (lt != null ? Convert.ToString(lt.GetValue(l, null)) : "?");
            }
        Check(n > 0, "an OCR language is installed", "count=" + n + " [" + tags + "]");

        string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screenpen_ocr2.png");
        const string Want = "ScreenPen 12345";
        using (Bitmap bmp = new Bitmap(760, 130))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (Font f = new Font("Segoe UI", 40f))
                    g.DrawString(Want, f, Brushes.Black, new PointF(20, 25));
            }
            bmp.Save(png, ImageFormat.Png);
        }

        object engine = null;
        try
        {
            MethodInfo m = engineT.GetMethod("TryCreateFromUserProfileLanguages", BindingFlags.Public | BindingFlags.Static);
            engine = m.Invoke(null, null);
        }
        catch (Exception ex) { Check(false, "TryCreateFromUserProfileLanguages", ex.Message); }
        Check(engine != null, "created OcrEngine", engine == null ? "null" : "ok");

        object softwareBitmap = null;
        try
        {
            // StorageFile.GetFileFromPathAsync
            Type fileT = WinRT("Windows.Storage.StorageFile");
            MethodInfo getFile = fileT.GetMethod("GetFileFromPathAsync", BindingFlags.Public | BindingFlags.Static);
            object file = Call(null, getFile, new object[] { png }, "GetFileFromPathAsync");
            Check(file != null, "StorageFile.GetFileFromPathAsync", file == null ? "null" : "ok");

            // file.OpenAsync(FileAccessMode.Read)
            MethodInfo open = Method(file, "OpenAsync", 1);
            object mode = Enum.Parse(open.GetParameters()[0].ParameterType, "Read");
            object stream = Call(file, open, new object[] { mode }, "OpenAsync");
            Check(stream != null, "StorageFile.OpenAsync -> IRandomAccessStream", stream == null ? "null" : "ok");

            // BitmapDecoder.CreateAsync(stream)
            Type decT = WinRT("Windows.Graphics.Imaging.BitmapDecoder");
            MethodInfo create = null;
            foreach (MethodInfo m in decT.GetMethods())
                if (m.Name == "CreateAsync" && m.GetParameters().Length == 1) { create = m; break; }
            object decoder = Call(null, create, new object[] { stream }, "BitmapDecoder.CreateAsync");
            Check(decoder != null, "BitmapDecoder.CreateAsync", decoder == null ? "null" : "ok");

            // decoder.GetSoftwareBitmapAsync()
            MethodInfo getSbm = Method(decoder, "GetSoftwareBitmapAsync", 0);
            softwareBitmap = Call(decoder, getSbm, new object[0], "GetSoftwareBitmapAsync");
        }
        catch (Exception ex) { Check(false, "decode PNG -> SoftwareBitmap", ex.Message); }
        Check(softwareBitmap != null, "got SoftwareBitmap", softwareBitmap == null ? "null" : "ok");

        string text = null;
        if (engine != null && softwareBitmap != null)
        {
            try
            {
                MethodInfo rec = Method(engine, "RecognizeAsync", 1);
                object result = Call(engine, rec, new object[] { softwareBitmap }, "RecognizeAsync");
                text = Convert.ToString(result.GetType().GetProperty("Text").GetValue(result, null));
            }
            catch (Exception ex) { Check(false, "RecognizeAsync", ex.Message); }
        }
        Check(text != null, "OCR returned text", text == null ? "null" : "\"" + text.Replace("\r", " ").Replace("\n", " ") + "\"");
        Check(text != null && text.Contains("ScreenPen") && text.Contains("12345"),
              "recognized text matches what was drawn", "want \"" + Want + "\" got \"" + (text ?? "") + "\"");

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
        try { System.IO.File.Delete(png); } catch { }
        return _fail == 0 ? 0 : 1;
    }
}
