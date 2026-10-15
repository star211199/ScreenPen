using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

// Regression probe for the "typing is impossible" report:
//
//   按下 T，进行打字的时候，按到其他的有特殊意义的按键会触发，
//   比如：L，P，O，E，A 等按键，然后打字就无法输入了
//
// The overlay sets KeyPreview = true, so AppContext2.OverlayKeyDown runs BEFORE
// the focused inline TextBox ever sees the keystroke. Every single-key command
// there (L/P/O/A = tools, E = clear, B = colour, 1-8 = palette) ended with
// e.Handled = true, which told WinForms "already dealt with" - so the character
// was swallowed and the label stayed empty.
//
// The fix is an early return from that handler while the editor is open. This
// probe proves it on the production path rather than by calling SetTool():
//
//   * negative control - with the editor CLOSED the very same handler must still
//     switch tools. Without this half, a probe asserting "nothing happened"
//     would pass even if the handler were never wired up at all.
//   * the guarded handler must leave Handled == false for every command key,
//     which is precisely the contract that lets the character through.
//   * real WM_KEYDOWN + WM_CHAR messages posted to the editor's own window must
//     land in the TextBox as text, and must not disturb tool/colour/strokes.
//   * Tab must not latch the ellipse modifier while typing (that latch would
//     make the next drag after committing a surprise ellipse).
static class TypingProbe
{
    const int WM_KEYDOWN = 0x0100;
    const int WM_CHAR = 0x0102;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    static int fails;

    static void Check(bool ok, string name, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    static void Pump()
    {
        for (int i = 0; i < 10; i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }
    }

    static object Field(object o, string name)
    {
        FieldInfo fi = o.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fi == null) throw new Exception("no field " + name);
        return fi.GetValue(o);
    }

    static void Call(object o, string name, params object[] args)
    {
        MethodInfo mi = o.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (mi == null) throw new Exception("no method " + name);
        mi.Invoke(o, args);
    }

    /// <summary>Drives the real AppContext2 key handler and reports what it did.</summary>
    static KeyEventArgs Feed(object ctx, Keys key)
    {
        KeyEventArgs e = new KeyEventArgs(key);
        Call(ctx, "OverlayKeyDown", null, e);
        return e;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();

        // startMinimized: true keeps the settings dialog from opening, so the
        // probe never touches a real modal window.
        AppContext2 ctx = new AppContext2(true);
        OverlayForm ov = (OverlayForm)Field(ctx, "_overlay");

        ov.ActivateOverlay(false);
        Pump();
        System.Threading.Thread.Sleep(400);
        Pump();

        Console.WriteLine("overlay hwnd = " + ov.Handle);
        Console.WriteLine();

        // ------------------------------------------------------------------
        // 1. Negative control: with NO editor open the handler still works.
        //    This is the half that makes the "nothing happens" assertions below
        //    meaningful.
        // ------------------------------------------------------------------
        Console.WriteLine("--- negative control: editor closed ---");
        ov.SetTool(Tool.Pen);

        KeyEventArgs eL = Feed(ctx, Keys.L);
        Check(ov.EffectiveToolNameForTest() == OverlayForm.ToolName(Tool.Line),
              "L switches to the line tool when not typing",
              "tool=" + ov.EffectiveToolNameForTest());
        Check(eL.Handled, "L is swallowed when not typing", "Handled=" + eL.Handled);

        Feed(ctx, Keys.E);
        Check(ov.StrokeCountForTest() == 0, "E clears when not typing",
              "strokes=" + ov.StrokeCountForTest());

        int colorBefore = ov.ColorIndex;
        KeyEventArgs eB = Feed(ctx, Keys.B);
        Check(ov.ColorIndex != colorBefore || eB.Handled,
              "B cycles the colour when not typing",
              "index " + colorBefore + " -> " + ov.ColorIndex);

        // Back to the text tool for the real test.
        ov.SetTool(Tool.Text);
        Pump();

        // ------------------------------------------------------------------
        // 2. Open the editor the way a click with the T tool does.
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- editor open ---");
        ov.TestBeginText(new PointF(700, 600));
        Pump();

        bool editing = ov.IsEditingTextForTest();
        Check(editing, "the inline editor opens",
              "IsEditing=" + editing);
        if (!editing)
        {
            Console.WriteLine();
            Console.WriteLine("cannot continue without the editor; "
                              + "another window probably owns the foreground");
            Console.WriteLine(fails + " FAILED");
            Environment.Exit(1);
        }

        TextBox editor = (TextBox)Field(ov, "_textEditor");
        Check(editor.Focused, "the editor really holds focus",
              "Focused=" + editor.Focused);

        // ------------------------------------------------------------------
        // 3. Every command key must be handed back to the editor.
        // ------------------------------------------------------------------
        string textTool = OverlayForm.ToolName(Tool.Text);
        Keys[] commands = new Keys[]
        {
            Keys.L, Keys.P, Keys.O, Keys.E, Keys.A, Keys.B,
            Keys.D1, Keys.M, Keys.R, Keys.T, Keys.F, Keys.D0
        };

        // Baseline read here, immediately before the command loop: the negative
        // control above deliberately pressed B and moved the palette on, so a
        // baseline captured back there would compare against a stale index.
        int colorWhileTyping = ov.ColorIndex;

        List<string> leaked = new List<string>();
        foreach (Keys k in commands)
        {
            KeyEventArgs e = Feed(ctx, k);
            if (e.Handled || e.SuppressKeyPress) leaked.Add(k.ToString());
        }
        Check(leaked.Count == 0,
              "no command key is swallowed while typing",
              leaked.Count == 0 ? "all " + commands.Length + " handed through"
                                : "swallowed: " + string.Join(",", leaked.ToArray()));
        Check(ov.EffectiveToolNameForTest() == textTool,
              "the tool does not change while typing",
              "tool=" + ov.EffectiveToolNameForTest());
        Check(ov.ColorIndex == colorWhileTyping,
              "the colour does not change while typing",
              "index " + colorWhileTyping + " -> " + ov.ColorIndex);
        Check(ov.StrokeCountForTest() == 0,
              "no stroke is created while typing",
              "strokes=" + ov.StrokeCountForTest());

        // Tab is the ellipse modifier: latching it here would leave _tabHeld
        // stuck, so the next drag after committing would be an ellipse.
        KeyEventArgs eTab = Feed(ctx, Keys.Tab);
        Check(!ov.TabHeld, "Tab does not latch the ellipse modifier while typing",
              "TabHeld=" + ov.TabHeld + " Handled=" + eTab.Handled);

        // ------------------------------------------------------------------
        // 4. The real thing: keystrokes posted at the editor's own window.
        //    WinForms raises KeyPreview on the parent form for these, so this
        //    exercises exactly the path the user's keyboard takes.
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- real keystrokes ---");
        string typed = "lpoeab1";
        for (int i = 0; i < typed.Length; i++)
        {
            char ch = typed[i];
            // WM_CHAR *is* the character: posting a matching WM_KEYDOWN as well
            // makes a well-behaved TextBox translate the key into the character
            // a second time (observed as "llppooeeaabb11"), which is a probe
            // artefact, not an app defect. Type the way text actually arrives.
            PostMessage(editor.Handle, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
            Pump();
        }

        string got = ov.TextEditorContentForTest();
        Check(got == typed, "every keystroke reaches the text box",
              "expected '" + typed + "' got '" + got + "'");

        string toolNow = ov.EffectiveToolNameForTest();
        Check(toolNow == textTool,
              "typing did not switch tools",
              "tool=" + toolNow);

        // ------------------------------------------------------------------
        // 5. Committing keeps the label and leaves no stuck modifier.
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- commit ---");
        ov.CommitTextEditing(true);
        Pump();

        Check(ov.StrokeCountForTest() == 1, "committing stores exactly one stroke",
              "strokes=" + ov.StrokeCountForTest());

        List<Stroke> strokes = (List<Stroke>)Field(ov, "_strokes");
        string stored = strokes.Count > 0 ? strokes[0].Text : null;
        Check(stored == typed, "the committed stroke holds what was typed",
              "text='" + (stored == null ? "<null>" : stored) + "'");
        Check(strokes.Count > 0 && strokes[0].Tool == Tool.Text,
              "the committed stroke is a text stroke",
              strokes.Count > 0 ? strokes[0].Tool.ToString() : "no stroke");
        Check(!ov.IsEditingTextForTest(), "the editor is gone after committing",
              "IsEditing=" + ov.IsEditingTextForTest());
        Check(!ov.TabHeld, "no modifier is left latched after committing",
              "TabHeld=" + ov.TabHeld);

        // ------------------------------------------------------------------
        // 6. After committing, single-key commands must work again.
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- commands work again after commit ---");
        Feed(ctx, Keys.L);
        Check(ov.EffectiveToolNameForTest() == OverlayForm.ToolName(Tool.Line),
              "L switches tools again once typing is finished",
              "tool=" + ov.EffectiveToolNameForTest());

        ov.DeactivateOverlay();
        try { ((IDisposable)ctx).Dispose(); } catch { }
        Pump();

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
