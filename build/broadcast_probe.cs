using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Why does PostMessage(HWND_BROADCAST, WM_APP+0x51) never reach the app's
// HotkeyWindow, while a direct post works?
//
// Creates the same shapes of top-level window in one process and broadcasts to
// all of them, so the answer cannot be "the other process was busy".
static class BroadcastProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    const int WM_APP_TEST = 0x8000 + 0x51;
    const int WM_LOW_TEST = 0x0400;          // WM_USER: the documented boundary
    const int WM_BELOW_TEST = 0x03FF;        // just below WM_USER
    const int HWND_BROADCAST = 0xffff;

    static uint _regMsg;

    sealed class ProbeWindow : NativeWindow
    {
        public readonly string Tag;
        public bool GotApp;
        public bool GotLow;
        public bool GotBelow;
        public bool GotReg;

        public ProbeWindow(string tag, int style, int exStyle)
        {
            Tag = tag;
            CreateHandle(new CreateParams
            {
                Caption = "BroadcastProbe_" + tag,
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                Style = style,
                ExStyle = exStyle,
                Parent = IntPtr.Zero
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_TEST) GotApp = true;
            if (m.Msg == WM_LOW_TEST) GotLow = true;
            if (m.Msg == WM_BELOW_TEST) GotBelow = true;
            if (_regMsg != 0 && (uint)m.Msg == _regMsg) GotReg = true;
            base.WndProc(ref m);
        }
    }

    [STAThread]
    static void Main()
    {
        // Style = 0 is exactly how HotkeyWindow is created in AppContext2.
        ProbeWindow styleZero = new ProbeWindow("Style0", 0, 0);
        // WS_POPUP - an ordinary invisible top-level window.
        ProbeWindow popup = new ProbeWindow("WS_POPUP", unchecked((int)0x80000000), 0);
        // WS_OVERLAPPEDWINDOW - a plain visible-style top-level window.
        ProbeWindow overlapped = new ProbeWindow("WS_OVERLAPPED", 0x00CF0000, 0);

        Console.WriteLine("handles: Style0=0x" + styleZero.Handle.ToInt64().ToString("X") +
                          " WS_POPUP=0x" + popup.Handle.ToInt64().ToString("X") +
                          " WS_OVERLAPPED=0x" + overlapped.Handle.ToInt64().ToString("X"));

        int ticks = 0;
        Timer t = new Timer();
        t.Interval = 200;
        t.Tick += delegate
        {
            ticks++;
            if (ticks == 4)
            {
                bool ok = PostMessage((IntPtr)HWND_BROADCAST, WM_APP_TEST, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine("PostMessage(HWND_BROADCAST, WM_APP+0x51) returned " + ok);
            }
            if (ticks == 14)
            {
                Console.WriteLine("  broadcast WM_APP+0x51 -> Style0=" + styleZero.GotApp +
                                  "  WS_POPUP=" + popup.GotApp +
                                  "  WS_OVERLAPPED=" + overlapped.GotApp);
                PostMessage((IntPtr)HWND_BROADCAST, WM_LOW_TEST, IntPtr.Zero, IntPtr.Zero);
            }
            if (ticks == 22)
            {
                Console.WriteLine("  broadcast WM_USER(0x400) -> Style0=" + styleZero.GotLow +
                                  "  WS_POPUP=" + popup.GotLow +
                                  "  WS_OVERLAPPED=" + overlapped.GotLow);
                PostMessage((IntPtr)HWND_BROADCAST, WM_BELOW_TEST, IntPtr.Zero, IntPtr.Zero);
            }
            if (ticks == 30)
            {
                Console.WriteLine("  broadcast 0x3FF         -> Style0=" + styleZero.GotBelow +
                                  "  WS_POPUP=" + popup.GotBelow +
                                  "  WS_OVERLAPPED=" + overlapped.GotBelow);
                _regMsg = RegisterWindowMessage("ScreenPen.Activate.BroadcastProbe");
                PostMessage((IntPtr)HWND_BROADCAST, (int)_regMsg, IntPtr.Zero, IntPtr.Zero);
            }
            if (ticks == 38)
            {
                Console.WriteLine("  broadcast RegisterWindowMessage(0x" +
                                  _regMsg.ToString("X") + ") -> Style0=" + styleZero.GotReg +
                                  "  WS_POPUP=" + popup.GotReg +
                                  "  WS_OVERLAPPED=" + overlapped.GotReg);
                styleZero.GotApp = false;
                PostMessage(styleZero.Handle, WM_APP_TEST, IntPtr.Zero, IntPtr.Zero);
            }
            if (ticks == 44)
            {
                Console.WriteLine("  direct WM_APP+0x51      -> Style0=" + styleZero.GotApp);
                styleZero.DestroyHandle();
                popup.DestroyHandle();
                overlapped.DestroyHandle();
                Application.ExitThread();
            }
        };
        t.Start();
        Application.Run(new ApplicationContext());
    }
}
