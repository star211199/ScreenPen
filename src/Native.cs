using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>P/Invoke surface: hotkeys, screen metrics, layered-window helpers.</summary>
    public static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;

        /// <summary>Pseudo-handle: every top-level window.</summary>
        public const int HWND_BROADCAST = 0xffff;

        /// <summary>
        /// Custom broadcast message used to wake an already-running instance
        /// when the user launches the EXE a second time.
        ///
        /// This must be a RegisterWindowMessage id (0xC000-0xFFFF), NOT a
        /// WM_APP-based constant. PostMessage/SendMessage to HWND_BROADCAST
        /// silently drops every message >= WM_USER (0x400) - it returns true
        /// but no window ever receives it - so the old 0x8051 constant meant a
        /// second launch did nothing at all. Registered ids are allocated
        /// globally per string and are delivered to all top-level windows.
        /// </summary>
        public const string SCREENPEN_ACTIVATE_MESSAGE = "ScreenPen.Activate.9F2A7C14";

        /// <summary>
        /// The registered id for SCREENPEN_ACTIVATE_MESSAGE. RegisterWindowMessage
        /// returns the same value for the same string in every process, so the
        /// signalling instance and the running instance agree without any IPC.
        /// </summary>
        public static readonly int WM_SCREENPEN_ACTIVATE =
            (int)RegisterWindowMessage(SCREENPEN_ACTIVATE_MESSAGE);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        /// <summary>Overload that returns the owning process id.</summary>
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
                                                    bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>
        /// Makes <paramref name="hWnd"/> the foreground window as reliably as the
        /// OS permits.
        ///
        /// A bare SetForegroundWindow() is only a request: Windows refuses it
        /// whenever the calling process did not last receive user input (the
        /// "foreground lock"). The overlay then stays behind another window, and
        /// because the inline text editor calls Focus() and commits itself on
        /// LostFocus, a refused claim silently discards the label the user just
        /// typed. Attaching our input queue to the current foreground thread
        /// satisfies the check for the duration of the call, which is the
        /// documented workaround. Everything here is best-effort - the return
        /// value reports whether the window ended up in front.
        /// </summary>
        public static bool ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            if (GetForegroundWindow() == hWnd) return true;

            IntPtr fg = GetForegroundWindow();
            uint ourThread = GetCurrentThreadId();
            uint fgThread = fg == IntPtr.Zero ? 0u
                          : GetWindowThreadProcessId(fg, IntPtr.Zero);

            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != ourThread)
                    attached = AttachThreadInput(ourThread, fgThread, true);

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                SetActiveWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThread, fgThread, false);
            }

            return GetForegroundWindow() == hWnd;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                               int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        // ---- window hit-testing / click-through -----------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        public const uint GA_ROOT = 2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, value);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
        }

        /// <summary>
        /// Makes a window ignore mouse input so clicks pass through to whatever
        /// is underneath. Used by scroll-follow mode so the user can scroll and
        /// click the real document while the annotation layer stays on top.
        /// </summary>
        public static void SetClickThrough(IntPtr hWnd, bool enable)
        {
            try
            {
                IntPtr ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                long v = ex.ToInt64();
                if (enable) v |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                else v &= ~(long)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(v));
            }
            catch { }
        }

        // ---- input synthesis -------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint MOUSEEVENTF_HWHEEL = 0x1000;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// Injects a mouse-wheel event into the system input queue. Unlike
        /// PostMessage(WM_MOUSEWHEEL) this is indistinguishable from real
        /// hardware, so browsers and Win32 apps both honour it, and it is
        /// delivered to whatever window is under the cursor.
        /// </summary>
        public static bool SendWheel(int delta, bool horizontal)
        {
            try
            {
                INPUT[] inp = new INPUT[1];
                inp[0].type = INPUT_MOUSE;
                inp[0].mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
                inp[0].mi.mouseData = unchecked((uint)delta);
                inp[0].mi.dx = 0;
                inp[0].mi.dy = 0;
                inp[0].mi.time = 0;
                inp[0].mi.dwExtraInfo = IntPtr.Zero;
                return SendInput(1, inp, Marshal.SizeOf(typeof(INPUT))) == 1;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        /// <summary>Ex-style flags of a window, or 0 when it cannot be read.</summary>
        public static long ExStyleOf(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return 0;
                return GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            }
            catch { return 0; }
        }

        /// <summary>
        /// True when the window under the given screen point scrolls
        /// independently of the desktop, i.e. it is a real document/web page
        /// rather than the desktop or a Chromium/Electron host surface.
        /// </summary>
        public static bool IsScrollableWindowUnder(Point screenPoint)
        {
            try
            {
                POINT p = new POINT(screenPoint.X, screenPoint.Y);
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;

                System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
                GetClassName(h, cls, 256);
                string c = cls.ToString();

                // The desktop and the shell's worker windows do not scroll.
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" ||
                    c == "SysListView32" || c == "SHELLDLL_DefView") return false;

                IntPtr root = GetAncestor(h, GA_ROOT);
                System.Text.StringBuilder rcls = new System.Text.StringBuilder(256);
                if (root != IntPtr.Zero) GetClassName(root, rcls, 256);
                string rc = rcls.ToString();
                if (rc == "Progman" || rc == "WorkerW") return false;

                return true;
            }
            catch { return false; }
        }

        // ---- screen capture of a region (see CaptureRegion) -------------------

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SM_CMONITORS = 80;

        /// <summary>Virtual-desktop bounds across every monitor.</summary>
        public static Rectangle VirtualScreen()
        {
            return new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
                Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));
        }

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                  IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
                                      int rop);

        [DllImport("gdi32.dll")]
        static extern int SetStretchBltMode(IntPtr hdc, int mode);

        const int HALFTONE = 4;

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

        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
                                    byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

        [DllImport("user32.dll")]
        static extern IntPtr GetDesktopWindow();

        /// <summary>
        /// Grabs the screen area scaled down by <paramref name="divisor"/> and
        /// returns it as an 8-bit luminance buffer.
        ///
        /// This is used to detect page scrolling: doing a full-resolution
        /// capture and DIB conversion every frame would cost tens of megabytes
        /// per second, whereas a 1/8-scale StretchBlt plus a small GetDIBits is
        /// cheap enough to run continuously.
        /// </summary>
        public static byte[] CaptureGray(Point origin, Size size, int divisor,
                                         out int outW, out int outH)
        {
            outW = 0; outH = 0;
            if (size.Width <= 0 || size.Height <= 0 || divisor < 1) return null;

            int sw = Math.Max(1, size.Width / divisor);
            int sh = Math.Max(1, size.Height / divisor);

            IntPtr hdcSrc = IntPtr.Zero, hdcDst = IntPtr.Zero, hBmp = IntPtr.Zero, hOld = IntPtr.Zero;
            try
            {
                hdcSrc = GetDC(IntPtr.Zero);
                hdcDst = CreateCompatibleDC(hdcSrc);
                hBmp = CreateCompatibleBitmap(hdcSrc, sw, sh);
                hOld = SelectObject(hdcDst, hBmp);
                SetStretchBltMode(hdcDst, HALFTONE);
                if (!StretchBlt(hdcDst, 0, 0, sw, sh, hdcSrc,
                                origin.X, origin.Y, size.Width, size.Height,
                                SRCCOPY | CAPTUREBLT))
                    return null;

                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bi.biWidth = sw;
                bi.biHeight = -sh;              // top-down
                bi.biPlanes = 1;
                bi.biBitCount = 32;
                bi.biCompression = 0;

                byte[] buf = new byte[sw * sh * 4];
                if (GetDIBits(hdcDst, hBmp, 0, (uint)sh, buf, ref bi, 0) == 0) return null;

                byte[] gray = new byte[sw * sh];
                for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
                {
                    // Rec.601 luma, integer weights.
                    gray[i] = (byte)((buf[p + 2] * 77 + buf[p + 1] * 150 + buf[p] * 29) >> 8);
                }
                outW = sw; outH = sh;
                return gray;
            }
            catch { return null; }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcDst, hOld);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (hdcDst != IntPtr.Zero) DeleteDC(hdcDst);
                if (hdcSrc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }

        const int WDA_NONE = 0x00000000;
        const int WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        /// <summary>
        /// Hides a window from screen-capture APIs while leaving it visible on
        /// screen (Windows 10 2004+). The annotation overlay uses this so it can
        /// observe the *real* document it is floating over without its own
        /// strokes polluting the observation.
        /// </summary>
        public static bool SetExcludedFromCapture(IntPtr hWnd, bool excluded)
        {
            try
            {
                return SetWindowDisplayAffinity(hWnd,
                    excluded ? (uint)WDA_EXCLUDEFROMCAPTURE : (uint)WDA_NONE);
            }
            catch { return false; }
        }

        const int SRCCOPY = 0x00CC0020;
        const int CAPTUREBLT = 0x40000000;

        /// <summary>
        /// Captures a screen rectangle as a new Bitmap. Uses CAPTUREBLT so
        /// layered windows (including our own overlays) are included.
        /// </summary>
        public static Bitmap CaptureRegion(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            IntPtr hdcSrc = IntPtr.Zero, hdcDst = IntPtr.Zero, hBmp = IntPtr.Zero, hOld = IntPtr.Zero;
            try
            {
                hdcSrc = GetDC(IntPtr.Zero);
                hdcDst = CreateCompatibleDC(hdcSrc);
                hBmp = CreateCompatibleBitmap(hdcSrc, bounds.Width, bounds.Height);
                hOld = SelectObject(hdcDst, hBmp);
                BitBlt(hdcDst, 0, 0, bounds.Width, bounds.Height,
                       hdcSrc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
                SelectObject(hdcDst, hOld);
                hOld = IntPtr.Zero;
                Bitmap result = Image.FromHbitmap(hBmp);
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcDst, hOld);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (hdcDst != IntPtr.Zero) DeleteDC(hdcDst);
                if (hdcSrc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
                                                string lParam, uint flags,
                                                uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
                                                IntPtr lParam, uint flags,
                                                uint timeout, out IntPtr result);

        static readonly IntPtr HWND_BROADCAST_PTR = new IntPtr(HWND_BROADCAST);
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>
        /// Tells Explorer that environment settings changed. Explorer caches
        /// Run-key state, so without this a newly added autostart entry may not
        /// be honoured until the next sign-in.
        /// </summary>
        public static void NotifySettingChange()
        {
            try
            {
                IntPtr res;
                SendMessageTimeout(HWND_BROADCAST_PTR, WM_SETTINGCHANGE, IntPtr.Zero,
                                   "Environment", SMTO_ABORTIFHUNG, 3000, out res);
            }
            catch { }
        }
    }
}
