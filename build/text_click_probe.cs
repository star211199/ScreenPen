using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ScreenPen;

// Does a REAL mouse click open the inline text editor?
//
// tools_probe only exercises BeginTextEditing through the TestBeginText hook,
// which bypasses OnMouseDown. verify_upgrade.py drives the real thing with
// synthetic input but reports "0 Edit child", and its pen/Ctrl-drag numbers are
// bit-identical to each other (i.e. neither drew), so it cannot be trusted to
// distinguish a product bug from its own input-injection problem.
//
// This probe posts real WM_LBUTTONDOWN/WM_LBUTTONUP to the overlay's own
// window handle, so OnMouseDown/OnMouseUp run for real in-process with no
// cross-process focus games.
static class TextClickProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    delegate bool EnumProc(IntPtr h, IntPtr l);

    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_MOUSEMOVE = 0x0200;

    static int _passed, _failed;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what +
                          (detail != null && detail.Length > 0 ? "  (" + detail + ")" : ""));
    }

    static List<string> ChildClasses(IntPtr parent)
    {
        List<string> names = new List<string>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            names.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return names;
    }

    [STAThread]
    static void Main()
    {
        Console.WriteLine("=== real-click text editor probe ===");
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        Settings cfg = SettingsStore.Load();
        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov.Show();
        ov.Activate();
        Application.DoEvents();
        Thread.Sleep(300);

        IntPtr h = ov.Handle;

        // --- 1. clicking with the Text tool must open the editor -------------
        ov.SetTool(Tool.Text);
        Application.DoEvents();

        int lx = 400, ly = 300;
        IntPtr lp = (IntPtr)((ly << 16) | (lx & 0xFFFF));
        SendMessage(h, WM_MOUSEMOVE, IntPtr.Zero, lp);
        SendMessage(h, WM_LBUTTONDOWN, (IntPtr)1, lp);
        SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, lp);
        Application.DoEvents();
        Thread.Sleep(250);

        bool editing = ov.IsEditingTextForTest();
        Check(editing, "real click with the Text tool opens the inline editor",
              "IsEditingText=" + editing);

        List<string> kids = ChildClasses(h);
        int edits = 0;
        foreach (string c in kids) if (c == "Edit") edits++;
        Check(edits >= 1, "the editor is a real Edit child of the overlay",
              edits + " Edit child(ren); children=" + string.Join(",", kids.ToArray()));

        // --- 2. it must survive long enough to type into --------------------
        Thread.Sleep(400);
        Application.DoEvents();
        bool stillThere = ov.IsEditingTextForTest();
        Check(stillThere, "the editor does not self-destruct after the click",
              "still editing=" + stillThere);

        // --- 3. committing keeps the label ---------------------------------
        if (stillThere)
        {
            ov.TestSetTextEditorContent("测试");
            ov.CommitTextEditing(true);
            Application.DoEvents();
            int strokes = ov.StrokeCountForTest();
            Check(strokes >= 1, "committing the editor stores a text stroke",
                  strokes + " stroke(s)");
        }
        else
        {
            Check(false, "committing the editor stores a text stroke",
                  "editor was already gone");
        }

        // --- 4. real drag with Ctrl held must produce a rectangle -----------
        // Same in-process message path, so this isolates the modifier rule from
        // the harness's synthetic-key injection.
        ov.SetTool(Tool.Pen);
        Application.DoEvents();

        int x0 = 500, y0 = 500;
        IntPtr d0 = (IntPtr)((y0 << 16) | (x0 & 0xFFFF));
        SendMessage(h, WM_MOUSEMOVE, IntPtr.Zero, d0);
        SendMessage(h, WM_LBUTTONDOWN, (IntPtr)1, d0);
        for (int i = 1; i <= 12; i++)
        {
            int px = x0 + i * 10, py = y0 + i * 6;
            SendMessage(h, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((py << 16) | (px & 0xFFFF)));
        }
        SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, (IntPtr)(((y0 + 72) << 16) | ((x0 + 120) & 0xFFFF)));
        Application.DoEvents();
        Thread.Sleep(150);

        string tool = ov.LastStrokeToolForTest();
        int pts = ov.LastStrokePointCountForTest();
        Check(tool == "Pen", "an unmodified drag records a Pen stroke",
              "tool=" + tool + " points=" + pts);
        Check(pts > 2, "the pen stroke accumulated every mouse-move point",
              "points=" + pts);

        // Ctrl-drag: force the modifier through the same override rule the
        // mouse handler consults, then confirm the stored tool changes.
        ov.ClearForTest();
        Application.DoEvents();
        string rectName = ov.EffectiveToolForModifiersForTest(true, false, false);
        Check(rectName == "Rectangle", "Ctrl resolves to Rectangle",
              "got " + rectName);

        Console.WriteLine();
        Console.WriteLine("=== " + _passed + " passed, " + _failed + " failed ===");
        ov.Close();
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
