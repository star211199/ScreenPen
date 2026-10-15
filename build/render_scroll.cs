// Renders a settings page scrolled to an arbitrary offset, so cards that sit
// below the viewport (the tier-1 additions) can actually be inspected.
//
// render_ui.cs renders each page at its full content height, which is great for
// spotting clipped text but produces a bitmap taller than the screen. This one
// instead keeps the real viewport and scrolls, which is what the user sees.
//
// Usage: render_scroll.exe <outdir> <pageName> <scrollY>
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class RenderScroll
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

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { }

        string outDir = args.Length > 0 ? args[0] : "build\\obj\\renderscroll";
        string pageName = args.Length > 1 ? args[1] : "截图";
        int scrollY = args.Length > 2 ? int.Parse(args[2]) : 0;
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings cfg = new Settings();
        cfg.Clamp();
        // Turn on the new features so the controls render in their enabled state.
        cfg.BeautifyEnabled = true;

        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        Call(f, "ShowPage", new object[] { pageName });
        Application.DoEvents();

        IDictionary pages = (IDictionary)Field(f, "_pages");
        Panel page = (Panel)pages[pageName];
        Console.WriteLine("page " + pageName + " viewport=" + page.ClientSize.Height +
                          " content=" + page.AutoScrollMinSize.Height);

        page.AutoScrollPosition = new Point(0, scrollY);
        Application.DoEvents();

        using (Bitmap bm = new Bitmap(page.ClientSize.Width, page.ClientSize.Height))
        {
            page.DrawToBitmap(bm, new Rectangle(0, 0, bm.Width, bm.Height));
            string path = Path.Combine(outDir, pageName + "_" + scrollY + ".png");
            bm.Save(path, ImageFormat.Png);
            Console.WriteLine("wrote " + Path.GetFullPath(path));
        }

        f.Close();
        f.Dispose();
        return 0;
    }
}
