// Probe for the OCR + GIF integration.
//
// The wiring is the risky part, not the engines:
//   * RegionAction.Ocr/RecordGif must not disturb the existing 0/1/2/3 values
//     (saved state and probes depend on them)
//   * the region toolbar must STILL be exactly four buttons -- five would push
//     the row past the width it can shrink to
//   * the selector must map bare O and G to the new actions, without stealing
//     Ctrl+O / Ctrl+G, and without breaking Esc/Enter/P/Ctrl+C/arrows/Ctrl+A
//   * the new hotkeys must be registrable and unique by default
//   * OcrResultForm must show the text it was given
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenPen.Probes
{
    static class OcrGifWiringProbe
    {
        static int _pass, _fail;

        static void Check(bool ok, string what, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  [PASS] " + what + (detail == null ? "" : "  (" + detail + ")")); }
            else { _fail++; Console.WriteLine("  [FAIL] " + what + (detail == null ? "" : "  (" + detail + ")")); }
        }

        static void SendKey(Form f, Keys key, bool ctrl, bool alt, bool shift)
        {
            // Drive the selector's own key handler in-process.
            System.Reflection.MethodInfo mi = typeof(Form).GetMethod("OnKeyDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Keys mods = Keys.None;
            if (ctrl) mods |= Keys.Control;
            if (alt) mods |= Keys.Alt;
            if (shift) mods |= Keys.Shift;
            KeyEventArgs e = new KeyEventArgs(key | mods);
            mi.Invoke(f, new object[] { e });
        }

        /// The padding ToolbarBoundsForTest adds on each side.
        static int ToolbarPadOf(RegionSelectForm sel)
        {
            Rectangle u = sel.ToolbarBoundsForTest;
            return Math.Max(0, (u.Width - (sel.CancelButtonBounds.Right - sel.SaveButtonBounds.Left)) / 2);
        }

        [STAThread]
        static int Main()
        {
            Console.WriteLine("=== OCR + GIF WIRING PROBE ===");
            Console.WriteLine();

            // ---------------------------------------------------------------
            Console.WriteLine("-- RegionAction values are stable --");
            Check((int)RegionAction.Cancelled == 0, "Cancelled stays 0", ((int)RegionAction.Cancelled).ToString());
            Check((int)RegionAction.SaveToFile == 1, "SaveToFile stays 1", ((int)RegionAction.SaveToFile).ToString());
            Check((int)RegionAction.CopyToClipboard == 2, "CopyToClipboard stays 2", ((int)RegionAction.CopyToClipboard).ToString());
            Check((int)RegionAction.PinToDesktop == 3, "PinToDesktop stays 3", ((int)RegionAction.PinToDesktop).ToString());
            Check((int)RegionAction.Ocr == 4, "Ocr is appended as 4", ((int)RegionAction.Ocr).ToString());
            Check((int)RegionAction.RecordGif == 5, "RecordGif is appended as 5", ((int)RegionAction.RecordGif).ToString());

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- the toolbar is still exactly four buttons --");
            Settings cfg = new Settings();
            Rectangle desktop = new Rectangle(0, 0, 1280, 800);
            Bitmap backdrop = new Bitmap(desktop.Width, desktop.Height);
            using (Graphics g = Graphics.FromImage(backdrop)) g.Clear(Color.SteelBlue);

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                string[] labels = sel.ToolbarLabelsForTest;
                Check(labels.Length == 4, "the toolbar has four buttons", "count=" + labels.Length);

                // A selection has to exist before the toolbar is laid out, so
                // select everything first and only then measure it.
                SendKey(sel, Keys.A, true, false, false);
                Check(sel.HasSelectionForTest, "Ctrl+A still selects everything", "ok");

                Check(sel.SaveButtonBounds.Width > 0 && sel.CopyButtonBounds.Width > 0 &&
                      sel.PinButtonBounds.Width > 0 && sel.CancelButtonBounds.Width > 0,
                      "all four buttons have a layout",
                      "save=" + sel.SaveButtonBounds.Width + " copy=" + sel.CopyButtonBounds.Width +
                      " pin=" + sel.PinButtonBounds.Width + " cancel=" + sel.CancelButtonBounds.Width);

                // ToolbarBoundsForTest inflates the row by the shadow padding, so
                // it can legitimately sit up to that padding outside the 6px
                // margin LayoutToolbar reserves for the buttons themselves. Check
                // the buttons, which is what has to remain reachable.
                int pad = ToolbarPadOf(sel);
                Rectangle union = sel.ToolbarBoundsForTest;
                Rectangle buttons = union;
                buttons.Inflate(-pad, -pad);
                Check(buttons.Left >= 0 && buttons.Top >= 0 &&
                      buttons.Right <= desktop.Width && buttons.Bottom <= desktop.Height,
                      "the toolbar buttons stay on screen",
                      buttons.ToString() + "  shadow bounds=" + union +
                      "  desktop=" + desktop.Width + "x" + desktop.Height +
                      "  shrunk=" + sel.ToolbarShrunkForTest);

                // -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("-- bare O and G choose the new actions --");

                SendKey(sel, Keys.O, false, false, false);
                Check(sel.ActionTaken == RegionAction.Ocr,
                      "bare O selects OCR", sel.ActionTaken.ToString());
            }

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.G, false, false, false);
                Check(sel.ActionTaken == RegionAction.RecordGif,
                      "bare G selects GIF recording", sel.ActionTaken.ToString());
            }

            // Ctrl+O / Ctrl+G must NOT be swallowed by the bare-key branches.
            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.O, true, false, false);
                Check(sel.ActionTaken == RegionAction.Cancelled,
                      "Ctrl+O is left alone for future use", sel.ActionTaken.ToString());
            }

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.G, true, false, false);
                Check(sel.ActionTaken == RegionAction.Cancelled,
                      "Ctrl+G is left alone for future use", sel.ActionTaken.ToString());
            }

            // The pre-existing bindings must still work.
            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.P, false, false, false);
                Check(sel.ActionTaken == RegionAction.PinToDesktop,
                      "bare P still pins", sel.ActionTaken.ToString());
            }

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.C, true, false, false);
                Check(sel.ActionTaken == RegionAction.CopyToClipboard,
                      "Ctrl+C still copies", sel.ActionTaken.ToString());
            }

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.Enter, false, false, false);
                Check(sel.ActionTaken == RegionAction.SaveToFile,
                      "Enter still takes the default action", sel.ActionTaken.ToString());
            }

            using (RegionSelectForm sel = new RegionSelectForm(cfg, desktop, backdrop, RegionAction.SaveToFile))
            {
                SendKey(sel, Keys.A, true, false, false);
                SendKey(sel, Keys.Escape, false, false, false);
                Check(sel.ActionTaken == RegionAction.Cancelled,
                      "Esc still cancels", sel.ActionTaken.ToString());
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- new default hotkeys --");
            Check(cfg.OcrKey == "Ctrl+Alt+O", "OCR default is Ctrl+Alt+O", cfg.OcrKey);
            Check(cfg.GifKey == "Ctrl+Alt+G", "GIF default is Ctrl+Alt+G", cfg.GifKey);

            HotKey ocr = HotKey.Parse(cfg.OcrKey);
            HotKey gif = HotKey.Parse(cfg.GifKey);
            Check(!ocr.IsEmpty, "the OCR hotkey parses", ocr.ToString());
            Check(!gif.IsEmpty, "the GIF hotkey parses", gif.ToString());
            Check(ocr.ToString() != gif.ToString(), "the two new hotkeys differ", ocr + " vs " + gif);

            string[] all = new string[]
            {
                cfg.ToggleKey, cfg.LiveZoomKey, cfg.ScreenshotKey, cfg.CopyShotKey,
                cfg.WindowShotKey, cfg.ColorPickerKey, cfg.OcrKey, cfg.GifKey,
                cfg.ZoomInKey, cfg.ZoomOutKey
            };
            Dictionary<string, int> seen = new Dictionary<string, int>();
            for (int i = 0; i < all.Length; i++)
            {
                HotKey h = HotKey.Parse(all[i]);
                if (h.IsEmpty) continue;
                string k = h.ToString();
                if (!seen.ContainsKey(k)) seen[k] = 0;
                seen[k]++;
            }
            bool dup = false;
            string dupes = "";
            foreach (KeyValuePair<string, int> kv in seen)
                if (kv.Value > 1) { dup = true; dupes += kv.Key + " "; }
            Check(!dup, "every default hotkey is unique", dup ? dupes : seen.Count + " distinct");

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- OCR result window --");
            using (OcrResultForm f = OcrResultForm.CreateForTest("第一行\r\nsecond line", "zh-Hans-CN", true))
            {
                Check(f.TextForTest == "第一行\r\nsecond line",
                      "the result window shows the recognised text", f.TextForTest);
            }
            using (OcrResultForm f = OcrResultForm.CreateForTest("", "", false))
            {
                Check(f.TextForTest == "", "an empty result is shown without crashing", "ok");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- settings round-trip for the new keys --");
            {
                string path = Settings.SettingsPath();
                string backup = null;
                if (System.IO.File.Exists(path))
                {
                    backup = path + ".probe_backup";
                    System.IO.File.Copy(path, backup, true);
                }
                try
                {
                    Settings s = new Settings();
                    s.OcrKey = "Ctrl+Shift+9";
                    s.GifKey = "Ctrl+Shift+8";
                    s.OcrCopyToClipboard = false;
                    s.OcrKeepLineBreaks = false;
                    s.OcrUpscaleSmall = 5;
                    s.OcrLanguage = "en-US";
                    s.GifFps = 12;
                    s.GifMaxSeconds = 45;
                    s.GifLoop = false;
                    s.GifSkipIdenticalFrames = false;
                    s.GifShowBorder = false;
                    s.GifCountdownSeconds = 7;
                    SettingsStore.Save(s);

                    Settings r = SettingsStore.Load();
                    Check(r.OcrKey == "Ctrl+Shift+9", "OcrKey persists", r.OcrKey);
                    Check(r.GifKey == "Ctrl+Shift+8", "GifKey persists", r.GifKey);
                    Check(r.OcrCopyToClipboard == false, "OcrCopyToClipboard persists", r.OcrCopyToClipboard.ToString());
                    Check(r.OcrKeepLineBreaks == false, "OcrKeepLineBreaks persists", r.OcrKeepLineBreaks.ToString());
                    Check(r.OcrUpscaleSmall == 5, "OcrUpscaleSmall persists", r.OcrUpscaleSmall.ToString());
                    Check(r.OcrLanguage == "en-US", "OcrLanguage persists", r.OcrLanguage);
                    Check(r.GifFps == 12, "GifFps persists", r.GifFps.ToString());
                    Check(r.GifMaxSeconds == 45, "GifMaxSeconds persists", r.GifMaxSeconds.ToString());
                    Check(r.GifLoop == false, "GifLoop persists", r.GifLoop.ToString());
                    Check(r.GifSkipIdenticalFrames == false, "GifSkipIdenticalFrames persists", r.GifSkipIdenticalFrames.ToString());
                    Check(r.GifShowBorder == false, "GifShowBorder persists", r.GifShowBorder.ToString());
                    Check(r.GifCountdownSeconds == 7, "GifCountdownSeconds persists", r.GifCountdownSeconds.ToString());
                }
                finally
                {
                    if (backup != null)
                    {
                        System.IO.File.Copy(backup, path, true);
                        System.IO.File.Delete(backup);
                    }
                    else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- clamping of out-of-range values --");
            {
                Settings s = new Settings();
                s.OcrUpscaleSmall = 99;
                s.GifFps = 999;
                s.GifMaxSeconds = 0;
                s.GifCountdownSeconds = -5;
                s.OcrLanguage = null;
                s.Clamp();
                Check(s.OcrUpscaleSmall >= 0 && s.OcrUpscaleSmall <= 8,
                      "OcrUpscaleSmall is clamped", s.OcrUpscaleSmall.ToString());
                Check(s.GifFps >= 2 && s.GifFps <= 25, "GifFps is clamped", s.GifFps.ToString());
                Check(s.GifMaxSeconds >= 1 && s.GifMaxSeconds <= 300,
                      "GifMaxSeconds is clamped", s.GifMaxSeconds.ToString());
                Check(s.GifCountdownSeconds >= 0 && s.GifCountdownSeconds <= 10,
                      "GifCountdownSeconds is clamped", s.GifCountdownSeconds.ToString());
                Check(s.OcrLanguage != null, "OcrLanguage never stays null", "\"" + s.OcrLanguage + "\"");
            }

            // ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("-- the settings dialog exposes the new options --");
            {
                Settings d = new Settings();
                d.OcrKey = "Ctrl+Alt+O";
                d.GifKey = "Ctrl+Alt+G";
                d.OcrCopyToClipboard = true;
                d.OcrKeepLineBreaks = true;
                d.OcrUpscaleSmall = 4;
                d.OcrLanguage = "";
                d.GifFps = 10;
                d.GifMaxSeconds = 30;
                d.GifCountdownSeconds = 3;
                d.GifShowBorder = true;
                d.GifLoop = true;
                d.GifSkipIdenticalFrames = true;

                using (SettingsForm f = SettingsForm.CreateForTest(d))
                {
                    f.PromptOverride = delegate { return DialogResult.Yes; };

                    Check(f.OcrHotkeyForTest == "Ctrl+Alt+O",
                          "the OCR hotkey row shows the value", f.OcrHotkeyForTest);
                    Check(f.GifHotkeyForTest == "Ctrl+Alt+G",
                          "the GIF hotkey row shows the value", f.GifHotkeyForTest);
                    Check(f.GifFpsForTest == 10, "the GIF frame rate shows the value",
                          f.GifFpsForTest.ToString());
                    Check(f.OcrUpscaleForTest == 4, "the OCR upscale shows the value",
                          f.OcrUpscaleForTest.ToString());
                    Check(f.OcrLanguageCountForTest > 0,
                          "the language list includes 跟随系统",
                          "items=" + f.OcrLanguageCountForTest);
                    Check(f.OcrLanguageForTest == "",
                          "跟随系统 is selected for an empty language", "\"" + f.OcrLanguageForTest + "\"");

                    // Round-trip: change the controls, run the real save path,
                    // then restore whatever the user had.
                    string cfgPath = Settings.SettingsPath();
                    string cfgBackup = null;
                    if (System.IO.File.Exists(cfgPath))
                    {
                        cfgBackup = cfgPath + ".formtest_backup";
                        System.IO.File.Copy(cfgPath, cfgBackup, true);
                    }
                    try
                    {
                        f.SetGifFpsForTest(18);
                        f.SetOcrUpscaleForTest(2);
                        f.SetGifCountdownForTest(9);
                        f.SetOcrCopyForTest(false);
                        Settings outDraft = f.RunSaveForTest();

                        Check(outDraft.GifFps == 18, "a changed frame rate is saved",
                              outDraft.GifFps.ToString());
                        Check(outDraft.OcrUpscaleSmall == 2, "a changed upscale is saved",
                              outDraft.OcrUpscaleSmall.ToString());
                        Check(outDraft.GifCountdownSeconds == 9, "a changed countdown is saved",
                              outDraft.GifCountdownSeconds.ToString());
                        Check(outDraft.OcrCopyToClipboard == false,
                              "a changed OCR checkbox is saved", outDraft.OcrCopyToClipboard.ToString());
                        Check(outDraft.OcrKey == "Ctrl+Alt+O" && outDraft.GifKey == "Ctrl+Alt+G",
                              "the new hotkeys survive a form round-trip",
                              outDraft.OcrKey + " / " + outDraft.GifKey);

                        // OnSave deliberately does not touch the disk: it hands
                        // the draft back and the app persists it. Do what the
                        // app does, then check the file the next launch reads.
                        SettingsStore.Save(f.Result);
                        Settings reloaded = SettingsStore.Load();
                        Check(reloaded.GifFps == 18, "the saved frame rate reaches the settings file",
                              reloaded.GifFps.ToString());
                        Check(reloaded.OcrKey == "Ctrl+Alt+O" && reloaded.GifKey == "Ctrl+Alt+G",
                              "the saved hotkeys reach the settings file",
                              reloaded.OcrKey + " / " + reloaded.GifKey);
                    }
                    finally
                    {
                        if (cfgBackup != null)
                        {
                            System.IO.File.Copy(cfgBackup, cfgPath, true);
                            System.IO.File.Delete(cfgBackup);
                        }
                        else if (System.IO.File.Exists(cfgPath)) System.IO.File.Delete(cfgPath);
                    }
                }
            }

            backdrop.Dispose();

            Console.WriteLine();
            Console.WriteLine("=== RESULT: " + _pass + " passed, " + _fail + " failed ===");
            return _fail == 0 ? 0 : 1;
        }
    }
}
