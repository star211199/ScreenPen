// Renders the real SettingsForm to PNGs so a human (or the model) can SEE the
// clipping instead of trusting an arithmetic probe.
//
// Why this exists: several rounds of numeric probes reported "0 clipped" while
// the user kept reporting 文字被遮挡. Arithmetic on Bounds/MeasureText is only as
// good as the assumptions baked into it; a rendered bitmap has no assumptions.
//
// Usage: render_ui.exe <outdir>
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class RenderUi
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int i);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

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
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { }

        IntPtr dc = GetDC(IntPtr.Zero);
        int dpi = GetDeviceCaps(dc, 90);
        ReleaseDC(IntPtr.Zero, dc);

        string outDir = args.Length > 0 ? args[0] : "build\\shots\\render";
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings cfg = new Settings();
        cfg.Clamp();

        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        Console.WriteLine("system DPI          = " + dpi);
        Console.WriteLine("form ClientSize     = " + f.ClientSize);
        Console.WriteLine("form AutoScaleMode  = " + f.AutoScaleMode);
        Console.WriteLine("form font           = " + f.Font);

        // 1. the whole window as laid out
        Save(f, Path.Combine(outDir, "00_window.png"));

        // 2. each page, rendered at full content height so nothing is hidden by
        //    the scroll viewport. This is the view the user never gets but which
        //    proves whether the text itself is intact.
        IDictionary pages = (IDictionary)Field(f, "_pages");
        ArrayList names = new ArrayList();
        foreach (DictionaryEntry e in pages) names.Add(e.Key);

        int i = 1;
        foreach (object key in names)
        {
            string name = (string)key;
            Call(f, "ShowPage", new object[] { name });
            Application.DoEvents();

            Panel page = (Panel)pages[name];
            int full = Math.Max(page.AutoScrollMinSize.Height, page.ClientSize.Height);
            Console.WriteLine(string.Format("  page {0,-6} viewport={1} content={2}",
                name, page.ClientSize.Height, full));

            Save(page, Path.Combine(outDir,
                string.Format("{0:00}_page_{1}.png", i, name)));
            i++;
        }

        f.Close();
        f.Dispose();
        Console.WriteLine("wrote to " + Path.GetFullPath(outDir));
        return 0;
    }

    /// <summary>
    /// DrawToBitmap only paints the visible client area of a scrollable control,
    /// so a tall page is rendered by temporarily making the control as tall as
    /// its content. Restored afterwards.
    /// </summary>
    static void Save(Control c, string path)
    {
        Size original = c.Size;
        bool scrolled = c is ScrollableControl;
        Point scroll = Point.Empty;
        if (scrolled) scroll = ((ScrollableControl)c).AutoScrollPosition;

        if (scrolled)
        {
            ScrollableControl sc = (ScrollableControl)c;
            sc.AutoScroll = false;
            int h = Math.Max(c.ClientSize.Height, sc.AutoScrollMinSize.Height);
            c.Size = new Size(c.Width, h + (c.Height - c.ClientSize.Height));
            Application.DoEvents();
        }

        using (Bitmap bm = new Bitmap(c.Width, c.Height))
        {
            c.DrawToBitmap(bm, new Rectangle(0, 0, c.Width, c.Height));
            bm.Save(path, ImageFormat.Png);
        }

        if (scrolled)
        {
            ScrollableControl sc = (ScrollableControl)c;
            c.Size = original;
            sc.AutoScroll = true;
            sc.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            Application.DoEvents();
        }
    }
}
