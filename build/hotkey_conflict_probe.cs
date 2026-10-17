// Verifies the hotkey-conflict handling that was trapping the user.
//
// The reported failure was: setting 画笔 = E while 清空标注 = E produced
//   "以下快捷键设置有问题，尚未保存：·快捷键冲突：「清空标注」与「画笔」都是E。"
// and threw away every other edit with no way to proceed.
//
// This probe asserts the fixed behaviour:
//   1. a clean config reports no clash and a positive status line
//   2. typing a duplicate surfaces it LIVE in the readout, before saving
//   3. the duplicate is savable after the user confirms (Result is produced)
//   4. a genuinely broken entry (bare letter as a GLOBAL key) still blocks
//   5. cancelling the duplicate prompt does NOT close the dialog
//   6. the validation and the readout agree on every row
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScreenPen;

class HotkeyConflictProbe
{
    static int _pass, _fail;

    static void Check(string what, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    // =====================================================================
    //  Dialog capture
    //
    //  The probe answers dialogs through SettingsForm.PromptOverride, the
    //  test seam added for exactly this purpose. It must NOT drive real
    //  MessageBox windows: doing so pops modal windows onto the desktop of
    //  whoever is running the tests and then hangs waiting for a click.
    // =====================================================================
    sealed class Prompts
    {
        public readonly List<string> Texts = new List<string>();
        public readonly List<string> Captions = new List<string>();
        public DialogResult Answer = DialogResult.OK;

        public DialogResult Handle(string text, string caption, MessageBoxButtons buttons)
        {
            Texts.Add(text);
            Captions.Add(caption);
            return Answer;
        }

        public bool Saw(string needle)
        {
            foreach (string s in Texts)
                if (s != null && s.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public string Last()
        {
            return Texts.Count == 0 ? "" : Texts[Texts.Count - 1];
        }
    }

    static IEnumerable<Control> Walk(Control c)
    {
        foreach (Control k in c.Controls)
        {
            yield return k;
            foreach (Control d in Walk(k)) yield return d;
        }
    }

    static List<TextBox> KeyBoxes(Form f)
    {
        // ShowPage() deliberately detaches every page but the current one from
        // _content, so walking the form's control tree only reaches the page
        // that happens to be selected. That was invisible while every key row
        // lived on the 快捷键 page; the moment a key row moved to another page
        // (浮标) the walk silently under-counted and this check failed for a
        // reason that had nothing to do with the invariant it guards. Walk the
        // page dictionary as well, so "every row built in the UI" really means
        // every row.
        var list = new List<TextBox>();
        var seen = new HashSet<TextBox>();

        Action<Control> add = delegate(Control root)
        {
            foreach (Control c in Walk(root))
            {
                TextBox tb = c as TextBox;
                if (tb != null && tb.ReadOnly && seen.Add(tb)) list.Add(tb);
            }
        };

        add(f);
        FieldInfo pagesField = typeof(SettingsForm).GetField("_pages",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (pagesField != null)
        {
            var pages = pagesField.GetValue(f) as System.Collections.IDictionary;
            if (pages != null)
            {
                foreach (System.Collections.DictionaryEntry e in pages)
                {
                    Control page = e.Value as Control;
                    if (page != null) add(page);
                }
            }
        }
        return list;
    }

    // Reach the private box fields by name so the probe drives the real rows.
    static TextBox Field(Form f, string name)
    {
        FieldInfo fi = typeof(SettingsForm).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return fi == null ? null : (TextBox)fi.GetValue(f);
    }

    // The readout label sitting to the right of a given box.
    static string Readout(Form f, TextBox box)
    {
        FieldInfo fi = typeof(SettingsForm).GetField("_readouts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var map = fi.GetValue(f) as System.Collections.IDictionary;
        if (map == null) return null;
        Label l = map[box] as Label;
        return l == null ? null : l.Text;
    }

    static string Status(Form f)
    {
        FieldInfo fi = typeof(SettingsForm).GetField("_status",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Label l = (Label)fi.GetValue(f);
        return l == null ? null : l.Text;
    }

    // Invoke the private OnSave. Returns true when it committed (Result set).
    static bool TrySave(Form f)
    {
        MethodInfo m = typeof(SettingsForm).GetMethod("OnSave",
            BindingFlags.Instance | BindingFlags.NonPublic);
        m.Invoke(f, null);
        Application.DoEvents();
        return f.DialogResult == DialogResult.OK;
    }

    static SettingsForm Make(Settings cfg, Prompts p)
    {
        SettingsForm f = new SettingsForm(cfg, delegate { return true; });
        f.PromptOverride = p.Handle;
        f.Show();
        Application.DoEvents();
        return f;
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---------------------------------------------------------------
        Console.WriteLine("=== 1. clean config: no clash, positive status ===");
        var p1 = new Prompts();
        SettingsForm f1 = Make(new Settings(), p1);

        TextBox pen = Field(f1, "_hkPen");
        TextBox clear = Field(f1, "_hkClear");
        Check("pen box exists", pen != null);
        Check("clear box exists", clear != null);
        Check("default PenKey is P", pen.Text == "P");
        Check("default ClearKey is E", clear.Text == "E");
        Check("pen readout is a clean tick", Readout(f1, pen) == "✓ 单键可用");
        Check("clear readout is a clean tick", Readout(f1, clear) == "✓ 单键可用");
        Console.WriteLine("        status: " + Status(f1));
        Check("status says no conflict", Status(f1) != null && Status(f1).Contains("没有冲突"));
        Check("no prompt was raised for a clean config", p1.Texts.Count == 0);
        f1.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 2. duplicate surfaces LIVE in the readout ===");
        var p2 = new Prompts();
        SettingsForm f2 = Make(new Settings(), p2);
        pen = Field(f2, "_hkPen");
        clear = Field(f2, "_hkClear");
        pen.Text = "E";            // now 画笔 = E and 清空标注 = E
        Application.DoEvents();
        string penRo = Readout(f2, pen);
        string clearRo = Readout(f2, clear);
        Console.WriteLine("        pen readout   = " + penRo);
        Console.WriteLine("        clear readout = " + clearRo);
        Check("duplicate is flagged on the edited row", penRo != null && penRo.Contains("重复"));
        Check("duplicate is flagged on the other row", clearRo != null && clearRo.Contains("占用"));
        Check("status warns about the duplicate",
              Status(f2) != null && Status(f2).Contains("重复"));
        Console.WriteLine("        status: " + Status(f2));
        Check("the live readout alone raises no modal prompt", p2.Texts.Count == 0);
        f2.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 3. the duplicate IS savable (no dead end) ===");
        var p3 = new Prompts();
        p3.Answer = DialogResult.Yes;
        SettingsForm f3 = Make(new Settings(), p3);
        pen = Field(f3, "_hkPen");
        pen.Text = "E";
        Application.DoEvents();

        bool committed = TrySave(f3);
        Check("a duplicate prompt was shown", p3.Saw("快捷键重复"));
        Check("saving with a duplicate commits", committed);
        Check("the duplicate value was actually stored",
              f3.Result != null && f3.Result.PenKey == "E" && f3.Result.ClearKey == "E");
        f3.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 4. a broken GLOBAL key still blocks the save ===");
        var p4 = new Prompts();
        SettingsForm f4 = Make(new Settings(), p4);
        TextBox toggle = Field(f4, "_hkToggle");
        toggle.Text = "Q";   // bare letter as a global hotkey
        Application.DoEvents();
        Check("bare global key flagged in the readout",
              Readout(f4, toggle) != null && Readout(f4, toggle).Contains("修饰键"));

        bool badCommitted = TrySave(f4);
        Check("an invalid global key was warned about", p4.Saw("抢走打字输入"));
        Check("an invalid global key does NOT commit", !badCommitted);
        Check("nothing was written for the broken config", f4.Result == null);
        f4.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 5. cancelling the duplicate prompt keeps the dialog open ===");
        var p5 = new Prompts();
        p5.Answer = DialogResult.No;
        SettingsForm f5 = Make(new Settings(), p5);
        Field(f5, "_hkPen").Text = "E";
        Application.DoEvents();

        bool cancelled = TrySave(f5);
        Check("the duplicate prompt was shown", p5.Saw("快捷键重复"));
        Check("declining the duplicate does not commit", !cancelled);
        Check("the dialog is still usable", !f5.IsDisposed);
        Check("no settings were written when declining", f5.Result == null);
        f5.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 6. readout and validation agree on every row ===");
        var p6 = new Prompts();
        SettingsForm f6 = Make(new Settings(), p6);
        var boxes = KeyBoxes(f6);
        Console.WriteLine("        key boxes found: " + boxes.Count);

        // Compare the boxes found in the control tree against the form's own
        // binding list rather than a hard-coded number. A literal count (this
        // was 19) breaks every time a rebindable key is added, and it would not
        // catch the real defect it is meant to guard: a row built in the UI but
        // never added to _bindings, which is silently excluded from both the
        // live readout and the save-time conflict check.
        var bindings = (System.Collections.ICollection)typeof(SettingsForm)
            .GetField("_bindings", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(f6);
        Console.WriteLine("        bindings registered: " + bindings.Count);
        Check("every key row is registered in _bindings",
              boxes.Count == bindings.Count);
        Check("the dialog has a non-trivial number of key rows", boxes.Count >= 19);

        // Make two distinct clashes and confirm each row's readout is non-clean
        // exactly when that row shares its key with another row.
        Field(f6, "_hkPen").Text = "E";      // clashes with 清空标注
        Field(f6, "_hkLine").Text = "R";     // clashes with 矩形
        Application.DoEvents();

        int flagged = 0;
        foreach (TextBox b in boxes)
        {
            string ro = Readout(f6, b) ?? "";
            bool shares = false;
            foreach (TextBox o in boxes)
            {
                if (o == b) continue;
                if (o.Text.Trim().Length > 0 && o.Text.Trim() == b.Text.Trim()) shares = true;
            }
            if (shares && (ro.Contains("重复") || ro.Contains("占用"))) flagged++;
        }
        Console.WriteLine("        rows sharing a key and flagged: " + flagged);
        Check("both clashing pairs are flagged on all four rows", flagged == 4);
        Check("status counts the duplicates",
              Status(f6) != null && Status(f6).Contains("4"));
        f6.Close();

        // ---------------------------------------------------------------
        Console.WriteLine("\n=== 7. the reported 画笔=E / 清空标注=E case is recoverable ===");
        var p7 = new Prompts();
        p7.Answer = DialogResult.Yes;
        SettingsForm f7 = Make(new Settings(), p7);
        Field(f7, "_hkPen").Text = "E";
        // Also change an unrelated field, to prove the whole save is not discarded.
        FieldInfo numFi = typeof(SettingsForm).GetField("_numWidth",
            BindingFlags.Instance | BindingFlags.NonPublic);
        ((NumericUpDown)numFi.GetValue(f7)).Value = 7;
        Application.DoEvents();

        bool ok7 = TrySave(f7);
        Check("the previously-refused save now goes through", ok7);
        Check("the unrelated edit was preserved too",
              f7.Result != null && f7.Result.PenWidth == 7);
        Check("the clashing key was stored as asked",
              f7.Result != null && f7.Result.PenKey == "E");
        f7.Close();

        Console.WriteLine();
        Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
        return _fail == 0 ? 0 : 1;
    }
}
