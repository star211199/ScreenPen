// layout_probe.cs - machine-checkable UI layout assertions.
//
// The model cannot view images, so "the text is clipped" must be turned into a
// number. This probe realizes the real SettingsForm off-screen and asserts:
//   1. every nav label fits inside its button,
//   2. every page's content fits inside the scroll viewport,
//   3. every card's children fit inside the card,
//   4. every Label's text fits inside the Label.
using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class LayoutProbe
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    static int passed, failed;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) passed++; else failed++;
        Console.WriteLine("  " + (ok ? "[PASS]" : "[FAIL]") + " " + name
            + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    static object Field(object o, string n)
    {
        Type t = o.GetType();
        while (t != null)
        {
            FieldInfo fi = t.GetField(n,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi != null) return fi.GetValue(o);
            t = t.BaseType;
        }
        return null;
    }

    static void Call(object o, string n, object[] args)
    {
        Type t = o.GetType();
        while (t != null)
        {
            MethodInfo mi = t.GetMethod(n,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi != null) { mi.Invoke(o, args); return; }
            t = t.BaseType;
        }
        throw new MissingMethodException(n);
    }

    static int MeasureW(string text, Font f)
    {
        return TextRenderer.MeasureText(text, f).Width;
    }

    static int MeasureH(string text, Font f, int width)
    {
        if (width < 1) width = 1;
        return TextRenderer.MeasureText(text, f, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak).Height;
    }

    /// <summary>
    /// Widest single line of a possibly multi-line caption.
    ///
    /// The dialog writes explicit "\n" breaks into most descriptions, so
    /// measuring the whole string as one line wildly overstates the width (a
    /// four-line paragraph would "need" 1800px). Only the longest line has to
    /// fit.
    /// </summary>
    static int MeasureWidestLine(string text, Font f)
    {
        int worst = 0;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            int w = TextRenderer.MeasureText(line, f).Width;
            if (w > worst) worst = w;
        }
        return worst;
    }

    /// <summary>
    /// Height a caption actually occupies: its explicit line count when each
    /// line fits, otherwise the word-wrapped height.
    /// </summary>
    static int MeasureTextHeight(string text, Font f, int width)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int total = 0;
        int oneLine = TextRenderer.MeasureText("Ag", f).Height;
        foreach (string line in lines)
        {
            if (line.Length == 0) { total += oneLine; continue; }
            if (TextRenderer.MeasureText(line, f).Width <= width)
                total += oneLine;
            else
                total += MeasureH(line, f, width);
        }
        return total;
    }

    [STAThread]
    static int Main()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (Exception ex) { Console.WriteLine("dpi: " + ex.Message); }

        try { Run(); }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            failed++;
        }

        Console.WriteLine();
        Console.WriteLine("=== " + passed + " passed, " + failed + " failed ===");
        return failed == 0 ? 0 : 1;
    }

    static void Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings cfg = new Settings();
        cfg.Clamp();

        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);   // off-screen: never disturb the user
        f.Show();
        Application.DoEvents();

        Console.WriteLine("dialog client = " + f.ClientSize.Width + "x" + f.ClientSize.Height);

        // =============================================================
        // 1. navigation buttons
        // =============================================================
        Console.WriteLine();
        Console.WriteLine("--- 1. navigation buttons ---");
        IList navs = (IList)Field(f, "_navButtons");
        Panel nav = (Panel)Field(f, "_nav");
        Console.WriteLine("nav panel width = " + nav.Width);

        bool allNavFit = true;
        foreach (object o in navs)
        {
            Button b = (Button)o;
            int need = MeasureW(b.Text, b.Font);
            int avail = b.Width - 10;
            bool fits = need <= avail;
            if (!fits) allNavFit = false;
            Console.WriteLine(string.Format(
                "  text={0,-16} font={1,5:F1}pt box={2,3}px need={3,3}px avail={4,3}px {5}",
                b.Text.Replace(" ", "·"), b.Font.Size, b.Width, need, avail,
                fits ? "ok" : "CLIPPED"));
        }
        Check("every nav label fits its button", allNavFit, navs.Count + " buttons");

        // =============================================================
        // 2-4. per page
        // =============================================================
        IDictionary pages = (IDictionary)Field(f, "_pages");
        Panel content = (Panel)Field(f, "_content");
        Console.WriteLine();
        Console.WriteLine("--- 2. page content vs viewport ---");
        Console.WriteLine("content viewport height = " + content.ClientSize.Height
            + "  AutoScroll=" + content.AutoScroll);

        bool allPagesFit = true;
        bool allChildrenFit = true;
        bool allLabelsFit = true;
        int totalClippedLabels = 0;

        ArrayList names = new ArrayList();
        foreach (DictionaryEntry e in pages) names.Add(e.Key);

        foreach (object key in names)
        {
            string name = (string)key;
            Call(f, "ShowPage", new object[] { name });
            Application.DoEvents();

            Panel page = (Panel)pages[name];
            int maxBottom = 0;
            foreach (Control c in page.Controls)
                if (c.Bottom > maxBottom) maxBottom = c.Bottom;

            // A page may legitimately be taller than its viewport PROVIDED it can
            // scroll to reach the overflow. That is the whole point of the fix,
            // so the assertion is "reachable", not "fits".
            int scrollable = page.AutoScrollMinSize.Height;
            int reachable = page.ClientSize.Height + Math.Max(0, scrollable - page.ClientSize.Height);
            bool canScroll = page.AutoScroll && page.VerticalScroll.Visible;
            bool fits = maxBottom <= page.ClientSize.Height;
            bool ok = fits || (canScroll && maxBottom <= scrollable);
            if (!ok) allPagesFit = false;
            Console.WriteLine(string.Format(
                "  {0,-6} content={1,4}px viewport={2,4}px scrollMin={3,4}px bar={4,-5} {5}",
                name, maxBottom, page.ClientSize.Height, scrollable,
                page.VerticalScroll.Visible,
                ok ? (fits ? "fits" : "scrollable") : "UNREACHABLE by "
                    + (maxBottom - Math.Max(scrollable, page.ClientSize.Height)) + "px"));

            foreach (Control c in page.Controls)
            {
                Panel card = c as Panel;
                if (card == null) continue;
                Walk(card, card, ref allChildrenFit, ref allLabelsFit,
                     ref totalClippedLabels, 0);
            }
        }

        Check("every page's content fits the viewport", allPagesFit, "");
        Check("every card child fits inside its card", allChildrenFit, "");
        Check("every label's text fits inside the label", allLabelsFit,
            totalClippedLabels + " clipped");

        f.Close();
        f.Dispose();
    }

        /// <summary>
        /// Recursively checks every descendant of a card.
        ///
        /// Recursion matters: the first version only inspected direct children,
        /// so a label nested one level down (inside a group or a host panel) was
        /// never measured and could clip freely. Buttons are checked too - the
        /// "恢复默认" button was clipping its own caption and the probe said
        /// nothing, because it only ever looked at Label.
        /// </summary>
        static void Walk(Control root, Panel card, ref bool allChildrenFit,
                         ref bool allLabelsFit, ref int totalClipped, int depth)
        {
            foreach (Control ch in root.Controls)
            {
                if (ch.Bottom > card.Height)
                {
                    allChildrenFit = false;
                    Console.WriteLine(string.Format(
                        "      child overflow: {0} '{1}' bottom={2} cardH={3}",
                        ch.GetType().Name, Trim(ch.Text), ch.Bottom, card.Height));
                }

                if (ch.Text != null && ch.Text.Length > 0)
                {
                    Label lab = ch as Label;
                    Button btn = ch as Button;
                    CheckBox cb = ch as CheckBox;
                    RadioButton rb = ch as RadioButton;
                    bool textish = lab != null || btn != null || cb != null || rb != null;

                    if (textish)
                    {
                        int needW, needH;

                        if (lab != null && lab.AutoSize)
                        {
                            // Authoritative: an AutoSize label reports the size
                            // it actually wants, already accounting for
                            // MaximumSize word-wrap. Deriving the width from
                            // raw text measurement double-counts the wrap cap
                            // and reported every wrapped paragraph as clipped.
                            needW = lab.PreferredSize.Width;
                            needH = lab.PreferredSize.Height;

                            // AutoSize means it is never internally clipped; the
                            // only real risk is the parent cutting it off.
                            bool parentClips = false;
                            Rectangle scr = ch.Parent.RectangleToScreen(ch.Bounds);
                            Rectangle cardScr = card.RectangleToScreen(card.ClientRectangle);
                            if (scr.Right > cardScr.Right || scr.Bottom > cardScr.Bottom)
                                parentClips = true;

                            if (parentClips)
                            {
                                allLabelsFit = false;
                                totalClipped++;
                                Console.WriteLine(string.Format(
                                    "      text clipped: {0} '{1}' (parent) size={2}x{3} need={4}x{5}",
                                    ch.GetType().Name, Trim(ch.Text),
                                    ch.Width, ch.Height, needW, needH));
                            }
                            continue;
                        }

                        if (btn != null)
                        {
                            // Width: the caption must fit inside the button's
                            // client area. GetPreferredSize is NOT usable here -
                            // it reports the button's *desired* size (including
                            // its default 23px minimum and border padding), so a
                            // perfectly legible 32px-tall button "needs" 43px and
                            // produces false positives.
                            needW = TextRenderer.MeasureText(btn.Text, btn.Font).Width + 16;
                            // Height: only the text line height matters; the
                            // button's own border adds nothing the caption needs.
                            needH = TextRenderer.MeasureText(btn.Text, btn.Font).Height + 4;
                        }
                        else
                        {
                            needW = MeasureWidestLine(ch.Text, ch.Font) + 4;
                            needH = MeasureTextHeight(ch.Text, ch.Font,
                                Math.Max(1, ch.Width - 4)) + 2;
                        }

                        bool clippedW = needW > ch.Width;
                        bool clippedH = needH > ch.Height;

                        if (clippedW || clippedH)
                        {
                            allLabelsFit = false;
                            totalClipped++;
                            Console.WriteLine(string.Format(
                                "      text clipped: {0} '{1}' {2} size={3}x{4} need={5}x{6}",
                                ch.GetType().Name, Trim(ch.Text),
                                clippedW && clippedH ? "(H+W)" : (clippedH ? "(H)" : "(W)"),
                                ch.Width, ch.Height, needW, needH));
                        }
                    }
                }

                if (ch.Controls.Count > 0)
                    Walk(ch, card, ref allChildrenFit, ref allLabelsFit, ref totalClipped, depth + 1);
            }
        }

    static string Trim(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 34 ? s.Substring(0, 34) + "..." : s;
    }
}
