using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>
    /// Application entry point and controller.
    ///
    /// Responsibilities: own the tray icon, register global hotkeys, drive the
    /// overlay, and route save/copy/screenshot requests.
    /// </summary>
    public sealed class AppContext2 : ApplicationContext
    {
        // Hotkey IDs
        const int HK_TOGGLE = 1;
        const int HK_LIVE = 2;
        const int HK_SHOT = 3;
        const int HK_ZOOM_IN = 4;
        const int HK_ZOOM_OUT = 5;
        const int HK_COPYSHOT = 6;
        const int HK_WINDOWSHOT = 7;
        const int HK_PICKER = 8;
        const int HK_OCR = 9;
        const int HK_GIF = 10;
        const int HK_SPOT = 11;

        Settings _cfg;
        OverlayForm _overlay;
        NotifyIcon _tray;
        HotkeyWindow _msgWindow;
        SettingsForm _settingsForm;
        GifRecorder _gifRecorder;

        /// <summary>
        /// The 浮标 overlay: a semi-transparent circle on the pointer, plus
        /// optional click ripples. Lives for the whole process so the circle can
        /// be toggled without paying for window creation each time.
        /// </summary>
        CursorSpotOverlay _spot;

        /// <summary>Tray item whose tick mirrors <c>CursorSpotEnabled</c>.</summary>
        ToolStripMenuItem _spotItem;

        /// <summary>
        /// Set once <see cref="Quit"/> starts. A finished recording would
        /// otherwise open a modal result window from inside the shutdown path
        /// and the process would never exit.
        /// </summary>
        bool _quitting;

        /// <summary>
        /// Watches for Esc while a recording is running.
        ///
        /// The recorder's own chrome window is WS_EX_NOACTIVATE and never takes
        /// focus, so it can never receive a key press - which is why the README
        /// promised "按 Esc 停止" and nothing happened. The only way to see the
        /// key is a low-level keyboard hook, installed for the duration of the
        /// recording only.
        /// </summary>
        KeyboardHook _recordingKeys;

        /// <summary>Tray item that ends a recording early; enabled only while one runs.</summary>
        ToolStripMenuItem _stopGifItem;

        readonly List<int> _registered = new List<int>();

        /// <summary>Hotkeys that failed to register, as "名称 = 组合键" strings.</summary>
        readonly List<string> _failedHotkeys = new List<string>();

        /// <summary>
        /// Every 贴图 (pinned screenshot) window currently open. They are
        /// independent top-level windows with no owner, so nothing else keeps
        /// them alive: if the context did not track them, quitting the app would
        /// leave orphaned windows floating over the desktop forever.
        /// </summary>
        readonly List<PinForm> _pins = new List<PinForm>();

        public AppContext2(bool startMinimized)
        {
            _cfg = SettingsStore.Load();

            _msgWindow = new HotkeyWindow();
            _msgWindow.HotkeyPressed += OnHotkey;
            _msgWindow.ActivateRequested += (s, e) => OpenSettings();

            _overlay = new OverlayForm(_cfg, () => Size.Empty);
            _overlay.KeyDown += OverlayKeyDown;
            _overlay.KeyUp += OverlayKeyUp;
            _overlay.FormClosing += (s, e) =>
            {
                // Closing the overlay should only hide it, never exit the app.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideOverlay();
                }
            };

            // Created unconditionally: construction is cheap (two hidden
            // windows, no timer) and it installs the capture guard, which is what
            // keeps the circle out of our own screenshots even before the user
            // has ever switched the feature on.
            _spot = new CursorSpotOverlay();
            _spot.ApplySettings(_cfg);

            BuildTray();
            RegisterAllHotkeys();

            if (!startMinimized)
                BeginInvokeSettings();
        }

        /// <summary>Explains, once, if some hotkeys could not be claimed.</summary>
        void ReportHotkeyProblems()
        {
            if (_failedHotkeys.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("以下快捷键无法注册，可能已被系统或其它软件占用：");
            sb.AppendLine();
            foreach (string f in _failedHotkeys) sb.AppendLine("  · " + f);
            sb.AppendLine();
            sb.Append("请在「设置 → 快捷键」里改成其它组合。");

            MessageBox.Show(sb.ToString(), "ScreenPen 快捷键冲突",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void BeginInvokeSettings()
        {
            Timer t = new Timer { Interval = 120 };
            t.Tick += (s, e) =>
            {
                t.Stop(); t.Dispose();
                OpenSettings();
            };
            t.Start();
        }

        // =====================================================================
        //  Tray
        // =====================================================================

        void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Icon = IconLibrary.Tray,
                Text = "ScreenPen 屏幕画笔",
                Visible = _cfg.ShowTrayIcon
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(MenuItem("开始标注 / 画笔模式", (s, e) => ToggleOverlay(false)));
            menu.Items.Add(MenuItem("实时缩放", (s, e) => ToggleOverlay(true)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuItem("区域截图并保存…", (s, e) => RegionScreenshot(RegionAction.SaveToFile)));
            menu.Items.Add(MenuItem("区域截图并复制到剪贴板", (s, e) => RegionScreenshot(RegionAction.CopyToClipboard)));
            menu.Items.Add(MenuItem("区域截图并钉在桌面", (s, e) => RegionScreenshot(RegionAction.PinToDesktop)));
            menu.Items.Add(MenuItem("识别选区文字…", (s, e) => RegionScreenshot(RegionAction.Ocr)));
            menu.Items.Add(MenuItem("录制选区为 GIF…", (s, e) => RegionScreenshot(RegionAction.RecordGif)));
            // Enabled only while a recording is actually running, so the tray
            // itself tells the user a recording is in progress and gives them a
            // way out that does not depend on remembering a key.
            _stopGifItem = MenuItem("停止 GIF 录制", (s, e) => StopGifRecording());
            _stopGifItem.Enabled = false;
            menu.Items.Add(_stopGifItem);
            // Checkable rather than a pair of 开/关 items: the tick is the only
            // place the current state is visible, and it doubles as the answer to
            // "is the circle on or did it just not draw?".
            _spotItem = new ToolStripMenuItem("鼠标浮标（指针旁的半透明圆圈）");
            _spotItem.CheckOnClick = false;
            _spotItem.Click += (s, e) => ToggleCursorSpot(true);
            _spotItem.Checked = _cfg.CursorSpotEnabled;
            menu.Items.Add(_spotItem);
            menu.Items.Add(MenuItem("整屏截图并保存…", (s, e) => ScreenshotToFile()));
            menu.Items.Add(MenuItem("复制整屏到剪贴板", (s, e) => CopyToClipboard()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuItem("设置…", (s, e) => OpenSettings()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuItem("退出", (s, e) => Quit()));

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => OpenSettings();
        }

        static ToolStripMenuItem MenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += handler;
            return it;
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            if (_tray == null || !_tray.Visible) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(2200);
            }
            catch { }
        }

        // =====================================================================
        //  Hotkeys
        // =====================================================================

        void RegisterAllHotkeys()
        {
            UnregisterAll();
            _failedHotkeys.Clear();

            TryRegister(HK_TOGGLE, _cfg.ToggleKey, "开关画笔");
            TryRegister(HK_LIVE, _cfg.LiveZoomKey, "实时缩放");
            TryRegister(HK_SHOT, _cfg.ScreenshotKey, "区域截图");
            TryRegister(HK_COPYSHOT, _cfg.CopyShotKey, "截图并复制");
            TryRegister(HK_WINDOWSHOT, _cfg.WindowShotKey, "窗口截图");
            TryRegister(HK_PICKER, _cfg.ColorPickerKey, "取色器");
            TryRegister(HK_OCR, _cfg.OcrKey, "文字识别");
            TryRegister(HK_GIF, _cfg.GifKey, "录制 GIF");
            TryRegister(HK_SPOT, _cfg.CursorSpotKey, "浮标");
            TryRegister(HK_ZOOM_IN, _cfg.ZoomInKey, "放大");
            TryRegister(HK_ZOOM_OUT, _cfg.ZoomOutKey, "缩小");
        }

        void TryRegister(int id, string text, string displayName)
        {
            HotKey hk = HotKey.Parse(text);
            if (hk.IsEmpty) return;
            uint mods = hk.Modifiers() | Native.MOD_NOREPEAT;
            if (Native.RegisterHotKey(_msgWindow.Handle, id, mods, (uint)hk.Key))
            {
                _registered.Add(id);
            }
            else
            {
                // Registration failures used to be silent, which made "my new
                // hotkey does nothing" impossible to diagnose. Record them so
                // the user gets told exactly which combination is unavailable.
                _failedHotkeys.Add(displayName + " = " + hk.ToString());
            }
        }

        void UnregisterAll()
        {
            foreach (int id in _registered)
            {
                try { Native.UnregisterHotKey(_msgWindow.Handle, id); }
                catch { }
            }
            _registered.Clear();
        }

        /// <summary>
        /// True when the given combination could be registered globally.
        ///
        /// The probe deliberately uses the *same* modifiers the user would get,
        /// because availability depends on them: Ctrl+Up can be free while
        /// Alt+Up is taken. It also first checks whether ScreenPen itself
        /// already holds the combination, which is the common case when the user
        /// is just re-recording an existing hotkey.
        /// </summary>
        public bool IsHotkeyAvailable(Keys key)
        {
            return IsHotkeyAvailable(new HotKey { Key = key });
        }

        public bool IsHotkeyAvailable(HotKey hk)
        {
            if (hk == null || hk.Key == Keys.None) return false;

            // Already ours? Then obviously it is usable.
            if (OwnsHotkey(hk)) return true;

            using (HotkeyWindow probe = new HotkeyWindow())
            {
                uint mods = hk.Modifiers() | Native.MOD_NOREPEAT;
                bool ok = Native.RegisterHotKey(probe.Handle, 9001, mods, (uint)hk.Key);
                if (ok) Native.UnregisterHotKey(probe.Handle, 9001);
                return ok;
            }
        }

        /// <summary>True when one of ScreenPen's own hotkeys equals this combo.</summary>
        bool OwnsHotkey(HotKey hk)
        {
            string norm = hk.ToString();
            string[] mine = new string[]
            {
                _cfg.ToggleKey, _cfg.LiveZoomKey, _cfg.ScreenshotKey, _cfg.CopyShotKey,
                _cfg.WindowShotKey, _cfg.ColorPickerKey,
                _cfg.OcrKey, _cfg.GifKey,
                _cfg.ZoomInKey, _cfg.ZoomOutKey
            };
            foreach (string s in mine)
            {
                HotKey m = HotKey.Parse(s);
                if (!m.IsEmpty && m.ToString() == norm) return true;
            }
            return false;
        }

        void OnHotkey(object sender, int id)
        {
            switch (id)
            {
                case HK_TOGGLE: ToggleOverlay(false); break;
                case HK_LIVE: ToggleOverlay(true); break;
                case HK_SHOT: RegionScreenshot(RegionAction.SaveToFile); break;
                case HK_COPYSHOT: RegionScreenshot(RegionAction.CopyToClipboard); break;
                case HK_WINDOWSHOT: WindowScreenshot(); break;
                case HK_PICKER: StartColorPicker(); break;
                case HK_OCR: RegionScreenshot(RegionAction.Ocr); break;
                case HK_GIF: RegionScreenshot(RegionAction.RecordGif); break;
                case HK_SPOT: ToggleCursorSpot(false); break;
                case HK_ZOOM_IN:
                    if (_overlay.Visible) _overlay.ZoomBy(120, CursorAtClient());
                    break;
                case HK_ZOOM_OUT:
                    if (_overlay.Visible) _overlay.ZoomBy(-120, CursorAtClient());
                    break;
            }
        }

        Point CursorAtClient()
        {
            if (!_overlay.Visible) return new Point(0, 0);
            return _overlay.PointToClient(Cursor.Position);
        }

        // =====================================================================
        //  Overlay control
        // =====================================================================

        void ToggleOverlay(bool liveZoom)
        {
            if (_overlay.Visible)
            {
                // Pressing the other hotkey while active switches mode.
                if (_overlay.LiveMode != liveZoom)
                {
                    HideOverlay();
                    _overlay.ActivateOverlay(liveZoom);
                }
                else HideOverlay();
                return;
            }
            _overlay.ActivateOverlay(liveZoom);
        }

        void HideOverlay()
        {
            try { _overlay.DeactivateOverlay(); }
            catch { }
        }

        void OverlayKeyUp(object sender, KeyEventArgs e)
        {
            // Tab is used as a held modifier for the ellipse override, so its
            // release must clear the latch or every later drag would be an ellipse.
            if (e.KeyCode == Keys.Tab) HoldModifier(false, e.KeyCode);
            if (e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey ||
                e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey)
                HoldModifier(false, e.KeyCode);
        }

        void HoldModifier(bool down, Keys code)
        {
            if (code == Keys.Tab)
            {
                if (down) _overlay.SetTabHeld(true);
                else _overlay.SetTabHeld(false);
            }
        }

        void OverlayKeyDown(object sender, KeyEventArgs e)
        {
            Keys code = e.KeyCode;

            // While the inline text editor is open, every key belongs to it.
            //
            // The overlay sets KeyPreview = true, so this handler runs *before*
            // the focused TextBox sees the keystroke. Without this guard the
            // single-key commands below stole the user's typing: 'L' switched to
            // the line tool, 'P' to pen, 'O' to the ellipse, 'A' to the arrow,
            // 'E' cleared the canvas, 'B' cycled the colour and '1'-'8' picked a
            // palette entry - and because each of those branches sets
            // e.Handled, the character never reached the box at all. The user's
            // report was exactly "按到其他的有特殊意义的按键会触发 ... 然后打字就无法输入了".
            //
            // Returning without touching e.Handled hands the key straight back to
            // the editor. Esc and Enter still work: TextEditorKeyDown handles
            // both (Esc discards this label, Enter commits it).
            if (_overlay.IsEditingText) return;

            // Esc always exits.
            if (code == Keys.Escape)
            {
                HideOverlay();
                e.Handled = true;
                return;
            }

            // Tab is a held modifier (ellipse), never a command: swallow it so
            // it cannot move focus away from the overlay.
            if (code == Keys.Tab)
            {
                _overlay.SetTabHeld(true);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Ctrl-modified actions first.
            if (e.Control)
            {
                HotKey undo = HotKey.Parse(_cfg.UndoKey);
                HotKey redo = HotKey.Parse(_cfg.RedoKey);
                HotKey save = HotKey.Parse(_cfg.SaveKey);
                HotKey copy = HotKey.Parse(_cfg.CopyKey);

                // Redo is tested BEFORE undo. Ctrl+Shift+Z is the other common
                // redo binding, and the undo test below matches a bare
                // `code == Keys.Z` regardless of Shift - so if undo ran first it
                // would swallow Ctrl+Shift+Z and redo would be unreachable.
                if (Matches(redo, code) || (code == Keys.Z && e.Shift))
                {
                    _overlay.Redo();
                    e.Handled = true;
                    return;
                }
                if (Matches(undo, code) || code == Keys.Z)
                {
                    _overlay.Undo();
                    e.Handled = true;
                    return;
                }
                if (Matches(save, code) || code == Keys.S)
                {
                    SaveAnnotated();
                    e.Handled = true;
                    return;
                }
                if (Matches(copy, code) || code == Keys.C)
                {
                    CopyToClipboard();
                    e.Handled = true;
                    return;
                }
                return;
            }

            // Configurable tool keys (a single plain key each).
            Tool? tool = _cfg.ToolForKey(code);
            if (tool.HasValue)
            {
                _overlay.SetTool(tool.Value);
                e.Handled = true;
                return;
            }

            if (IsKey(code, _cfg.FollowKey))
            {
                // ToggleScrollFollow returns the resulting state, which is false
                // both when the user just switched it OFF and when the request
                // was refused. Reporting "已关闭跟随滚动" for a refusal was
                // actively misleading - it claimed to have turned something off
                // that was never on, which is exactly how the F key looked
                // broken. Distinguish the two by asking why it is unavailable.
                string why = _overlay.FollowUnavailableReason();
                bool on = _overlay.ToggleScrollFollow();

                if (!on && why.Length > 0)
                    Balloon("无法开启跟随滚动", why + "。", ToolTipIcon.Warning);
                else if (on)
                    Balloon("已开启跟随滚动",
                            "滚轮将滚动下方网页，标注会随内容移动。",
                            ToolTipIcon.Info);
                else
                    Balloon("已关闭跟随滚动",
                            "滚轮恢复为调整笔宽。",
                            ToolTipIcon.Info);

                e.Handled = true;
                return;
            }

            switch (code)
            {
                case Keys.B: _overlay.CycleColor(1); e.Handled = true; break;
                case Keys.D1: _overlay.SelectColor(0); e.Handled = true; break;
                case Keys.D2: _overlay.SelectColor(1); e.Handled = true; break;
                case Keys.D3: _overlay.SelectColor(2); e.Handled = true; break;
                case Keys.D4: _overlay.SelectColor(3); e.Handled = true; break;
                case Keys.D5: _overlay.SelectColor(4); e.Handled = true; break;
                case Keys.D6: _overlay.SelectColor(5); e.Handled = true; break;
                case Keys.D7: _overlay.SelectColor(6); e.Handled = true; break;
                case Keys.D8: _overlay.SelectColor(7); e.Handled = true; break;

                case Keys.OemOpenBrackets: _overlay.AdjustPenWidth(-1); e.Handled = true; break;
                case Keys.OemCloseBrackets: _overlay.AdjustPenWidth(1); e.Handled = true; break;

                case Keys.Add: _overlay.AdjustPenWidth(1); e.Handled = true; break;
                case Keys.Subtract: _overlay.AdjustPenWidth(-1); e.Handled = true; break;

                case Keys.D0: _overlay.ResetView(); e.Handled = true; break;

                default:
                    // Configurable single-key clear (default 'E'). Checked last
                    // so it can never shadow a tool key.
                    if (IsKey(code, _cfg.ClearKey))
                    {
                        _overlay.Clear();
                        e.Handled = true;
                    }
                    break;
            }
        }

        /// <summary>True when a configured single-key string matches a key code.</summary>
        static bool IsKey(Keys code, string configured)
        {
            HotKey hk = HotKey.Parse(configured);
            return hk.Key != Keys.None && hk.Key == code &&
                   !hk.Ctrl && !hk.Alt && !hk.Shift;
        }

        static bool Matches(HotKey hk, Keys code)
        {
            return hk != null && hk.Key != Keys.None && hk.Key == code;
        }

        // =====================================================================
        //  Save / copy
        // =====================================================================

        /// <summary>Ctrl+S inside the overlay: save the annotated view.</summary>
        void SaveAnnotated()
        {
            Bitmap bmp = null;
            try
            {
                bmp = _overlay.RenderComposite(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("生成图像失败：" + ex.Message, "ScreenPen");
                return;
            }
            if (bmp == null) return;
            try
            {
                string path = ChooseSavePath();
                if (path == null) return;
                SaveBitmap(bmp, path);
                Balloon("已保存", path, ToolTipIcon.Info);
            }
            finally { bmp.Dispose(); }
        }

        /// <summary>Screenshot hotkey / tray item: save the current view.</summary>
        void ScreenshotToFile()
        {
            bool wasVisible = _overlay.Visible;
            string status = null;

            if (wasVisible)
            {
                try
                {
                    Bitmap bmp = _overlay.RenderComposite(false);
                    string path = ChooseSavePath();
                    if (path != null)
                    {
                        SaveBitmap(bmp, path);
                        status = path;
                    }
                    bmp.Dispose();
                    if (status != null)
                    {
                        Balloon("截图已保存", status, ToolTipIcon.Info);
                        PlayShotSound();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("截图失败：" + ex.Message, "ScreenPen");
                    return;
                }
            }

            // Overlay not active: grab the raw desktop (no annotations).
            Rectangle screen = Native.VirtualScreen();
            Bitmap shot = null;
            try
            {
                shot = Native.CaptureRegion(screen);
                if (shot == null) { Balloon("截图失败", "无法抓取屏幕内容。", ToolTipIcon.Warning); return; }

                string path = ChooseSavePath();
                if (path == null) return;
                SaveBitmap(shot, path);
                Balloon("截图已保存", path, ToolTipIcon.Info);
                PlayShotSound();
            }
            catch (Exception ex)
            {
                MessageBox.Show("截图失败：" + ex.Message, "ScreenPen");
            }
            finally { if (shot != null) shot.Dispose(); }
        }

        /// <summary>
        /// Region screenshot: freezes the screen, lets the user drag a rectangle,
        /// then saves, copies or pins just that area.
        ///
        /// <paramref name="defaultAction"/> is what Enter and double-click commit;
        /// the toolbar's explicit buttons always override it.
        ///
        /// When the overlay is already active the selection is cropped from the
        /// *composited* view so the user's annotations are included; otherwise it
        /// captures the raw desktop.
        /// </summary>
        void RegionScreenshot(RegionAction defaultAction)
        {
            // Pressing the GIF key (or picking the tray item) again while a
            // recording is running means "stop and save", not "start another
            // one". Before this, the only way out of a recording was to wait for
            // the 30 s cap - the README's "再次触发录制快捷键即可结束" simply did
            // not exist.
            if (defaultAction == RegionAction.RecordGif && StopGifRecording()) return;

            bool overlayWasVisible = _overlay.Visible;
            Bitmap frozen = null;
            // The circle must not sit over the frozen backdrop the user is
            // selecting from. Suspended for the whole interaction rather than
            // left to the per-capture guard, which would blink it on and off.
            SuspendSpot();
            try
            {
                frozen = overlayWasVisible
                    ? _overlay.RenderComposite(false)
                    : Native.CaptureRegion(Native.VirtualScreen());

                if (frozen == null)
                {
                    Balloon("截图失败", "无法抓取屏幕内容。", ToolTipIcon.Warning);
                    return;
                }

                // The selector must never appear in its own capture, so hide the
                // overlay (if any) before showing it. The frozen bitmap is what
                // the user sees, so nothing visually changes for them.
                Rectangle desktop = Native.VirtualScreen();
                if (overlayWasVisible) _overlay.Hide();

                RegionAction action;
                Rectangle region;
                Bitmap cropped = null;

                RegionSelectForm sel = new RegionSelectForm(_cfg, desktop, frozen, defaultAction);
                try
                {
                    sel.ShowDialog();

                    // Read everything the caller needs *before* the form is torn
                    // down: Close() disposes the window, and querying a disposed
                    // form is not guaranteed to keep working.
                    action = sel.ActionTaken;
                    region = sel.ChosenScreenRect;
                    if (action != RegionAction.Cancelled)
                    {
                        // Crop from the frozen backdrop rather than the live
                        // screen, so the selector's own dimming and toolbar can
                        // never end up inside the screenshot.
                        cropped = sel.CropSelection();
                    }
                }
                finally
                {
                    sel.Dispose();
                }

                if (action == RegionAction.Cancelled || cropped == null)
                {
                    if (cropped != null) cropped.Dispose();
                    return;
                }

                if (action == RegionAction.PinToDesktop)
                {
                    // The pin window owns the pixels from here on, so the local is
                    // cleared *before* the hand-off: the try/finally below would
                    // otherwise dispose the very bitmap the pin is displaying.
                    // PinScreenshot disposes it itself if the pin cannot be built,
                    // so exactly one of the two always releases it. The pin lands
                    // on the region the user just selected, which is where their
                    // attention already is.
                    Bitmap toPin = cropped;
                    cropped = null;
                    PinScreenshot(toPin, region);
                    // Returning here still runs the outer finally, which restores
                    // the annotation overlay and frees the frozen backdrop.
                    return;
                }

                if (action == RegionAction.Ocr)
                {
                    // Recognition reads the frozen crop, so the text the user
                    // selected is exactly what gets recognised.
                    OcrText(cropped);
                    return;
                }

                if (action == RegionAction.RecordGif)
                {
                    // Recording needs the LIVE screen, not the frozen crop, so
                    // the frozen backdrop is released first and the region is
                    // re-captured on every frame.
                    cropped.Dispose();
                    cropped = null;
                    if (frozen != null) { frozen.Dispose(); frozen = null; }
                    RecordGif(region);
                    return;
                }

                try
                {
                    if (action == RegionAction.CopyToClipboard)
                    {
                        bool copied;
                        using (Bitmap copy = new Bitmap(cropped.Width, cropped.Height,
                                                        PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(copy))
                                g.DrawImageUnscaled(cropped, 0, 0);
                            copied = SafeSetClipboardImage(copy);
                        }

                        if (copied)
                        {
                            Balloon("已复制", string.Format("已复制 {0} × {1} 的区域到剪贴板。",
                                    cropped.Width, cropped.Height), ToolTipIcon.Info);
                            PlayShotSound();
                        }
                        else
                            Balloon("复制失败", "剪贴板被其它程序占用，请稍后重试。",
                                    ToolTipIcon.Warning);
                    }
                    else
                    {
                        string path = ChooseSavePath();
                        if (path != null)
                        {
                            SaveBitmap(cropped, path);
                            Balloon("截图已保存", path, ToolTipIcon.Info);
                            PlayShotSound();
                        }
                    }
                }
                finally { cropped.Dispose(); }
            }
            catch (Exception ex)
            {
                MessageBox.Show("截图失败：" + ex.Message, "ScreenPen");
            }
            finally
            {
                if (frozen != null) frozen.Dispose();
                // Restore the annotation overlay the user was working in.
                // This runs on every path, including the 贴图 hand-off above.
                if (overlayWasVisible && !_overlay.Visible)
                {
                    try { _overlay.ResumeAfterCapture(); }
                    catch { }
                }
                ResumeSpot();
            }
        }

        // =====================================================================
        //  OCR
        // =====================================================================

        /// <summary>
        /// Recognises the text in <paramref name="shot"/> and shows it, copying it
        /// to the clipboard when the user asked for that.
        ///
        /// Recognition runs on a worker so the UI stays responsive, but the result
        /// dialog is shown back on the UI thread.
        /// </summary>
        void OcrText(Bitmap shot)
        {
            if (shot == null) return;

            string detail;
            if (!Ocr.IsAvailable(out detail))
            {
                Balloon("文字识别不可用", detail, ToolTipIcon.Warning);
                return;
            }

            OcrResult result = null;
            try
            {
                result = Ocr.RecognizeOnWorker(shot, _cfg, 20000);
            }
            catch (Exception ex)
            {
                Balloon("文字识别失败", ex.Message, ToolTipIcon.Warning);
                return;
            }

            if (result == null || !result.Ok)
            {
                Balloon("文字识别失败",
                        result == null ? "识别没有返回结果。" : result.Error,
                        ToolTipIcon.Warning);
                return;
            }

            string text = result.Text == null ? "" : result.Text.Trim();
            if (text.Length == 0)
            {
                Balloon("没有识别到文字",
                        "选区内没有识别到文字，可以试着选大一点或放大后再试。",
                        ToolTipIcon.Info);
                return;
            }

            bool copied = false;
            if (_cfg.OcrCopyToClipboard)
            {
                copied = SafeSetClipboardText(text);
                if (copied) PlayShotSound();
            }

            OcrResultForm.ShowResult(text, result.LanguageTag, copied);
        }

        // =====================================================================
        //  GIF recording
        // =====================================================================

        /// <summary>Records <paramref name="region"/> to a GIF, reporting progress.</summary>
        void RecordGif(Rectangle region)
        {
            if (_gifRecorder != null && (_gifRecorder.IsRecording || _gifRecorder.IsCountingDown))
            {
                Balloon("正在录制", "已经有一个 GIF 正在录制中。", ToolTipIcon.Info);
                return;
            }

            GifRecorder rec = new GifRecorder(_cfg);
            _gifRecorder = rec;

            rec.Finished += delegate
            {
                _gifRecorder = null;
                StopRecordingKeyWatch();
                ResumeSpot();

                string file = rec.LastFile;
                int frames = rec.FrameCount;
                int seconds = rec.ElapsedSeconds;
                if (file != null)
                {
                    PlayShotSound();

                    // The path used to be reported through a balloon only. A
                    // balloon vanishes after a couple of seconds and cannot be
                    // selected or acted on, so the recording looked like it had
                    // produced nothing at all - the reported complaint was
                    // literally "不知道生成的动图在哪里". GifResultForm shows the
                    // path in a selectable box next to 复制路径 / 在文件夹中显示 /
                    // 打开动图.
                    //
                    // Skipped while quitting: a modal dialog inside Quit() would
                    // block shutdown forever.
                    if (!_quitting)
                        GifResultForm.ShowResult(file, frames, seconds);
                }
                else
                {
                    string why = rec.StopReason;
                    if (why != null && why != "已取消")
                        Balloon("GIF 录制结束", why, ToolTipIcon.Warning);
                }
                rec.Dispose();
            };

            try
            {
                // Held down for the whole recording, not just per frame. Every
                // frame goes through Native.CaptureRegion, whose guard would
                // otherwise hide and re-show the circle ten times a second -
                // a very visible blink, and pointless work.
                SuspendSpot();
                rec.Start(region, null);
                // Start() can fail synchronously (region too small) and raise
                // Finished on the spot, so only arm the Esc watch once a
                // recording actually exists.
                if (IsRecordingGif) StartRecordingKeyWatch();
            }
            catch (Exception ex)
            {
                _gifRecorder = null;
                StopRecordingKeyWatch();
                ResumeSpot();
                rec.Dispose();
                Balloon("无法开始录制", ex.Message, ToolTipIcon.Warning);
            }
        }

        /// <summary>Stops any recording in progress, e.g. when the app is quitting.</summary>
        internal void StopRecordingIfAny()
        {
            GifRecorder rec = _gifRecorder;
            if (rec == null) return;
            _gifRecorder = null;
            StopRecordingKeyWatch();
            try { rec.Stop(null); } catch { }
            try { rec.Dispose(); } catch { }
        }

        /// <summary>
        /// True when a GIF is being recorded right now, so the caller can turn a
        /// second trigger into "stop" instead of starting a new selection.
        /// </summary>
        internal bool IsRecordingGif
        {
            get
            {
                GifRecorder rec = _gifRecorder;
                return rec != null && (rec.IsRecording || rec.IsCountingDown);
            }
        }

        /// <summary>
        /// Ends a recording from the outside (Esc, the tray item, or a second
        /// press of the GIF hotkey). Returns false when nothing was recording.
        /// </summary>
        internal bool StopGifRecording()
        {
            if (!IsRecordingGif) return false;
            StopRecordingIfAny();
            return true;
        }

        void StartRecordingKeyWatch()
        {
            StopRecordingKeyWatch();
            KeyboardHook hook = new KeyboardHook();
            hook.EscapePressed += delegate { StopGifRecording(); };
            if (hook.Install()) _recordingKeys = hook;
            else hook.Dispose();
            if (_stopGifItem != null) _stopGifItem.Enabled = true;
        }

        void StopRecordingKeyWatch()
        {
            if (_stopGifItem != null) _stopGifItem.Enabled = false;
            KeyboardHook hook = _recordingKeys;
            if (hook == null) return;
            _recordingKeys = null;
            try { hook.Dispose(); } catch { }
        }

        /// <summary>
        /// Captures the top-level window under the pointer, saving it.
        ///
        /// The window is resolved *before* anything of ours is shown, because
        /// once an overlay is up the window under the pointer is us. Bounds come
        /// from Native.WindowBounds, which uses DWMWA_EXTENDED_FRAME_BOUNDS - see
        /// the comment there for why GetWindowRect is wrong at this DPI.
        /// </summary>
        void WindowScreenshot()
        {
            IntPtr target = IntPtr.Zero;
            try
            {
                Point cursor = Cursor.Position;
                IntPtr under = Native.RootWindowAt(cursor);
                // Refuse to capture our own windows. The overlay and every pin
                // are excluded, so hovering our own UI cannot produce a
                // screenshot of the screenshot tool.
                IntPtr[] mine = OwnWindowHandles();
                target = Native.IsCapturableWindow(under, mine) ? under : IntPtr.Zero;

                if (target == IntPtr.Zero)
                {
                    Balloon("窗口截图", "指针下没有可截取的窗口。", ToolTipIcon.Warning);
                    return;
                }

                Rectangle bounds = Native.WindowBounds(target);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Balloon("窗口截图", "无法获取窗口尺寸。", ToolTipIcon.Warning);
                    return;
                }

                // Hide our overlay for the duration so it cannot land inside the
                // capture even if the target window overlaps it.
                bool overlayWasVisible = _overlay.Visible;
                if (overlayWasVisible) _overlay.Hide();

                Bitmap shot = null;
                try
                {
                    shot = Native.CaptureRegion(bounds);
                    if (shot == null)
                    {
                        Balloon("窗口截图", "无法抓取窗口内容。", ToolTipIcon.Warning);
                        return;
                    }

                    string path = ChooseSavePath();
                    if (path == null) return;
                    SaveBitmap(shot, path);
                    Balloon("窗口截图已保存", path, ToolTipIcon.Info);
                    PlayShotSound();
                }
                finally
                {
                    if (shot != null) shot.Dispose();
                    if (overlayWasVisible)
                    {
                        try { _overlay.ResumeAfterCapture(); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Balloon("窗口截图失败", ex.Message, ToolTipIcon.Warning);
            }
        }

        /// <summary>Every top-level window this process owns, for self-exclusion.</summary>
        IntPtr[] OwnWindowHandles()
        {
            List<IntPtr> list = new List<IntPtr>();
            try { if (_overlay != null && _overlay.IsHandleCreated) list.Add(_overlay.Handle); }
            catch { }
            // HotkeyWindow is a NativeWindow, not a Control, so it has no
            // IsHandleCreated; its handle is created in the constructor.
            try { if (_msgWindow != null) list.Add(_msgWindow.Handle); }
            catch { }
            try
            {
                foreach (PinForm p in _pins)
                {
                    try { if (p != null && p.IsHandleCreated) list.Add(p.Handle); }
                    catch { }
                }
            }
            catch { }
            try
            {
                if (_settingsForm != null && _settingsForm.IsHandleCreated)
                    list.Add(_settingsForm.Handle);
            }
            catch { }
            return list.ToArray();
        }

        /// <summary>
        /// Colour picker: shows a full-screen frozen backdrop with a magnifier
        /// loupe; clicking copies the pixel under the pointer and selects it as
        /// the current pen colour, so "pick then draw" is one gesture.
        /// </summary>
        void StartColorPicker()
        {
            bool overlayWasVisible = _overlay.Visible;
            // The loupe magnifies the live desktop, so a circle drawn at the
            // pointer would be magnified too - and the sampled pixel would be the
            // circle's own colour.
            SuspendSpot();
            try
            {
                Rectangle desktop = Native.VirtualScreen();
                Bitmap frozen = overlayWasVisible
                    ? _overlay.RenderComposite(false)
                    : Native.CaptureRegion(desktop);
                if (frozen == null)
                {
                    Balloon("取色失败", "无法抓取屏幕内容。", ToolTipIcon.Warning);
                    return;
                }

                if (overlayWasVisible) _overlay.Hide();

                Color picked;
                // Ownership of `frozen` transfers to the picker unconditionally:
                // this method has no other reference to it, so if the form did
                // not dispose it nothing would.
                using (ColorPickerForm picker =
                       new ColorPickerForm(_cfg, desktop, frozen, true))
                {
                    picker.ShowDialog();
                    picked = picker.PickedColor;
                }

                if (picked.IsEmpty)
                {
                    if (overlayWasVisible)
                    {
                        try { _overlay.ResumeAfterCapture(); }
                        catch { }
                    }
                    return;
                }

                // Feed the colour straight into the pen palette's active slot, so
                // picking a colour and drawing with it is one flow rather than
                // "copy hex, open settings, paste".
                _overlay.SetColorFromPicker(picked);

                string hex = Beautifier.FormatColor(picked);
                try
                {
                    Clipboard.SetText(hex);
                    Balloon("已取色 " + hex,
                            "颜色已设为当前画笔色，并复制到剪贴板。", ToolTipIcon.Info);
                }
                catch
                {
                    Balloon("已取色 " + hex, "颜色已设为当前画笔色。", ToolTipIcon.Info);
                }

                if (overlayWasVisible)
                {
                    try { _overlay.ResumeAfterCapture(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Balloon("取色失败", ex.Message, ToolTipIcon.Warning);
            }
            finally { ResumeSpot(); }
        }

        /// <summary>
        /// Shows a new 贴图 window that takes ownership of <paramref name="bmp"/>,
        /// placed over the region the user just selected.
        ///
        /// The ownership transfer is atomic from the caller's point of view:
        /// either the pin owns the bitmap, or this method has already disposed it.
        /// That is what makes "hand over, then forget the local" safe - there is
        /// no path on which the pixels are leaked *or* released twice.
        ///
        /// Errors are reported non-modally and swallowed; a failed pin must never
        /// take down the message loop.
        /// </summary>
        void PinScreenshot(Bitmap bmp, Rectangle screenRect)
        {
            if (bmp == null) return;

            PinForm pin = null;
            try
            {
                pin = new PinForm(bmp, screenRect);

                // ---- ownership hand-off point ------------------------------
                // Past this line the window owns the pixels, so the local is
                // cleared and no later path - including the catch below - can
                // release them a second time.
                bmp = null;

                pin.FormClosed += PinFormClosed;
                _pins.Add(pin);
                pin.Show();
            }
            catch (Exception)
            {
                if (bmp != null)
                {
                    // The constructor threw before it took ownership: release the
                    // pixels here, exactly once. (PinForm only disposes an image
                    // on the explicit Dispose path, never from the finalizer, so
                    // a half-built window cannot release it behind our back.)
                    try { bmp.Dispose(); }
                    catch (Exception) { }
                    Balloon("贴图失败", "无法创建贴图窗口。", ToolTipIcon.Warning);
                }
                else
                {
                    // The window was built and owns the bitmap, but showing it
                    // failed. Discard the window (which frees the bitmap) rather
                    // than the bitmap, so the pixels are still freed once.
                    try { if (pin != null) pin.FormClosed -= PinFormClosed; }
                    catch (Exception) { }
                    try { if (pin != null) _pins.Remove(pin); }
                    catch (Exception) { }
                    try { if (pin != null && !pin.IsDisposed) pin.Dispose(); }
                    catch (Exception) { }
                    Balloon("贴图失败", "无法显示贴图窗口。", ToolTipIcon.Warning);
                }
            }
        }

        /// <summary>
        /// Drops a closed pin from the tracking list and releases the window.
        /// The window already disposes its own bitmap; this only frees the handle.
        /// </summary>
        void PinFormClosed(object sender, FormClosedEventArgs e)
        {
            PinForm pin = sender as PinForm;
            if (pin == null) return;

            try { pin.FormClosed -= PinFormClosed; }
            catch (Exception) { }
            try { _pins.Remove(pin); }
            catch (Exception) { }
            try
            {
                // The user closed the window; dispose it so its GDI resources do
                // not accumulate across many pins in one session.
                if (!pin.IsDisposed) pin.Dispose();
            }
            catch (Exception) { }
        }

        /// <summary>Closes and releases every open 贴图 window.</summary>
        void CloseAllPins()
        {
            // Iterate a copy: disposing a pin raises nothing here (the handler is
            // unhooked first in PinFormClosed), but Close() can pump messages and
            // re-enter, and mutating a list while enumerating it is a hard crash.
            PinForm[] snapshot = _pins.ToArray();
            _pins.Clear();

            foreach (PinForm pin in snapshot)
            {
                if (pin == null) continue;
                try { pin.FormClosed -= PinFormClosed; }
                catch (Exception) { }
                try { if (!pin.IsDisposed) pin.Close(); }
                catch (Exception) { }
                try { if (!pin.IsDisposed) pin.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Puts an image on the clipboard, retrying briefly because another process may
        /// hold it open. Clipboard.SetImage copies the pixels, so the caller may dispose
        /// its bitmap as soon as this returns true.
        ///
        /// Never throws and never shows a modal dialog: the previous code called
        /// Clipboard.SetImage directly and popped a MessageBox whenever another app
        /// held the clipboard, which blocked the tray from a background hotkey.
        /// </summary>
        bool SafeSetClipboardImage(Bitmap img)
        {
            if (img == null) return false;

            // CF_BITMAP is the format most consumers expect; a clipboard owner that
            // is mid-write usually lets go within a few hundred milliseconds.
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetImage(img); return true; }
                catch (Exception) { }
                try { System.Threading.Thread.Sleep(60); }
                catch (Exception) { }
            }

            // Some owners refuse CF_BITMAP but accept the data-object path, which
            // offers a DIB alongside it (copy: true, so the bitmap stays ours).
            for (int i = 0; i < 2; i++)
            {
                try { Clipboard.SetDataObject(img, true); return true; }
                catch (Exception) { }
                try { System.Threading.Thread.Sleep(60); }
                catch (Exception) { }
            }

            return false;
        }

        /// <summary>Puts text on the clipboard, retrying while another app holds it.</summary>
        bool SafeSetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch (Exception) { }
                try { System.Threading.Thread.Sleep(60); }
                catch (Exception) { }
            }
            return false;
        }

        void PlayShotSound()
        {
            if (!_cfg.PlaySoundOnScreenshot) return;
            try { SystemSounds.Asterisk.Play(); }
            catch { }
        }

        /// <summary>
        /// Returns the destination path, or null if the user cancelled.
        /// Honours the "save silently to folder" setting.
        /// </summary>
        string ChooseSavePath()
        {
            string ext = (_cfg.ScreenshotFormat ?? "png").ToLowerInvariant();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string name = "ScreenPen_" + stamp + "." + ext;

            if (_cfg.SaveScreenshotsToDefaultFolder &&
                !string.IsNullOrEmpty(_cfg.ScreenshotFolder))
            {
                try
                {
                    if (!Directory.Exists(_cfg.ScreenshotFolder))
                        Directory.CreateDirectory(_cfg.ScreenshotFolder);
                    return Path.Combine(_cfg.ScreenshotFolder, name);
                }
                catch
                {
                    // Fall through to the dialog so the user is never stuck.
                }
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "保存截图";
                sfd.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|BMP 图片 (*.bmp)|*.bmp";
                sfd.FileName = name;
                sfd.DefaultExt = ext;
                sfd.AddExtension = true;
                sfd.OverwritePrompt = true;
                string initial = _cfg.ScreenshotFolder;
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    sfd.InitialDirectory = initial;
                else
                    sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                if (sfd.ShowDialog() != DialogResult.OK) return null;
                return sfd.FileName;
            }
        }

        /// <summary>
        /// Writes a bitmap to disk, applying the beautifier first when it is
        /// enabled. An instance method (not static) because the beautifier reads
        /// the live settings, and the settings object is owned by the context.
        ///
        /// If the beautifier fails for any reason the raw screenshot is still
        /// written: a saved screenshot without the decoration is strictly better
        /// than an error dialog and no file.
        /// </summary>
        void SaveBitmap(Bitmap bmp, string path)
        {
            if (_cfg != null && _cfg.BeautifyEnabled)
            {
                Bitmap pretty = null;
                try
                {
                    pretty = Beautifier.Render(bmp, _cfg);
                    if (pretty != null)
                    {
                        SaveBitmapRaw(pretty, path);
                        return;
                    }
                }
                catch { }
                finally { if (pretty != null) pretty.Dispose(); }
            }
            SaveBitmapRaw(bmp, path);
        }

        static void SaveBitmapRaw(Bitmap bmp, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            ImageFormat fmt = ImageFormat.Png;
            if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
            else if (ext == ".bmp") fmt = ImageFormat.Bmp;

            // Flatten onto a solid background: JPEG/BMP have no alpha channel,
            // and a transparent PNG of a full screenshot helps nobody.
            using (Bitmap flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.Black);
                    g.DrawImageUnscaled(bmp, 0, 0);
                }
                flat.Save(path, fmt);
            }
        }

        void CopyToClipboard()
        {
            Bitmap bmp = null;
            try
            {
                bmp = _overlay.Visible
                    ? _overlay.RenderComposite(false)
                    : Native.CaptureRegion(Native.VirtualScreen());

                if (bmp == null) { Balloon("复制失败", "无法生成图像。", ToolTipIcon.Warning); return; }

                // The staging copy exists only to guarantee a 32bppArgb surface for
                // the clipboard, so it is scoped with using: the old code leaked it
                // (~20.6 MB per invocation on a 2860x1800 display) on every tray
                // click and every Ctrl+C. SafeSetClipboardImage's CF_BITMAP path
                // copies the pixels, so releasing it right after is correct.
                bool copied;
                using (Bitmap copy = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(bmp, 0, 0);
                    copied = SafeSetClipboardImage(copy);
                }

                if (copied)
                    Balloon("已复制", "画面已复制到剪贴板，可直接粘贴。", ToolTipIcon.Info);
                else
                    // Non-modal on purpose: this path also runs from a global hotkey
                    // while no window is up, where a MessageBox would be a dead end.
                    Balloon("复制失败", "剪贴板被其它程序占用，请稍后重试。", ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                Balloon("复制到剪贴板失败", ex.Message, ToolTipIcon.Warning);
            }
            finally { if (bmp != null) bmp.Dispose(); }
        }

        // =====================================================================
        //  Settings
        // =====================================================================

        /// <summary>
        /// Flips the 浮标 on or off. Reachable from the tray tick and the global
        /// hotkey, so it has to keep three things in step: the settings object,
        /// the overlay itself, and the tray tick.
        /// </summary>
        void ToggleCursorSpot(bool fromTray)
        {
            _cfg.CursorSpotEnabled = !_cfg.CursorSpotEnabled;

            if (_spot != null) _spot.ApplySettings(_cfg);
            if (_spotItem != null) _spotItem.Checked = _cfg.CursorSpotEnabled;

            // Persisted so the choice survives a restart. A failed write is not
            // worth a dialog: the toggle has already taken effect for this run,
            // and OpenSettings reports write failures where they matter.
            try { SettingsStore.Save(_cfg); } catch { }

            // The tray click already shows the new state in its tick; a hotkey
            // press has no visible feedback at all, so it gets a balloon.
            if (!fromTray)
            {
                Balloon(_cfg.CursorSpotEnabled ? "浮标已开启" : "浮标已关闭",
                        _cfg.CursorSpotEnabled
                            ? "指针周围会显示半透明圆圈。大小、颜色和透明度可在「设置 → 浮标」里调整。"
                            : "按 " + _cfg.CursorSpotKey + " 可再次打开。",
                        ToolTipIcon.Info);
            }
        }

        /// <summary>Test seam: the live spot overlay, if one was created.</summary>
        internal CursorSpotOverlay CursorSpotForTest { get { return _spot; } }

        /// <summary>Test seam: runs the same toggle the tray item and hotkey use.</summary>
        internal void ToggleCursorSpotForTest(bool fromTray) { ToggleCursorSpot(fromTray); }

        /// <summary>Test seam: whether the tray item's tick matches the setting.</summary>
        internal bool CursorSpotTrayCheckedForTest
        {
            get { return _spotItem != null && _spotItem.Checked; }
        }

        /// <summary>
        /// Takes the circle off the screen while the user is aiming at something
        /// precise: region selection and the colour picker.
        ///
        /// Both hand the user a frozen screenshot to aim at, and a circle that
        /// keeps drawing on top of it is at best distracting - for the picker it
        /// is actively wrong, because the loupe magnifies whatever is on screen,
        /// so the sampled pixel would be the circle's own colour.
        /// </summary>
        void SuspendSpot()
        {
            if (_spot != null) _spot.Suspend();
        }

        void ResumeSpot()
        {
            if (_spot != null) _spot.Resume();
        }

        void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.BringToFront();
                _settingsForm.Activate();
                return;
            }

            HideOverlay();
            _settingsForm = new SettingsForm(_cfg, IsHotkeyAvailable);

            // Draw the 浮标 page's values on the real desktop while the dialog is
            // open. Without this the user tunes a circle they cannot see: the
            // values only reach the overlay on 保存, and the feature is usually
            // still switched off while it is being set up.
            _settingsForm.SpotPreviewChanged = delegate(Settings draft)
            {
                if (_spot != null) _spot.BeginPreview(draft);
            };

            DialogResult r = _settingsForm.ShowDialog();

            // Whatever the answer, drop the preview: OK adopts the new settings
            // through ApplySettings below, Cancel must restore the old look.
            if (_spot != null) _spot.EndPreview();

            if (r == DialogResult.OK && _settingsForm.Result != null)
            {
                bool wasTray = _cfg.ShowTrayIcon;
                _cfg = _settingsForm.Result;

                // SettingsStore.Save now returns false instead of throwing when the
                // file cannot be written (read-only directory, disk full, ...).
                // This call used to be unguarded and sat *before* ApplySettings and
                // RegisterAllHotkeys, so a throw there silently skipped both and
                // propagated into the message loop. Report the failure but carry on
                // regardless: the user's changes must still take effect for this
                // run, and only the persistence is lost.
                bool saved = SettingsStore.Save(_cfg);
                if (!saved)
                    Balloon("设置保存失败", "配置未能写入磁盘，本次改动仅对当前运行有效。",
                            ToolTipIcon.Warning);

                // Push live-updatable settings into the overlay.
                _overlay.ApplySettings(_cfg);
                if (_spot != null) _spot.ApplySettings(_cfg);
                if (_spotItem != null) _spotItem.Checked = _cfg.CursorSpotEnabled;

                if (_tray != null) _tray.Visible = _cfg.ShowTrayIcon;
                RegisterAllHotkeys();

                if (!_cfg.ShowTrayIcon && wasTray)
                    Balloon("托盘图标已隐藏", "可通过再次运行本程序打开设置窗口。", ToolTipIcon.Info);
                else if (_failedHotkeys.Count > 0)
                    // The settings dialog validated the combinations, but the OS
                    // can still refuse one that another process grabbed. Say so
                    // instead of pretending the save worked.
                    ReportHotkeyProblems();
                else
                    Balloon("设置已保存", "快捷键已重新注册。", ToolTipIcon.Info);
            }
            _settingsForm.Dispose();
            _settingsForm = null;
        }

        // =====================================================================
        //  Shutdown
        // =====================================================================

        public void Quit()
        {
            try
            {
                // Set before the recording is stopped: the recorder raises
                // Finished from inside Stop(), and the result window must not be
                // shown from the shutdown path or the process would never exit.
                _quitting = true;
                UnregisterAll();
                HideOverlay();
                // A recording in progress owns a border window and a timer; write
                // out what was captured and tear them down before exiting.
                StopRecordingIfAny();
                // Pins are unowned top-level windows: without this they would stay
                // floating over the desktop after the process exits its tray.
                CloseAllPins();
                // Same for the two 浮标 windows, and disposing also unhooks the
                // static capture guard so it cannot outlive the process's state.
                if (_spot != null) { try { _spot.Dispose(); } catch { } _spot = null; }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_msgWindow != null) _msgWindow.DestroyHandle();
            }
            catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { CloseAllPins(); } catch { }
                try { if (_spot != null) { _spot.Dispose(); _spot = null; } } catch { }
                try { if (_tray != null) _tray.Dispose(); } catch { }
                try { if (_msgWindow != null) _msgWindow.Dispose(); } catch { }
                try { if (_overlay != null) _overlay.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Invisible window whose only job is to receive WM_HOTKEY and the
    /// single-instance activation broadcast.
    /// </summary>
    public sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event EventHandler<int> HotkeyPressed;
        public event EventHandler ActivateRequested;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ScreenPen.MessageWindow",
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                Style = 0,
                ExStyle = 0,
                Parent = IntPtr.Zero
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                EventHandler<int> h = HotkeyPressed;
                if (h != null) h(this, m.WParam.ToInt32());
            }
            else if (m.Msg == Native.WM_SCREENPEN_ACTIVATE)
            {
                EventHandler h = ActivateRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { DestroyHandle(); }
            catch { }
        }
    }
}
