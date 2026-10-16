using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenPen
{
    /// <summary>
    /// A WH_KEYBOARD_LL hook that reports a system-wide Esc key-down.
    ///
    /// The GIF recorder's chrome window is WS_EX_NOACTIVATE and never takes
    /// focus, so it can never receive a key press. That is why the README's
    /// "按 Esc 停止" had no way of working: the key went to whatever application
    /// was in the foreground and the recording just kept going until the 30 s
    /// cap. A low-level hook sees the key regardless of focus.
    ///
    /// It is installed only for the duration of a recording and always
    /// uninstalled afterwards, so no keystroke is ever observed at any other
    /// time. Install() reports failure rather than throwing: if the hook cannot
    /// be placed the recording still works, it just has to be ended from the
    /// tray.
    /// </summary>
    sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int VK_ESCAPE = 0x1B;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        IntPtr _handle;

        /// <summary>
        /// Held in a field on purpose. The GC does not trace a delegate that is
        /// only referenced by native code, so a local would be collected and the
        /// next keystroke would jump into freed memory.
        /// </summary>
        HookProc _proc;

        /// <summary>
        /// Captured at Install() so the callback can hand the work back to the
        /// UI thread's queue. See <see cref="Callback"/>.
        /// </summary>
        SynchronizationContext _ctx;

        /// <summary>Raised on the UI thread when Esc goes down.</summary>
        public event EventHandler EscapePressed;

        public bool IsInstalled { get { return _handle != IntPtr.Zero; } }

        public bool Install()
        {
            if (_handle != IntPtr.Zero) return true;
            try
            {
                _ctx = SynchronizationContext.Current;
                _proc = new HookProc(Callback);
                _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_handle == IntPtr.Zero) _proc = null;
                return _handle != IntPtr.Zero;
            }
            catch
            {
                _handle = IntPtr.Zero;
                _proc = null;
                return false;
            }
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 means "pass it on without processing".
            if (nCode >= 0 &&
                (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                bool esc = false;
                try
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(KBDLLHOOKSTRUCT));
                    esc = k.vkCode == VK_ESCAPE;
                }
                catch { }

                if (esc)
                {
                    // The work must not run inside the callback. Stopping a
                    // recording encodes and writes the whole GIF, and Windows
                    // silently unhooks a low-level hook whose callback takes
                    // longer than LowLevelHooksTimeout (~300 ms) - so the hook
                    // would disappear after the first Esc. Posting returns
                    // immediately and runs the work on the next message.
                    SynchronizationContext ctx = _ctx;
                    if (ctx != null) ctx.Post(delegate { RaiseEscapePressed(); }, null);
                    else RaiseEscapePressed();

                    // Swallow it: Esc belongs to the recording, not to whatever
                    // happens to be in the foreground.
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_handle, nCode, wParam, lParam);
        }

        void RaiseEscapePressed()
        {
            EventHandler h = EscapePressed;
            if (h == null) return;
            try { h(this, EventArgs.Empty); }
            catch { }
        }

        /// <summary>Test seam: exercises the event plumbing without a real keystroke.</summary>
        internal void RaiseEscapeForTest()
        {
            RaiseEscapePressed();
        }

        public void Dispose()
        {
            IntPtr h = _handle;
            _handle = IntPtr.Zero;
            if (h == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(h); }
            catch { }
            _proc = null;
            _ctx = null;
        }
    }
}
