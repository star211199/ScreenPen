using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenPen;

// Why does tools_probe's text editor vanish immediately?
//
// BeginTextEditing() creates the TextBox and calls Focus(). If the overlay form
// cannot become the foreground window, the control never really takes focus, so
// LostFocus fires at once -> CommitTextEditing(true) -> empty text discarded ->
// _textEditor becomes null and IsEditingTextForTest() reports False.
//
// This prints the foreground state at each step so we can tell an environment
// problem (another app owns the foreground) from a product defect.
static class TextFocusProbe
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);

    static int fails;

    static void Check(bool ok, string name, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Settings cfg = new Settings();
        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov.ActivateOverlay(false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(500);
        Application.DoEvents();

        IntPtr fg = GetForegroundWindow();
        Console.WriteLine("overlay hwnd      = " + ov.Handle);
        Console.WriteLine("foreground hwnd   = " + fg);
        Console.WriteLine("overlay is fg     = " + (fg == ov.Handle));
        Console.WriteLine("overlay ContainsFocus = " + ov.ContainsFocus);
        Console.WriteLine("overlay Focused   = " + ov.Focused);
        Console.WriteLine();

        Check(fg == ov.Handle, "the overlay actually owns the foreground",
              "fg=" + fg + " ov=" + ov.Handle);

        ov.TestBeginText(new PointF(700, 600));
        Application.DoEvents();
        Console.WriteLine("after TestBeginText: IsEditing=" + ov.IsEditingTextForTest());
        Check(ov.IsEditingTextForTest(), "text editor survives being opened",
              "IsEditing=" + ov.IsEditingTextForTest());

        if (ov.IsEditingTextForTest())
        {
            ov.TestSetTextEditorContent("标注文字测试");
            Application.DoEvents();
            Check(ov.TextEditorContentForTest() == "标注文字测试",
                  "editor accepts typed content", "'" + ov.TextEditorContentForTest() + "'");
            ov.CommitTextEditing(true);
            Application.DoEvents();
            Check(ov.StrokeCount == 1, "text became a stroke",
                  "strokes=" + ov.StrokeCount);
        }

        ov.Close();
        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
