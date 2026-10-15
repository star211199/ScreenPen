using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenPen
{
    /// <summary>Single-instance guard implemented with a named mutex.</summary>
    public static class SingleInstance
    {
        static Mutex _mutex;

        /// <summary>Returns true when this is the first instance.</summary>
        public static bool Acquire()
        {
            bool createdNew;
            _mutex = new Mutex(true, @"Local\ScreenPen.SingleInstance", out createdNew);
            return createdNew;
        }

        /// <summary>
        /// Brings an already-running instance to the front by broadcasting a
        /// custom window message that the running app listens for.
        ///
        /// The id comes from RegisterWindowMessage, which is required for a
        /// HWND_BROADCAST message: broadcasts silently drop anything in the
        /// WM_USER..WM_APP range, so a plain custom constant never arrives.
        /// </summary>
        public static void SignalExistingInstance()
        {
            try
            {
                Native.PostMessage((IntPtr)Native.HWND_BROADCAST,
                                   Native.WM_SCREENPEN_ACTIVATE,
                                   IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }

    /// <summary>
    /// Window-autostart helper. Uses HKCU only, so no administrator rights are
    /// ever required and the change is visible/removable by the user.
    /// </summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "ScreenPen";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static bool Apply(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (enabled)
                    {
                        string exe = Settings.ExePath();
                        k.SetValue(ValueName, "\"" + exe + "\" --tray",
                                   RegistryValueKind.String);
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                    }
                }
                // Explorer caches Run-key contents; nudge it so the change takes
                // effect without requiring a sign-out.
                Native.NotifySettingChange();
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Builds tray / application icons from the embedded resources.</summary>
    public static class IconLibrary
    {
        static Icon _app;
        static Icon _tray;

        // Icon.FromHandle does not take ownership of the HICON, so the raw
        // handle returned by GetHicon must be released by hand.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon App
        {
            get
            {
                if (_app == null) _app = LoadFirst(new[] { "app.ico", "huabi.ico" }, 32);
                return _app;
            }
        }

        public static Icon Tray
        {
            get
            {
                if (_tray == null) _tray = LoadFirst(new[] { "huabi.ico", "app.ico" }, 16);
                return _tray;
            }
        }

        static Icon LoadFirst(string[] names, int size)
        {
            foreach (string name in names)
            {
                Icon ic = LoadEmbedded(name, size);
                if (ic != null) return ic;
            }
            // Fall back to a generated icon so the app always has one.
            return GenerateFallback(size);
        }

        static Icon LoadEmbedded(string name, int size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (!res.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) continue;
                        byte[] raw = new byte[s.Length];
                        s.Read(raw, 0, raw.Length);
                        return IconFromBytes(raw, size);
                    }
                }
            }
            catch { }
            return null;
        }

        static Icon IconFromBytes(byte[] raw, int size)
        {
            try
            {
                // A PNG masquerading as .ico, or a real ICO: try both.
                using (MemoryStream ms = new MemoryStream(raw))
                {
                    try
                    {
                        using (Bitmap bmp = new Bitmap(ms))
                        using (Bitmap resized = Resize(bmp, size))
                        {
                            // FromHandle does not own the HICON: clone the icon
                            // and release the raw handle ourselves, otherwise the
                            // handle would leak for the life of the process.
                            IntPtr h = resized.GetHicon();
                            try { return (Icon)Icon.FromHandle(h).Clone(); }
                            finally { DestroyIcon(h); }
                        }
                    }
                    catch { }
                }
                using (MemoryStream ms = new MemoryStream(raw))
                    return new Icon(ms, size, size);
            }
            catch { return null; }
        }

        public static Bitmap Resize(Image src, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            return bmp;
        }

        /// <summary>Draws a simple brush glyph when no icon resource is present.</summary>
        static Icon GenerateFallback(int size)
        {
            using (Bitmap bmp = new Bitmap(Math.Max(16, size), Math.Max(16, size)))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float s = bmp.Width;
                    using (Pen p = new Pen(Color.FromArgb(255, 232, 160, 0), s * 0.16f))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(p, s * 0.28f, s * 0.76f, s * 0.72f, s * 0.24f);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 60, 64, 72)))
                        g.FillEllipse(b, s * 0.18f, s * 0.70f, s * 0.20f, s * 0.16f);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }
}
