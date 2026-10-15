// footer_probe.cs - checks the settings dialog's header/footer layout.
//
// The user reported overlapping, clipped text. Measuring the realized form
// (rather than reasoning about it) is the only reliable way to see this: the
// footer buttons are positioned from footer.Width at construction time, but the
// form's ClientSize is applied at the very end, so those positions can be
// computed against a stale width and land off-screen or on top of the status
// text.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ScreenPen;

static class FooterProbe
{
    static int passed, failed;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) passed++; else failed++;
        Console.WriteLine("  " + (ok ? "[PASS]" : "[FAIL]") + " " + name
            + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    static Control FindText(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Text == text) return c;
            Control r = FindText(c, text);
            if (r != null) return r;
        }
        return null;
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        try { Run(); }
        catch (Exception ex) { Console.WriteLine("EXCEPTION: " + ex); failed++; }

        Console.WriteLine();
        Console.WriteLine("=== " + passed + " passed, " + failed + " failed ===");
        return failed == 0 ? 0 : 1;
    }

    static void Run()
    {
        SettingsForm f = new SettingsForm(new Settings(), null);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        Console.WriteLine("form client = " + f.ClientSize.Width + "x" + f.ClientSize.Height);

        // ---- footer buttons must exist, be on-screen and not overlap ----
        Console.WriteLine();
        Console.WriteLine("--- 1. footer buttons ---");

        string[] names = new string[] { "保存设置", "取消", "恢复默认" };
        List<Control> buttons = new List<Control>();
        foreach (string n in names)
        {
            Control b = FindText(f, n);
            Check("the \"" + n + "\" button exists", b != null, "");
            if (b != null) buttons.Add(b);
        }

        if (buttons.Count == names.Length)
        {
            foreach (Control b in buttons)
            {
                Rectangle screen = f.RectangleToClient(
                    b.Parent.RectangleToScreen(b.Bounds));
                bool onScreen = screen.Left >= 0 && screen.Top >= 0
                    && screen.Right <= f.ClientSize.Width
                    && screen.Bottom <= f.ClientSize.Height;
                Check("the \"" + b.Text + "\" button is fully inside the window",
                      onScreen, screen.ToString());
            }

            for (int i = 0; i < buttons.Count; i++)
                for (int j = i + 1; j < buttons.Count; j++)
                {
                    Control a = buttons[i], b = buttons[j];
                    Rectangle ra = a.Parent.RectangleToScreen(a.Bounds);
                    Rectangle rb = b.Parent.RectangleToScreen(b.Bounds);
                    Check("\"" + a.Text + "\" and \"" + b.Text + "\" do not overlap",
                          !ra.IntersectsWith(rb), ra + " vs " + rb);
                }

            // The status label must not run under the buttons. It is
            // AutoEllipsis-clamped, so assert the control box is clear of the
            // buttons AND that it is wide enough to still show useful text.
            Control status = FindText(f, "提示：设置保存在 EXE 同目录的 .settings.json，绿色免安装。");
            if (status != null)
            {
                int leftmost = int.MaxValue;
                foreach (Control b in buttons)
                    leftmost = Math.Min(leftmost, b.Parent.RectangleToScreen(b.Bounds).Left);
                int statusRight = status.Parent.RectangleToScreen(status.Bounds).Right;
                Check("the status text does not run under the buttons",
                      statusRight <= leftmost,
                      "status ends at x=" + statusRight + ", buttons start at " + leftmost);
                Check("the status label is still wide enough to be readable",
                      status.Width >= 200, "width=" + status.Width);
            }
        }

        // ---- header: the title must not collide with its subtitle ----
        Console.WriteLine();
        Console.WriteLine("--- 2. header ---");
        Control title = FindText(f, "ScreenPen");
        Control sub = FindText(f, "屏幕画笔 · 实时缩放 · 截图");
        Check("the header title exists", title != null, "");
        Check("the header subtitle exists", sub != null, "");
        if (title != null && sub != null)
        {
            int titleBottom = title.Top + TextRenderer.MeasureText(title.Text, title.Font).Height;
            Console.WriteLine(string.Format("  title bottom={0}  subtitle top={1}",
                titleBottom, sub.Top));
            Check("the title does not overlap the subtitle",
                  titleBottom <= sub.Top,
                  string.Format("title bottom {0} vs subtitle top {1}", titleBottom, sub.Top));
        }

        f.Close();
        f.Dispose();
    }
}
