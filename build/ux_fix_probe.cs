// Probe for the four reported UX defects. One section each, because the
// failures were independent and a combined probe would hide which one regressed:
//
//   1. "OCR / GIF 在截图工具条上找不到"   -> the toolbar must carry both, and
//      clicking them must commit the same action the keyboard shortcut does.
//   2. "框选之后不能移动/调整候选框"      -> a finished selection must be
//      movable and resizable from its handles, and stay on screen.
//   3. "不知道生成的动图在哪里"           -> the result window must show the
//      path, be selectable, and offer 复制路径.
//   4. "标注时滚轮不能滚动页面"           -> the wheel must be forwarded to the
//      document, and the overlay must never be left click-through afterwards.
//      Also: the README's "Esc 停止" recording shortcut must really exist.
using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenPen;

static class UxFixProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    static void Pump(int ms)
    {
        int end = Environment.TickCount + ms;
        while (Environment.TickCount - end < 0)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
    }

    static RegionSelectForm NewSelector(out Settings cfg, out Bitmap backdrop,
                                        RegionAction def)
    {
        cfg = new Settings();
        cfg.Clamp();
        Rectangle desktop = new Rectangle(0, 0, 1280, 800);
        backdrop = new Bitmap(desktop.Width, desktop.Height);
        using (Graphics g = Graphics.FromImage(backdrop)) g.Clear(Color.SlateGray);
        RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, def);
        sel.StartPosition = FormStartPosition.Manual;
        sel.Location = new Point(-7000, -7000);
        sel.Show();
        Pump(120);
        return sel;
    }

    [STAThread]
    static int Main()
    {
        try { Application.EnableVisualStyles(); }
        catch { }

        // =================================================================
        Console.WriteLine("=== UX FIX PROBE ===");
        Console.WriteLine();
        Console.WriteLine("-- 1. the toolbar exposes OCR and GIF --");

        Settings cfg;
        Bitmap backdrop;
        RegionSelectForm sel = NewSelector(out cfg, out backdrop, RegionAction.SaveToFile);
        try
        {
            sel.SimulateDrag(new Point(60, 60), new Point(1000, 700));
            Pump(80);

            Check(sel.ToolbarButtonCountForTest == 6,
                  "the toolbar carries six buttons", sel.ToolbarButtonCountForTest.ToString());
            Check(sel.OcrButtonBounds.Width > 0 && sel.GifButtonBounds.Width > 0,
                  "the 识别 and GIF buttons are laid out",
                  sel.OcrButtonBounds + " / " + sel.GifButtonBounds);
            Check(sel.OcrLabelForTest.Contains("识别"),
                  "the OCR button says what it does", sel.OcrLabelForTest);
            Check(sel.GifLabelForTest.Contains("GIF"),
                  "the GIF button says what it does", sel.GifLabelForTest);

            // The two new buttons must sit between 贴图 and 取消, so the row reads
            // 保存 复制 贴图 识别 GIF 取消 rather than the new ones being tacked on
            // somewhere arbitrary.
            bool ordered =
                sel.SaveButtonBounds.Right <= sel.CopyButtonBounds.Left + 1 &&
                sel.CopyButtonBounds.Right <= sel.PinButtonBounds.Left + 1 &&
                sel.PinButtonBounds.Right <= sel.OcrButtonBounds.Left + 1 &&
                sel.OcrButtonBounds.Right <= sel.GifButtonBounds.Left + 1 &&
                sel.GifButtonBounds.Right <= sel.CancelButtonBounds.Left + 1;
            Check(ordered, "the buttons are ordered 保存/复制/贴图/识别/GIF/取消", "");
        }
        finally
        {
            try { sel.Close(); } catch { }
            sel.Dispose();
        }

        // Each of the two new buttons must commit its own action through the
        // real mouse path, not just through the key handler.
        RegionAction[] want = new RegionAction[] { RegionAction.Ocr, RegionAction.RecordGif };
        int[] idx = new int[] { 3, 4 };
        for (int i = 0; i < idx.Length; i++)
        {
            RegionSelectForm s2 = NewSelector(out cfg, out backdrop, RegionAction.SaveToFile);
            try
            {
                s2.SimulateDrag(new Point(60, 60), new Point(1000, 700));
                Pump(60);
                bool clicked = s2.ClickToolbarButtonForTest(idx[i]);
                Check(clicked && s2.ActionTaken == want[i],
                      "clicking button " + idx[i] + " commits " + want[i],
                      "clicked=" + clicked + " action=" + s2.ActionTaken);
            }
            finally
            {
                try { s2.Close(); } catch { }
                s2.Dispose();
            }
        }
        backdrop.Dispose();

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 2. a finished selection can be moved and resized --");

        RegionSelectForm sr = NewSelector(out cfg, out backdrop, RegionAction.SaveToFile);
        try
        {
            sr.SimulateDrag(new Point(200, 150), new Point(700, 500));
            Pump(60);
            Rectangle start = sr.SelectionForTest;
            Check(start.Width == 500 && start.Height == 350,
                  "the drag produced the expected region", start.ToString());

            Check(sr.HandleCountForTest == 8, "eight resize handles are offered",
                  sr.HandleCountForTest.ToString());

            // Every handle must sit on the selection outline, centred on the
            // border it belongs to - not somewhere else in the window, which
            // would make the affordance a lie.
            bool onOutline = true;
            string offBy = "";
            int cxMid = start.Left + start.Width / 2;
            int cyMid = start.Top + start.Height / 2;
            for (int i = 0; i < sr.HandleCountForTest; i++)
            {
                Rectangle h = sr.HandleBoundsForTest(i);
                Point c = new Point(h.Left + h.Width / 2, h.Top + h.Height / 2);
                bool xOk = c.X == start.Left || c.X == start.Right || c.X == cxMid;
                bool yOk = c.Y == start.Top || c.Y == start.Bottom || c.Y == cyMid;
                if (!xOk || !yOk || !h.IntersectsWith(start))
                {
                    onOutline = false;
                    offBy += "#" + i + "=" + c + " ";
                }
            }
            Check(onOutline,
                  "every handle is centred on the selection border it belongs to",
                  offBy);

            // ---- move: drag the body, the size must not change.
            Point body = new Point(start.Left + start.Width / 2, start.Top + start.Height / 2);
            sr.SimulatePressAndDrag(body, new Point(body.X + 120, body.Y + 60));
            Pump(40);
            Rectangle moved = sr.SelectionForTest;
            sr.SimulateRelease(new Point(body.X + 120, body.Y + 60));
            Check(moved.Width == start.Width && moved.Height == start.Height,
                  "moving the box keeps its size",
                  moved.Width + "x" + moved.Height + " was " + start.Width + "x" + start.Height);
            Check(moved.Left == start.Left + 120 && moved.Top == start.Top + 60,
                  "moving the box moves it by exactly the drag distance",
                  moved.Location + " from " + start.Location);

            // ---- resize: drag the bottom-right corner outwards.
            Rectangle beforeResize = sr.SelectionForTest;
            Point corner = new Point(beforeResize.Right - 2, beforeResize.Bottom - 2);
            sr.SimulatePressAndDrag(corner, new Point(corner.X + 80, corner.Y + 40));
            Pump(40);
            sr.SimulateRelease(new Point(corner.X + 80, corner.Y + 40));
            Rectangle bigger = sr.SelectionForTest;
            Check(bigger.Right == beforeResize.Right + 80 && bigger.Bottom == beforeResize.Bottom + 40,
                  "dragging the SE handle grows only the right and bottom edges",
                  bigger.ToString() + " from " + beforeResize);
            Check(bigger.Left == beforeResize.Left && bigger.Top == beforeResize.Top,
                  "the anchored corner does not move",
                  bigger.Location + " vs " + beforeResize.Location);

            // ---- resize: drag the top-left corner inwards.
            Rectangle beforeNw = sr.SelectionForTest;
            Point nw = new Point(beforeNw.Left + 1, beforeNw.Top + 1);
            sr.SimulatePressAndDrag(nw, new Point(nw.X + 50, nw.Y + 30));
            Pump(40);
            sr.SimulateRelease(new Point(nw.X + 50, nw.Y + 30));
            Rectangle smaller = sr.SelectionForTest;
            Check(smaller.Left == beforeNw.Left + 50 && smaller.Top == beforeNw.Top + 30,
                  "dragging the NW handle moves only the left and top edges",
                  smaller.ToString() + " from " + beforeNw);
            Check(smaller.Right == beforeNw.Right && smaller.Bottom == beforeNw.Bottom,
                  "the opposite corner stays put",
                  new Point(smaller.Right, smaller.Bottom) +
                  " vs " + new Point(beforeNw.Right, beforeNw.Bottom));

            // ---- clamping: a move that would leave the window must be contained.
            Rectangle r = sr.SelectionForTest;
            Point c2 = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            sr.SimulatePressAndDrag(c2, new Point(-4000, -4000));
            Pump(40);
            sr.SimulateRelease(new Point(-4000, -4000));
            Rectangle clamped = sr.SelectionForTest;
            Rectangle client = new Rectangle(0, 0, sr.ClientSize.Width, sr.ClientSize.Height);
            Check(client.Contains(clamped),
                  "a move that would leave the window is clamped back inside",
                  clamped + " in " + client);
            Check(clamped.Width == r.Width && clamped.Height == r.Height,
                  "clamping does not resize the box",
                  clamped.Width + "x" + clamped.Height + " vs " + r.Width + "x" + r.Height);

            // ---- the handles must not be offered on a selection too small to
            //      grab, or they would cover the whole thing and make moving
            //      impossible.
            Check(sr.HandlesVisibleForTest,
                  "handles are painted on a normal selection",
                  sr.SelectionForTest + " handle=" + sr.HandleSizeForTest);
            sr.SimulateDrag(new Point(300, 300), new Point(303, 303));
            Pump(40);
            Check(!sr.HandlesVisibleForTest,
                  "handles are hidden on a selection smaller than two handles",
                  sr.SelectionForTest + " handle=" + sr.HandleSizeForTest);
        }
        finally
        {
            try { sr.Close(); } catch { }
            sr.Dispose();
            backdrop.Dispose();
        }

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 3. the GIF result window shows where the file went --");

        string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                               "screenpen_ux_probe");
        System.IO.Directory.CreateDirectory(tmpDir);
        string fakeGif = System.IO.Path.Combine(tmpDir, "ScreenPen_20261016_120000.gif");
        System.IO.File.WriteAllBytes(fakeGif, new byte[2048]);

        GifResultForm gr = GifResultForm.CreateForTest(fakeGif, 42, 7);
        try
        {
            Check(gr.PathForTest == fakeGif,
                  "the window shows the full path of the file it wrote", gr.PathForTest);
            Check(gr.InfoForTest.Contains("42") && gr.InfoForTest.Contains("7"),
                  "the window reports the frame count and duration", gr.InfoForTest);
            Check(gr.InfoForTest.Contains("KB"),
                  "the window reports the file size", gr.InfoForTest);

            string[] btns = gr.ButtonTextsForTest;
            bool hasCopy = false;
            for (int i = 0; i < btns.Length; i++)
                if (btns[i].Contains("复制")) hasCopy = true;
            Check(hasCopy, "a 复制 button is offered", string.Join(" / ", btns));
            Check(btns.Length == 4, "the window offers four actions",
                  btns.Length.ToString());

            // The path must be inside the window at both extremes of size,
            // otherwise "save location not shown" comes straight back.
            int[] widths = new int[] { 460, 620, 1400 };
            bool pathVisible = true;
            for (int i = 0; i < widths.Length; i++)
            {
                gr.ClientSize = new Size(widths[i], 250);
                Pump(30);
                Rectangle box = gr.PathBoxBoundsForTest;
                Rectangle cl = new Rectangle(0, 0, gr.ClientSize.Width, gr.ClientSize.Height);
                if (box.Width <= 0 || !cl.Contains(box)) pathVisible = false;
            }
            Check(pathVisible, "the path box stays inside the window at every width", "");
        }
        finally
        {
            gr.Dispose();
        }

        GifResultForm empty = GifResultForm.CreateForTest("", 0, 0);
        try
        {
            Check(empty.PathForTest == "", "an empty path is tolerated", "ok");
        }
        finally { empty.Dispose(); }

        try { System.IO.Directory.Delete(tmpDir, true); }
        catch { }

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 4. the wheel reaches the document, and the overlay recovers --");

        OverlayForm ov = new OverlayForm(cfg, delegate { return new Size(32, 32); });
        try
        {
            ov.Show();
            Pump(150);

            // The desktop does not scroll. Pointing the wheel at it must not be
            // reported as a successful forward - and, crucially, must not leave
            // the overlay click-through, which would make the annotation layer
            // stop responding to the mouse for good.
            Point desktopPoint = new Point(4, 4);
            bool desktopScrolls = Native.IsScrollableWindowUnder(desktopPoint);
            Console.WriteLine("  (desktop point scrollable = " + desktopScrolls + ")");

            bool forwarded = ov.ForwardWheelForTest(desktopPoint, 120);
            Check(!ov.WheelPassThroughForTest,
                  "a refused forward does not leave the overlay click-through",
                  "passThrough=" + ov.WheelPassThroughForTest);
            Check(forwarded == desktopScrolls,
                  "the forward decision follows IsScrollableWindowUnder",
                  "forwarded=" + forwarded + " scrollable=" + desktopScrolls);
            Check(!ov.ClickThroughForTest,
                  "the overlay is not click-through when nothing was forwarded",
                  ov.ClickThroughForTest.ToString());

            // The real style change must be observable on the window itself, and
            // must be reversible - this is the regression guard for the old
            // helper that ORed WS_EX_LAYERED in and never cleared it.
            long before = Native.ExStyleOf(ov.Handle);
            ov.BeginWheelPassThroughForTest();
            long during = Native.ExStyleOf(ov.Handle);
            ov.EndWheelPassThroughForTest();
            long after = Native.ExStyleOf(ov.Handle);

            Check((during & Native.WS_EX_TRANSPARENT) != 0,
                  "forwarding really sets WS_EX_TRANSPARENT on the window",
                  "exStyle=0x" + during.ToString("X"));
            Check((after & Native.WS_EX_TRANSPARENT) == 0,
                  "the click-through is removed again",
                  "exStyle=0x" + after.ToString("X"));
            Check(after == before,
                  "the ex-style is byte-for-byte what it was before the forward",
                  "0x" + before.ToString("X") + " -> 0x" + after.ToString("X"));
            Check((after & Native.WS_EX_LAYERED) == 0,
                  "forwarding does not leave the window layered",
                  "exStyle=0x" + after.ToString("X"));

            // ---- the previously dead ScrollFollowClickThrough setting.
            Settings fcfg = new Settings();
            fcfg.ScrollFollowEnabled = true;
            fcfg.ScrollFollowClickThrough = true;
            OverlayForm ov2 = new OverlayForm(fcfg, delegate { return new Size(32, 32); });
            try
            {
                ov2.Show();
                Pump(150);
                Check(!ov2.ClickThroughForTest,
                      "the overlay is not click-through before follow mode starts", "");
                ov2.StartScrollFollow();
                Pump(60);
                Check(ov2.FollowClickThroughForTest && ov2.ClickThroughForTest,
                      "follow mode honours ScrollFollowClickThrough",
                      "follow=" + ov2.FollowClickThroughForTest +
                      " applied=" + ov2.ClickThroughForTest);
                ov2.StopScrollFollow();
                Pump(60);
                Check(!ov2.ClickThroughForTest,
                      "leaving follow mode restores normal mouse handling", "");
            }
            finally
            {
                try { ov2.Close(); } catch { }
                ov2.Dispose();
            }

            // ---- the persisted-key migration for that setting.
            //
            // The old key was written to every install (default true) while no
            // code read it, so honouring it now would make the overlay
            // un-drawable the first time someone pressed F. Reading a fresh key
            // is what keeps those installs safe, and it is invisible unless
            // asserted - so assert it.
            string cfgPath = Settings.SettingsPath();
            string cfgBackup = null;
            if (System.IO.File.Exists(cfgPath))
            {
                cfgBackup = cfgPath + ".uxprobe_backup";
                System.IO.File.Copy(cfgPath, cfgBackup, true);
            }
            try
            {
                System.IO.File.WriteAllText(cfgPath,
                    "{\r\n  \"ScrollFollowClickThrough\": true\r\n}", new System.Text.UTF8Encoding(false));
                Settings legacy = SettingsStore.Load();
                Check(legacy.ScrollFollowClickThrough == false,
                      "a legacy \"ScrollFollowClickThrough\": true is ignored",
                      legacy.ScrollFollowClickThrough.ToString());

                System.IO.File.WriteAllText(cfgPath,
                    "{\r\n  \"ScrollFollowClickThroughV2\": true\r\n}", new System.Text.UTF8Encoding(false));
                Settings current = SettingsStore.Load();
                Check(current.ScrollFollowClickThrough == true,
                      "the new key is honoured", current.ScrollFollowClickThrough.ToString());

                SettingsStore.Save(current);
                string written = System.IO.File.ReadAllText(cfgPath);
                Check(written.Contains("ScrollFollowClickThroughV2"),
                      "saving writes the new key", "ok");
                Check(written.IndexOf("\"ScrollFollowClickThrough\"") < 0,
                      "saving drops the stale key", "ok");
            }
            finally
            {
                if (cfgBackup != null)
                {
                    System.IO.File.Copy(cfgBackup, cfgPath, true);
                    System.IO.File.Delete(cfgBackup);
                }
                else if (System.IO.File.Exists(cfgPath))
                {
                    System.IO.File.Delete(cfgPath);
                }
            }
        }
        finally
        {
            try { ov.Close(); } catch { }
            ov.Dispose();
        }

        // =================================================================
        Console.WriteLine();
        Console.WriteLine("-- 5. the recording Esc shortcut really exists --");

        KeyboardHook hook = new KeyboardHook();
        int fired = 0;
        hook.EscapePressed += delegate { fired++; };
        bool installed = hook.Install();
        Check(installed, "the Esc hook installs", "installed=" + installed);
        Check(hook.IsInstalled == installed, "IsInstalled agrees with Install",
              hook.IsInstalled.ToString());

        if (installed)
        {
            // A real keystroke, not the test seam: this is the only way to prove
            // the hook actually sees a system-wide Esc, which is what the
            // documented "按 Esc 停止" needs and what never worked before.
            Native.SendEscape();
            int deadline = Environment.TickCount + 1500;
            while (fired == 0 && Environment.TickCount - deadline < 0)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
            Check(fired > 0, "a real Esc key press reaches the hook",
                  "callbacks=" + fired);

            // The seam must work regardless, so the wiring below it is covered
            // even on a machine that refuses the hook.
            int before = fired;
            hook.RaiseEscapeForTest();
            Check(fired == before + 1, "the event plumbing is intact",
                  before + " -> " + fired);
        }

        hook.Dispose();
        Check(!hook.IsInstalled, "disposing the hook uninstalls it", "ok");
        hook.Dispose();   // must be safe twice
        Check(true, "disposing twice is harmless", "ok");

        KeyboardHook hook2 = new KeyboardHook();
        hook2.Install();
        bool reinstall = hook2.Install();
        Check(reinstall && hook2.IsInstalled, "installing twice is idempotent", "ok");
        hook2.Dispose();

        Console.WriteLine();
        if (fails == 0)
        {
            Console.WriteLine("=== ALL UX FIX CHECKS PASSED ===");
            return 0;
        }
        Console.WriteLine("=== " + fails + " UX FIX CHECK(S) FAILED ===");
        return 1;
    }
}
