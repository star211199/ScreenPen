using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ScreenPen;

// Reproduces the failure mode that made tools_probe flaky: another process owns
// the foreground when the overlay activates. Before ForceForeground() the
// overlay stayed behind, Focus() was refused, and the inline text editor was
// destroyed by its own LostFocus handler the moment it was created.
static class HostileFocusProbe
{
    static int fails;

    static void Check(bool ok, string name, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Re-entrant mode: this same exe is launched again as the "hostile app".
        // Win11's Notepad is a Store app, so Process.Start("notepad.exe") hands
        // back a launcher stub whose Kill() does NOT close the real window - that
        // leaked a stray Notepad onto the user's desktop. Blocking with our own
        // exe gives us a pid we fully own and can always terminate.
        if (args.Length > 0 && args[0] == "--blocker") { RunBlocker(); return; }

        Application.EnableVisualStyles();

        // A separate process takes the foreground and holds it.
        Process blocker = Process.Start(new ProcessStartInfo(
            Application.ExecutablePath, "--blocker")
        {
            UseShellExecute = false
        });
        System.Threading.Thread.Sleep(2500);

        IntPtr before = Native.GetForegroundWindow();
        uint ourPid = (uint)Process.GetCurrentProcess().Id;
        Console.WriteLine("foreground held by pid " + PidOf(before) +
                          " (our pid " + ourPid + ")");
        Console.WriteLine("blocker pid " + blocker.Id);
        // Any process other than us is hostile enough: it means we did not last
        // receive input, so the window manager is free to refuse our request.
        Check(PidOf(before) != 0 && PidOf(before) != ourPid,
              "a foreign process really does own the foreground",
              "fg pid=" + PidOf(before));

        Settings cfg = new Settings();
        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        ov.ActivateOverlay(false);
        Application.DoEvents();
        System.Threading.Thread.Sleep(500);
        Application.DoEvents();

        Check(Native.GetForegroundWindow() == ov.Handle,
              "the overlay takes the foreground away from the other app",
              "fg=" + Native.GetForegroundWindow() + " ov=" + ov.Handle);

        ov.TestBeginText(new PointF(700, 600));
        Application.DoEvents();
        Check(ov.IsEditingTextForTest(),
              "the text editor survives with a hostile foreground",
              "IsEditing=" + ov.IsEditingTextForTest());

        if (ov.IsEditingTextForTest())
        {
            ov.TestSetTextEditorContent("抢焦点也能输入");
            Application.DoEvents();
            ov.CommitTextEditing(true);
            Application.DoEvents();
            Check(ov.StrokeCount == 1, "the typed label becomes a stroke",
                  "strokes=" + ov.StrokeCount);
        }

        ov.Close();
        try { blocker.Kill(); } catch { }
        try { blocker.WaitForExit(3000); } catch { }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    /// <summary>
    /// The hostile foreground holder. Shows a window and pumps messages until
    /// killed, so the foreground lock genuinely applies to the probe process.
    /// </summary>
    static void RunBlocker()
    {
        Form f = new Form();
        f.Text = "hostile foreground holder";
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.Size = new Size(320, 120);
        f.Shown += delegate { Native.ForceForeground(f.Handle); };
        Application.Run(f);
    }

    static uint PidOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return 0;
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        return pid;
    }
}
