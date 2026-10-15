// Asserts the settings dialog's card layout is sound without needing eyes on a
// PNG: every card must fully contain its children, no two cards may overlap,
// and no page may reserve a large band of dead space.
//
// Why a probe instead of a screenshot: render_ui.exe produces bitmaps that a
// human (or an image-capable model) has to interpret. The properties that
// actually matter - containment, overlap, dead space - are exact arithmetic on
// the control tree, so they can be asserted and regression-tested.
//
// Usage: layout_cards.exe [--verbose]
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class LayoutCards
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

    static int _pass, _fail;

    static void Check(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + name); }
        else { _fail++; Console.WriteLine("[FAIL] " + name + "  -- " + detail); }
    }

    static object Field(object o, string name)
    {
        FieldInfo fi = o.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return fi == null ? null : fi.GetValue(o);
    }

    static void Call(object o, string name, object[] args)
    {
        MethodInfo mi = o.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (mi == null) throw new Exception("no method " + name);
        mi.Invoke(o, args);
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool verbose = Array.IndexOf(args, "--verbose") >= 0;
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings cfg = new Settings();
        cfg.Clamp();
        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        IDictionary pages = (IDictionary)Field(f, "_pages");
        Check(pages != null && pages.Count >= 6,
            "the dialog exposes every settings page",
            "pages=" + (pages == null ? "null" : pages.Count.ToString()));

        int cardsSeen = 0;
        foreach (DictionaryEntry e in pages)
        {
            string name = (string)e.Key;
            Call(f, "ShowPage", new object[] { name });
            Application.DoEvents();

            Panel page = (Panel)pages[name];
            List<Panel> cards = new List<Panel>();
            foreach (Control c in page.Controls)
            {
                Panel card = c as Panel;
                if (card != null) cards.Add(card);
            }
            cards.Sort(delegate(Panel a, Panel b) { return a.Top.CompareTo(b.Top); });

            bool contained = true, noOverlap = true;
            int worstDead = -1;
            string worstDetail = "";

            foreach (Panel card in cards)
            {
                cardsSeen++;
                foreach (Control cc in card.Controls)
                {
                    // A child that pokes past its card is the "文字被遮挡" class of
                    // bug: the card border cuts the control, or the next card
                    // paints over it.
                    if (cc.Bottom > card.Height || cc.Right > card.Width ||
                        cc.Top < 0 || cc.Left < 0)
                    {
                        contained = false;
                        Console.WriteLine(string.Format(
                            "       page {0} card h={1} clips {2} bounds={3} text={4}",
                            name, card.Height, cc.GetType().Name, cc.Bounds,
                            (cc.Text ?? "").Replace("\n", "\\n")));
                    }
                }

                int contentBottom = 0;
                foreach (Control cc in card.Controls)
                    if (cc.Bottom > contentBottom) contentBottom = cc.Bottom;

                int dead = card.Height - contentBottom;
                if (dead > worstDead)
                {
                    worstDead = dead;
                    worstDetail = string.Format("card top={0} h={1} content={2}",
                        card.Top, card.Height, contentBottom);
                }
            }

            for (int i = 0; i + 1 < cards.Count; i++)
            {
                if (cards[i].Bottom > cards[i + 1].Top)
                {
                    noOverlap = false;
                    Console.WriteLine(string.Format(
                        "       page {0}: card {1} (bottom {2}) overlaps card {3} (top {4})",
                        name, i, cards[i].Bottom, i + 1, cards[i + 1].Top));
                }
            }

            Check(contained, name + ": every control fits inside its card", "");
            Check(noOverlap, name + ": cards do not overlap each other", "");

            // Dead space is cosmetic, but a card reserving more than a blank
            // line-and-a-half below its content is the visible "empty band"
            // defect, so cap it. 40px at 192 DPI is 20 logical px.
            Check(worstDead <= 40,
                name + ": no card reserves a large empty band",
                worstDetail);

            if (verbose)
                foreach (Panel card in cards)
                {
                    int contentBottom = 0;
                    foreach (Control cc in card.Controls)
                        if (cc.Bottom > contentBottom) contentBottom = cc.Bottom;
                    Console.WriteLine(string.Format(
                        "       card top={0,5} h={1,5} content={2,5} dead={3,4}",
                        card.Top, card.Height, contentBottom,
                        card.Height - contentBottom));
                }
        }

        Check(cardsSeen >= 15,
            "the dialog actually built its cards", "cards=" + cardsSeen);

        // The scaled layout must still scroll: a page taller than its viewport
        // has to publish a matching AutoScrollMinSize or its lower cards are
        // unreachable.
        bool scrollOk = true;
        foreach (DictionaryEntry e in pages)
        {
            Panel page = (Panel)pages[e.Key];
            int bottom = 0;
            foreach (Control c in page.Controls)
                if (c.Bottom > bottom) bottom = c.Bottom;
            if (bottom > page.ClientSize.Height &&
                page.AutoScrollMinSize.Height < bottom)
            {
                scrollOk = false;
                Console.WriteLine(string.Format(
                    "       page {0}: content bottom {1} but AutoScrollMinSize {2}",
                    e.Key, bottom, page.AutoScrollMinSize.Height));
            }
        }
        Check(scrollOk, "every overflowing page can scroll to its last card", "");

        f.Close();
        f.Dispose();

        Console.WriteLine();
        Console.WriteLine(string.Format("{0} PASS / {1} FAIL", _pass, _fail));
        return _fail == 0 ? 0 : 1;
    }
}
