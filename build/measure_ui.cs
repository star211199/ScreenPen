// Prints the real, post-DPI-scale geometry of the settings dialog.
//
// Why: render_ui.cs output suggested the card column might still be 780px wide
// (the 96-DPI constant) inside a 1936px page, i.e. that Form.Scale did not reach
// the cards. Guessing from pixels in a PNG is unreliable, so measure instead.
using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class MeasureUi
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

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

    static int failures;

    static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what + " -> " + detail);
        if (!ok) failures++;
    }

    [STAThread]
    static int Main()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings cfg = new Settings();
        cfg.Clamp();

        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        Console.WriteLine("== scale ==");
        float dpiScale = 0f;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
        Console.WriteLine("  dpi scale = " + dpiScale);
        Console.WriteLine("  form client = " + f.ClientSize);

        Check("the form grew with DPI",
            dpiScale <= 1.01f || f.ClientSize.Width > 1300,
            f.ClientSize.Width + "x" + f.ClientSize.Height);

        IDictionary pages = (IDictionary)Field(f, "_pages");
        Panel page = (Panel)pages["画笔"];
        Call(f, "ShowPage", new object[] { "画笔" });
        Application.DoEvents();

        Console.WriteLine("== 画笔 page ==");
        Console.WriteLine("  page client = " + page.ClientSize);

        // The card column must occupy roughly the same fraction of the page at
        // any DPI, which only holds if the cards scaled too.
        Panel firstCard = null;
        foreach (Control c in page.Controls)
        {
            if (c is Panel) { firstCard = (Panel)c; break; }
        }
        if (firstCard == null) { Console.WriteLine("  no card found"); return 1; }

        Console.WriteLine("  first card = " + firstCard.Bounds);
        int expected = (int)Math.Round(780 * dpiScale);
        Check("card column scaled with DPI",
            Math.Abs(firstCard.Width - expected) <= 4,
            "width=" + firstCard.Width + " expected=" + expected);

        // A card's children must not poke out of it.
        int cardBottom = 0, cardRight = 0;
        foreach (Control c in firstCard.Controls)
        {
            if (c.Bottom > cardBottom) cardBottom = c.Bottom;
            if (c.Right > cardRight) cardRight = c.Right;
        }
        Check("card children stay inside the card",
            cardBottom <= firstCard.Height && cardRight <= firstCard.Width,
            "content " + cardRight + "x" + cardBottom
                + " vs card " + firstCard.Width + "x" + firstCard.Height);

        // Every visible button must be tall enough for its own caption at this DPI.
        int badButtons = 0;
        string worst = "";
        Walk(page, delegate(Control c)
        {
            Button b = c as Button;
            if (b == null || b.Parent == null) return;
            int need = TextRenderer.MeasureText(b.Text, b.Font).Height + 4;
            if (b.Height < need)
            {
                badButtons++;
                worst += "\n      \"" + b.Text + "\" " + b.Width + "x" + b.Height
                    + " needs h=" + need;
            }
        });
        Check("every button fits its caption", badButtons == 0,
            badButtons == 0 ? "all ok" : badButtons + " clipped" + worst);

        // Same for labels, including the palette index captions.
        int badLabels = 0;
        string worstL = "";
        Walk(page, delegate(Control c)
        {
            Label l = c as Label;
            if (l == null || l.Parent == null || l.Text.Length == 0) return;
            Size need = l.AutoSize
                ? l.PreferredSize
                : new Size(TextRenderer.MeasureText(l.Text, l.Font).Width,
                           TextRenderer.MeasureText(l.Text, l.Font).Height);
            bool tooShort = l.Height + 1 < need.Height;
            bool tooNarrow = l.Width + 1 < need.Width && !l.AutoSize;
            if (tooShort || tooNarrow)
            {
                badLabels++;
                worstL += "\n      \"" + l.Text + "\" " + l.Width + "x" + l.Height
                    + " needs " + need.Width + "x" + need.Height;
            }
        });
        Check("every label fits its text", badLabels == 0,
            badLabels == 0 ? "all ok" : badLabels + " clipped" + worstL);

        // The header subtitle must sit fully below the title and inside the header.
        Panel header = (Panel)Field(f, "_header");
        Label title = (Label)Field(f, "_headerTitle");
        Label sub = (Label)Field(f, "_headerSub");
        if (header != null && title != null && sub != null)
        {
            Console.WriteLine("== header ==");
            Console.WriteLine("  header h = " + header.Height
                + "  title = " + title.Bounds + "  sub = " + sub.Bounds);
            Check("subtitle is below the title", sub.Top >= title.Bottom - 1,
                "title.Bottom=" + title.Bottom + " sub.Top=" + sub.Top);
            Check("subtitle is inside the header", sub.Bottom <= header.Height,
                "sub.Bottom=" + sub.Bottom + " header.H=" + header.Height);
        }

        f.Close();
        f.Dispose();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void Walk(Control root, Action<Control> visit)
    {
        foreach (Control c in root.Controls)
        {
            visit(c);
            if (c.Controls.Count > 0) Walk(c, visit);
        }
    }
}
