// Renders the new clause-(3) pin window and the region selector's toolbar to
// PNGs, offscreen, so the rendered result can be inspected visually instead of
// inferred from geometry assertions.
//
// The cross-process route (synthesize the screenshot hotkey, drag a selection,
// click 贴图) proved flaky: hotkey delivery depends on the foreground window and
// on nothing else having grabbed the combo. Rendering the real controls
// in-process exercises the same OnPaint/DrawCloseButton code with no input
// dependency at all.
//
// Usage: render_pin.exe <outdir>
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

static class RenderPin
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

    static object Field(object o, string name)
    {
        FieldInfo fi = o.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return fi == null ? null : fi.GetValue(o);
    }

    static void Set(object o, string name, object val)
    {
        FieldInfo fi = o.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fi == null) throw new Exception("no field " + name);
        fi.SetValue(o, val);
    }

    static void Call(object o, string name, params object[] args)
    {
        MethodInfo mi = null;
        foreach (MethodInfo m in o.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (m.Name == name && m.GetParameters().Length == args.Length)
            {
                mi = m;
                break;
            }
        }
        if (mi == null) throw new Exception("no method " + name + "/" + args.Length);
        mi.Invoke(o, args);
    }

    static void Shot(Control c, string path)
    {
        Application.DoEvents();
        using (Bitmap bm = new Bitmap(c.Width, c.Height))
        {
            c.DrawToBitmap(bm, new Rectangle(0, 0, c.Width, c.Height));
            bm.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("  wrote " + Path.GetFileName(path) + "  " + c.Width + "x" + c.Height);
    }

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { }

        string outDir = args.Length > 0 ? args[0] : "build\\shots\\render";
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- a pin holding a recognisable image -------------------------------
        Console.WriteLine("--- PinForm ---");
        int iw = 640, ih = 400;
        Bitmap src = new Bitmap(iw, ih);
        using (Graphics g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(24, 28, 38));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 255)))
                g.FillRectangle(b, 0, 0, iw, 90);
            using (Font f = new Font("Segoe UI", 22f, FontStyle.Bold))
            using (SolidBrush w = new SolidBrush(Color.White))
                g.DrawString("钉在桌面 · Pinned", f, w, 22, 24);
            using (Pen p = new Pen(Color.FromArgb(52, 199, 89), 5f))
            {
                g.DrawEllipse(p, 60, 140, 200, 180);
                g.DrawRectangle(p, 320, 150, 240, 160);
                g.DrawLine(p, 60, 360, 580, 200);
            }
            using (Font f = new Font("Segoe UI", 13f))
            using (SolidBrush w = new SolidBrush(Color.FromArgb(220, 225, 235)))
                g.DrawString("滚轮缩放 · 拖动移动 · Esc 关闭 · Ctrl+C 复制 · Ctrl+S 保存",
                             f, w, 22, ih - 42);
        }

        PinForm pin = new PinForm(src, new Rectangle(400, 300, iw, ih));
        pin.StartPosition = FormStartPosition.Manual;
        pin.Location = new Point(-5000, -5000);
        pin.Show();
        Application.DoEvents();
        Console.WriteLine("  base size      = " + pin.TestBaseSize);
        Console.WriteLine("  scale          = " + pin.TestScale);
        Console.WriteLine("  image size     = " + pin.TestImage.Width + "x" + pin.TestImage.Height);
        Console.WriteLine("  close bounds   = " + pin.CloseButtonBounds);
        Shot(pin, Path.Combine(outDir, "30_pin_idle.png"));

        // Hover state: the close button only paints when the cursor is inside.
        Set(pin, "_mouseInside", true);
        pin.Invalidate();
        Shot(pin, Path.Combine(outDir, "31_pin_hover.png"));

        // Zoomed out and in, to show the scaler at both extremes of its range.
        Set(pin, "_mouseInside", false);
        pin.TestSetScale(0.45f);
        Shot(pin, Path.Combine(outDir, "32_pin_zoom_out.png"));
        pin.TestSetScale(2.0f);
        Shot(pin, Path.Combine(outDir, "33_pin_zoom_in.png"));
        Console.WriteLine("  after 0.45 -> 2.0: scale=" + pin.TestScale
            + " size=" + pin.Width + "x" + pin.Height
            + " (base " + pin.TestBaseSize.Width + "x" + pin.TestBaseSize.Height + ")");

        // The close button must stay inside even when the window is tiny.
        pin.TestSetScale(0.1f);
        Application.DoEvents();
        Rectangle cb = pin.CloseButtonBounds;
        bool inside = pin.ClientRectangle.Contains(new Point(cb.Left, cb.Top))
                   && pin.ClientRectangle.Contains(new Point(cb.Right - 1, cb.Bottom - 1));
        Console.WriteLine("  at min scale " + pin.Width + "x" + pin.Height
            + " close=" + cb + " inside=" + inside);
        Shot(pin, Path.Combine(outDir, "34_pin_min_scale.png"));

        pin.Close();
        pin.Dispose();
        Console.WriteLine("  closed and disposed cleanly");

        // ---- the region selector toolbar, at a couple of widths ---------------
        Console.WriteLine();
        Console.WriteLine("--- RegionSelectForm toolbar ---");
        Settings cfg = new Settings();
        cfg.Clamp();

        foreach (int w in new int[] { 2880, 1400, 900 })
        {
            RegionSelectForm sel = new RegionSelectForm(
                cfg, new Rectangle(0, 0, w, 800),
                new Bitmap(w, 800), RegionAction.PinToDesktop);
            sel.StartPosition = FormStartPosition.Manual;
            sel.Location = new Point(-6000, -6000);
            sel.Show();
            Application.DoEvents();

            // LayoutToolbar only runs once a selection exists, so make one the
            // same way the automated probe does; without it the buttons are all
            // zero-sized and the capture shows nothing.
            sel.SimulateDrag(new Point(w / 5, 140), new Point(w * 4 / 5, 620));
            Application.DoEvents();

            Rectangle tb = sel.ToolbarBoundsForTest;
            Console.WriteLine("  width=" + w + " toolbar=" + tb
                + " pinBtn=" + sel.PinButtonBounds
                + " sel=" + sel.SelectionForTest
                + " defaultAction=" + sel.DefaultActionForTest);
            Shot(sel, Path.Combine(outDir, string.Format("35_selector_{0}.png", w)));
            sel.Close();
            sel.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("wrote to " + Path.GetFullPath(outDir));
        return 0;
    }
}
