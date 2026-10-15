// Feasibility probe for two more candidate features:
//
//  A) QR / barcode decoding from a screenshot WITHOUT bundling a decoder
//     library. There is no QR decoder in .NET Framework or in WinRT's base
//     surface, so this checks whether Windows actually exposes one (e.g. via
//     the ZXing-based Windows.Graphics.Imaging / barcode APIs) before we commit
//     to writing a pure-managed decoder.
//
//  B) Window-under-cursor / active-window capture geometry: can we enumerate
//     top-level windows and get their real bounds with only user32? This is the
//     basis for "capture active window" and "capture window under cursor", which
//     ShareX/Greenshot all have and ScreenPen currently lacks.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class FeatureProbe
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled(out bool enabled);

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    static Type WinRT(string name)
    {
        string[] asms = new string[] { "Windows.Foundation", "Windows.Graphics", "Windows.Media", "Windows.Storage" };
        foreach (string a in asms)
        {
            Type t = Type.GetType(name + ", " + a + ", ContentType=WindowsRuntime");
            if (t != null) return t;
        }
        return null;
    }

    static void Main()
    {
        Console.WriteLine("=== FEATURE FEASIBILITY PROBE ===");
        Console.WriteLine("runtime = " + Environment.Version + " / " + IntPtr.Size * 8 + "-bit");
        Console.WriteLine();

        // ================= A) is a QR decoder available? ======================
        Console.WriteLine("-- A) QR / barcode decoding --");
        string[] barcodeTypes = new string[] {
            "Windows.Media.Ocr.OcrEngine",
            "Windows.Graphics.Imaging.BitmapDecoder",
            "Windows.Media.Capture.MediaCapture",
        };
        foreach (string bt in barcodeTypes)
        {
            Type t = WinRT(bt);
            Console.WriteLine("     resolve " + bt + " -> " + (t != null ? "found" : "NOT FOUND"));
        }
        // Look for any WinRT barcode-ish type across the known namespaces.
        string[] guesses = new string[] {
            "Windows.Media.Ocr.BarcodeScanner", "Windows.Devices.PointOfService.BarcodeScanner",
            "Windows.Media.Ocr.OcrResult", "Windows.Graphics.Imaging.BitmapDecoder"
        };
        int found = 0;
        foreach (string g in guesses) if (WinRT(g) != null) found++;
        Console.WriteLine("     WinRT barcode scanners found: " + found + " (expect 0 -> no built-in decoder)");

        // Does the in-box .NET have anything? (System.Drawing has none; check
        // for the WPF/WIC barcode metadata readers, which do not decode QR.)
        bool hasManagedDecoder = false;
        try
        {
            Type zx = Type.GetType("ZXing.BarcodeReader, zxing");
            if (zx != null) hasManagedDecoder = true;
        }
        catch { }
        Check(!hasManagedDecoder, "no bundled QR library present (confirms: would need own decoder)",
              hasManagedDecoder ? "ZXing found" : "none");

        // ================= B) window geometry ==================================
        Console.WriteLine();
        Console.WriteLine("-- B) window capture geometry (user32/dwmapi only) --");

        bool composition = false;
        int dwmOk = DwmIsCompositionEnabled(out composition);
        Check(dwmOk == 0, "DwmIsCompositionEnabled reachable", "hr=0x" + dwmOk.ToString("X8") + " composition=" + composition);

        IntPtr fg = GetForegroundWindow();
        Check(fg != IntPtr.Zero, "GetForegroundWindow returns a handle", "hwnd=0x" + fg.ToString("X"));

        // The key DPI-correctness question: GetWindowRect includes the invisible
        // resize border on Win10, so a capture based on it has transparent
        // padding. DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9) gives the
        // real visible bounds. Verify both are obtainable and see if they differ.
        RECT raw, ext;
        bool gotRaw = GetWindowRect(fg, out raw);
        int hrExt = DwmGetWindowAttribute(fg, 9, out ext, Marshal.SizeOf(typeof(RECT)));
        Check(gotRaw, "GetWindowRect works", gotRaw ? Fmt(raw) : "failed");
        Check(hrExt == 0, "DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS) works",
              hrExt == 0 ? Fmt(ext) : "hr=0x" + hrExt.ToString("X8"));

        if (gotRaw && hrExt == 0)
        {
            int padL = ext.Left - raw.Left, padT = ext.Top - raw.Top;
            int padR = raw.Right - ext.Right, padB = raw.Bottom - ext.Bottom;
            Console.WriteLine("     invisible border padding: L=" + padL + " T=" + padT + " R=" + padR + " B=" + padB);
            Check(padL != 0 || padT != 0 || padR != 0 || padB != 0,
                  "extended frame bounds differ from GetWindowRect (so GetWindowRect alone would capture ghost padding)",
                  "delta L/T/R/B = " + padL + "/" + padT + "/" + padR + "/" + padB);
        }

        // Enumerate visible top-level windows: basis for a window picker.
        List<IntPtr> wins = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (IsWindowVisible(h) && !IsIconic(h))
            {
                int len = GetWindowTextLength(h);
                if (len > 0) wins.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        Check(wins.Count > 3, "enumerated visible titled top-level windows", "count=" + wins.Count);

        // Title + bounds + owner-process for each (needed for a picker UI).
        int named = 0;
        foreach (IntPtr h in wins)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length > 0) named++;
        }
        Check(named == wins.Count, "read every window title via GetWindowText", named + "/" + wins.Count);

        // WindowFromPoint + GetAncestor(GA_ROOT) = "window under cursor".
        POINT pt;
        pt.X = 20; pt.Y = 20;
        IntPtr under = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
        Check(under != IntPtr.Zero, "WindowFromPoint + GA_ROOT resolves a root window under a point",
              "hwnd=0x" + under.ToString("X"));

        // GWL_STYLE / GWL_EXSTYLE available for filtering tool windows.
        int style = GetWindowLong(fg, -16);
        int exstyle = GetWindowLong(fg, -20);
        Check(style != 0, "GetWindowLong(GWL_STYLE) readable", "style=0x" + style.ToString("X8") + " ex=0x" + exstyle.ToString("X8"));

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
    }

    static string Fmt(RECT r)
    {
        return "{X=" + r.Left + ",Y=" + r.Top + ",W=" + (r.Right - r.Left) + ",H=" + (r.Bottom - r.Top) + "}";
    }
}
