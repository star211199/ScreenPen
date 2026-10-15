using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenPen;

// Does the hotkey recorder really capture a keystroke?
//
// verify_upgrade.py drives it with synthetic OS input and reports
// 'hotkey box records a modifier-less F2' as FAIL, leaving the box at its old
// value. OS-level injection is unreliable here (the foreground lock makes
// SetForegroundWindow a no-op from a background process, so the keypress can
// land in the console instead). This probe drives the real control through the
// real WM_KEYDOWN path in-process, which is the only way to tell a product bug
// from an injection artifact.
static class HotkeyRecordProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    const uint KEYEVENTF_KEYUP = 0x0002;

    delegate bool EnumProc(IntPtr h, IntPtr l);

    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;

    static int _passed, _failed;

    static void Check(bool ok, string what, string detail)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what +
                          (detail != null && detail.Length > 0 ? "  (" + detail + ")" : ""));
    }

    static List<Control> Descendants(Control root)
    {
        List<Control> all = new List<Control>();
        foreach (Control c in root.Controls)
        {
            all.Add(c);
            all.AddRange(Descendants(c));
        }
        return all;
    }

    [STAThread]
    static void Main()
    {
        Console.WriteLine("=== hotkey recorder probe (in-process, real WM_KEYDOWN) ===");

        Settings cfg = SettingsStore.Load();
        SettingsForm f = new SettingsForm(cfg, delegate(Keys k) { return true; });
        // Never let a probe open a real modal dialog.
        f.PromptOverride = delegate(string a, string b, MessageBoxButtons c)
        {
            return DialogResult.Yes;
        };
        f.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);

        // Find every HotkeyBox: it is a read-only TextBox whose text looks like a
        // recorded combo.
        List<Control> all = Descendants(f);
        List<TextBox> boxes = new List<TextBox>();
        foreach (Control c in all)
        {
            TextBox tb = c as TextBox;
            if (tb != null && tb.ReadOnly) boxes.Add(tb);
        }
        Check(boxes.Count >= 15, "the settings form exposes the hotkey boxes",
              boxes.Count + " read-only TextBox(es)");

        if (boxes.Count == 0)
        {
            Console.WriteLine("=== " + _passed + " passed, " + _failed + " failed ===");
            Environment.Exit(1);
        }

        // The first hotkey row is the overlay toggle (Ctrl+Alt+D by default).
        TextBox first = boxes[0];
        string before = first.Text;
        Check(before.Length > 0, "the first box already shows a recorded combo",
              "text=" + before);

        // Focus it exactly as a click would, then send a real F2 keydown.
        first.Focus();
        Application.DoEvents();
        System.Threading.Thread.Sleep(150);
        Check(first.Focused, "the box can take focus", "Focused=" + first.Focused);

        IntPtr h = first.Handle;
        SendMessage(h, WM_KEYDOWN, (IntPtr)(int)Keys.F2, IntPtr.Zero);
        SendMessage(h, WM_KEYUP, (IntPtr)(int)Keys.F2, IntPtr.Zero);
        Application.DoEvents();
        System.Threading.Thread.Sleep(150);

        Check(first.Text == "F2", "a bare F2 is recorded as 'F2'",
              "got " + first.Text);

        // Backspace must clear the binding.
        SendMessage(h, WM_KEYDOWN, (IntPtr)(int)Keys.Back, IntPtr.Zero);
        SendMessage(h, WM_KEYUP, (IntPtr)(int)Keys.Back, IntPtr.Zero);
        Application.DoEvents();
        System.Threading.Thread.Sleep(120);
        Check(first.Text == "", "Backspace clears the binding", "got " + first.Text);

        // A modified combo must record its modifiers.
        //
        // HotkeyBox reads Control.ModifierKeys, which queries the *physical* key
        // state rather than the WM_KEYDOWN stream. A synthetic SendMessage of
        // VK_CONTROL therefore cannot satisfy it - only a real key state can.
        // keybd_event works here because this probe runs in-process and owns the
        // foreground, unlike verify_upgrade.py's cross-process injection.
        keybd_event((byte)Keys.ControlKey, 0, 0, UIntPtr.Zero);          // down
        // Pump the queue: GetKeyState (which Control.ModifierKeys wraps) only
        // reflects the physical key once this thread has processed the input.
        for (int i = 0; i < 10; i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(30);
        }
        bool ctrlSeen = (Control.ModifierKeys & Keys.Control) == Keys.Control;
        Console.WriteLine("  (modifier state after keybd_event: Ctrl=" + ctrlSeen + ")");
        SendMessage(h, WM_KEYDOWN, (IntPtr)(int)Keys.F2, IntPtr.Zero);
        SendMessage(h, WM_KEYUP, (IntPtr)(int)Keys.F2, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        keybd_event((byte)Keys.ControlKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Application.DoEvents();
        System.Threading.Thread.Sleep(150);
        Check(first.Text == "Ctrl+F2", "Ctrl+F2 records both the modifier and the key",
              "got " + first.Text);

        // Modifier-only presses must be ignored so a combo can be built.
        string keep = first.Text;
        SendMessage(h, WM_KEYDOWN, (IntPtr)(int)Keys.ShiftKey, IntPtr.Zero);
        Application.DoEvents();
        System.Threading.Thread.Sleep(120);
        Check(first.Text == keep, "a modifier-only press does not overwrite the binding",
              "got " + first.Text);

        // A bare Tab must be recordable rather than navigating away.
        SendMessage(h, WM_KEYDOWN, (IntPtr)(int)Keys.Tab, IntPtr.Zero);
        SendMessage(h, WM_KEYUP, (IntPtr)(int)Keys.Tab, IntPtr.Zero);
        Application.DoEvents();
        System.Threading.Thread.Sleep(120);
        Check(first.Text == "Tab", "a bare Tab is recorded instead of navigating away",
              "got " + first.Text);

        Console.WriteLine();
        Console.WriteLine("=== " + _passed + " passed, " + _failed + " failed ===");
        f.Close();
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
