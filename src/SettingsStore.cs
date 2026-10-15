using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>Serialises <see cref="Settings"/> to and from the portable JSON file.</summary>
    public static class SettingsStore
    {
        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string path = Settings.SettingsPath();
                if (!File.Exists(path)) { s.Clamp(); return s; }
                string text = File.ReadAllText(path, Encoding.UTF8);
                var m = Json.ParseObject(text);

                s.ToggleKey = Json.Str(m, "ToggleKey", s.ToggleKey);
                s.LiveZoomKey = Json.Str(m, "LiveZoomKey", s.LiveZoomKey);
                s.ScreenshotKey = Json.Str(m, "ScreenshotKey", s.ScreenshotKey);
                s.CopyShotKey = Json.Str(m, "CopyShotKey", s.CopyShotKey);
                s.ClearKey = Json.Str(m, "ClearKey", s.ClearKey);
                s.UndoKey = Json.Str(m, "UndoKey", s.UndoKey);
                s.SaveKey = Json.Str(m, "SaveKey", s.SaveKey);
                s.CopyKey = Json.Str(m, "CopyKey", s.CopyKey);
                s.ZoomInKey = Json.Str(m, "ZoomInKey", s.ZoomInKey);
                s.ZoomOutKey = Json.Str(m, "ZoomOutKey", s.ZoomOutKey);

                s.PenKey = Json.Str(m, "PenKey", s.PenKey);
                s.LineKey = Json.Str(m, "LineKey", s.LineKey);
                s.RectKey = Json.Str(m, "RectKey", s.RectKey);
                s.EllipseKey = Json.Str(m, "EllipseKey", s.EllipseKey);
                s.ArrowKey = Json.Str(m, "ArrowKey", s.ArrowKey);
                s.TextKey = Json.Str(m, "TextKey", s.TextKey);
                s.MosaicKey = Json.Str(m, "MosaicKey", s.MosaicKey);
                s.SpotlightKey = Json.Str(m, "SpotlightKey", s.SpotlightKey);
                s.FollowKey = Json.Str(m, "FollowKey", s.FollowKey);
                s.HighlighterKey = Json.Str(m, "HighlighterKey", s.HighlighterKey);
                s.StepKey = Json.Str(m, "StepKey", s.StepKey);
                s.RedoKey = Json.Str(m, "RedoKey", s.RedoKey);
                s.ColorPickerKey = Json.Str(m, "ColorPickerKey", s.ColorPickerKey);
                s.WindowShotKey = Json.Str(m, "WindowShotKey", s.WindowShotKey);

                s.PenWidth = Json.Int(m, "PenWidth", s.PenWidth);
                s.MinPenWidth = Json.Int(m, "MinPenWidth", s.MinPenWidth);
                s.MaxPenWidth = Json.Int(m, "MaxPenWidth", s.MaxPenWidth);
                s.Palette = Json.List(m, "Palette", s.Palette);

                s.RunAtStartup = Json.Bool(m, "RunAtStartup", s.RunAtStartup);
                s.ShowTrayIcon = Json.Bool(m, "ShowTrayIcon", s.ShowTrayIcon);
                s.StartMinimized = Json.Bool(m, "StartMinimized", s.StartMinimized);
                s.MagnifierDuringLiveZoom = Json.Bool(m, "MagnifierDuringLiveZoom", s.MagnifierDuringLiveZoom);
                s.ZoomStepPercent = Json.Int(m, "ZoomStepPercent", s.ZoomStepPercent);
                s.MinZoom = Json.Flt(m, "MinZoom", s.MinZoom);
                s.MaxZoom = Json.Flt(m, "MaxZoom", s.MaxZoom);
                s.SmoothLines = Json.Bool(m, "SmoothLines", s.SmoothLines);
                s.SaveScreenshotsToDefaultFolder = Json.Bool(m, "SaveScreenshotsToDefaultFolder", s.SaveScreenshotsToDefaultFolder);
                s.ScreenshotFolder = Json.Str(m, "ScreenshotFolder", s.ScreenshotFolder);
                s.ScreenshotFormat = Json.Str(m, "ScreenshotFormat", s.ScreenshotFormat);
                s.PlaySoundOnScreenshot = Json.Bool(m, "PlaySoundOnScreenshot", s.PlaySoundOnScreenshot);

                s.RegionDimOutside = Json.Bool(m, "RegionDimOutside", s.RegionDimOutside);
                s.RegionShowSize = Json.Bool(m, "RegionShowSize", s.RegionShowSize);

                s.TextSize = Json.Flt(m, "TextSize", s.TextSize);
                s.MosaicBlock = Json.Flt(m, "MosaicBlock", s.MosaicBlock);
                s.SpotlightDim = Json.Flt(m, "SpotlightDim", s.SpotlightDim);

                s.ModCtrlRectangle = Json.Bool(m, "ModCtrlRectangle", s.ModCtrlRectangle);
                s.ModTabEllipse = Json.Bool(m, "ModTabEllipse", s.ModTabEllipse);
                s.ModShiftLine = Json.Bool(m, "ModShiftLine", s.ModShiftLine);
                s.ModCtrlShiftArrow = Json.Bool(m, "ModCtrlShiftArrow", s.ModCtrlShiftArrow);

                s.ShowBrushCursor = Json.Bool(m, "ShowBrushCursor", s.ShowBrushCursor);
                s.BrushCursorSize = Json.Int(m, "BrushCursorSize", s.BrushCursorSize);

                s.ScrollFollowEnabled = Json.Bool(m, "ScrollFollowEnabled", s.ScrollFollowEnabled);
                s.WheelScrollsDocument = Json.Bool(m, "WheelScrollsDocument", s.WheelScrollsDocument);
                s.ScrollFollowClickThrough = Json.Bool(m, "ScrollFollowClickThrough", s.ScrollFollowClickThrough);

                s.HighlighterAlpha = Json.Int(m, "HighlighterAlpha", s.HighlighterAlpha);
                s.ArrowStyleIndex = Json.Int(m, "ArrowStyleIndex", s.ArrowStyleIndex);
                s.StepBadgeSize = Json.Flt(m, "StepBadgeSize", s.StepBadgeSize);
                s.StepNumberPersistent = Json.Bool(m, "StepNumberPersistent", s.StepNumberPersistent);

                s.BeautifyEnabled = Json.Bool(m, "BeautifyEnabled", s.BeautifyEnabled);
                s.BeautifyPadding = Json.Int(m, "BeautifyPadding", s.BeautifyPadding);
                s.BeautifyRadius = Json.Int(m, "BeautifyRadius", s.BeautifyRadius);
                s.BeautifyShadow = Json.Bool(m, "BeautifyShadow", s.BeautifyShadow);
                s.BeautifyColor1 = Json.Str(m, "BeautifyColor1", s.BeautifyColor1);
                s.BeautifyColor2 = Json.Str(m, "BeautifyColor2", s.BeautifyColor2);

                s.PickerLoupeSize = Json.Int(m, "PickerLoupeSize", s.PickerLoupeSize);
                s.PickerZoom = Json.Int(m, "PickerZoom", s.PickerZoom);
            }
            catch
            {
                // A corrupt settings file must never stop the app from starting.
                s = new Settings();
            }
            s.Clamp();
            return s;
        }

        /// <summary>
        /// Persists the settings. Returns true on success and false when the
        /// file could not be written; it never throws, because a read-only or
        /// full disk must not take the application down.
        /// </summary>
        public static bool Save(Settings s)
        {
            if (s == null) return false;
            string tmp = null;
            try
            {
                s.Clamp();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                var lines = new List<string>();

                Action<string, string> S = (k, v) => lines.Add("  " + Json.Escape(k) + ": " + Json.Escape(v));
                Action<string, int> I = (k, v) => lines.Add("  " + Json.Escape(k) + ": " + v.ToString(CultureInfo.InvariantCulture));
                Action<string, float> F = (k, v) => lines.Add("  " + Json.Escape(k) + ": " + v.ToString("0.###", CultureInfo.InvariantCulture));
                Action<string, bool> B = (k, v) => lines.Add("  " + Json.Escape(k) + ": " + (v ? "true" : "false"));

                S("ToggleKey", s.ToggleKey);
                S("LiveZoomKey", s.LiveZoomKey);
                S("ScreenshotKey", s.ScreenshotKey);
                S("CopyShotKey", s.CopyShotKey);
                S("ClearKey", s.ClearKey);
                S("UndoKey", s.UndoKey);
                S("SaveKey", s.SaveKey);
                S("CopyKey", s.CopyKey);
                S("ZoomInKey", s.ZoomInKey);
                S("ZoomOutKey", s.ZoomOutKey);
                S("PenKey", s.PenKey);
                S("LineKey", s.LineKey);
                S("RectKey", s.RectKey);
                S("EllipseKey", s.EllipseKey);
                S("ArrowKey", s.ArrowKey);
                S("TextKey", s.TextKey);
                S("MosaicKey", s.MosaicKey);
                S("SpotlightKey", s.SpotlightKey);
                S("FollowKey", s.FollowKey);
                S("HighlighterKey", s.HighlighterKey);
                S("StepKey", s.StepKey);
                S("RedoKey", s.RedoKey);
                S("ColorPickerKey", s.ColorPickerKey);
                S("WindowShotKey", s.WindowShotKey);
                I("HighlighterAlpha", s.HighlighterAlpha);
                I("ArrowStyleIndex", s.ArrowStyleIndex);
                F("StepBadgeSize", s.StepBadgeSize);
                B("StepNumberPersistent", s.StepNumberPersistent);
                B("BeautifyEnabled", s.BeautifyEnabled);
                I("BeautifyPadding", s.BeautifyPadding);
                I("BeautifyRadius", s.BeautifyRadius);
                B("BeautifyShadow", s.BeautifyShadow);
                S("BeautifyColor1", s.BeautifyColor1);
                S("BeautifyColor2", s.BeautifyColor2);
                I("PickerLoupeSize", s.PickerLoupeSize);
                I("PickerZoom", s.PickerZoom);
                I("PenWidth", s.PenWidth);
                I("MinPenWidth", s.MinPenWidth);
                I("MaxPenWidth", s.MaxPenWidth);

                var quoted = new List<string>();
                foreach (string c in s.Palette) quoted.Add(Json.Escape(c));
                lines.Add("  \"Palette\": [" + string.Join(", ", quoted.ToArray()) + "]");

                B("RunAtStartup", s.RunAtStartup);
                B("ShowTrayIcon", s.ShowTrayIcon);
                B("StartMinimized", s.StartMinimized);
                B("MagnifierDuringLiveZoom", s.MagnifierDuringLiveZoom);
                I("ZoomStepPercent", s.ZoomStepPercent);
                F("MinZoom", s.MinZoom);
                F("MaxZoom", s.MaxZoom);
                B("SmoothLines", s.SmoothLines);
                B("SaveScreenshotsToDefaultFolder", s.SaveScreenshotsToDefaultFolder);
                S("ScreenshotFolder", s.ScreenshotFolder);
                S("ScreenshotFormat", s.ScreenshotFormat);
                B("PlaySoundOnScreenshot", s.PlaySoundOnScreenshot);

                B("RegionDimOutside", s.RegionDimOutside);
                B("RegionShowSize", s.RegionShowSize);

                F("TextSize", s.TextSize);
                F("MosaicBlock", s.MosaicBlock);
                F("SpotlightDim", s.SpotlightDim);

                B("ModCtrlRectangle", s.ModCtrlRectangle);
                B("ModTabEllipse", s.ModTabEllipse);
                B("ModShiftLine", s.ModShiftLine);
                B("ModCtrlShiftArrow", s.ModCtrlShiftArrow);

                B("ShowBrushCursor", s.ShowBrushCursor);
                I("BrushCursorSize", s.BrushCursorSize);
                S("FollowKey", s.FollowKey);

                B("ScrollFollowEnabled", s.ScrollFollowEnabled);
                B("WheelScrollsDocument", s.WheelScrollsDocument);
                B("ScrollFollowClickThrough", s.ScrollFollowClickThrough);

                sb.AppendLine(string.Join("," + Environment.NewLine, lines.ToArray()));
                sb.AppendLine("}");

                string path = Settings.SettingsPath();
                tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));

                // Replace the live file atomically instead of deleting it first:
                // a crash between the old Delete and Move left no config at all,
                // silently resetting every setting on the next start. Passing a
                // null backup keeps the directory free of stray .bak files.
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                tmp = null;
                return true;
            }
            catch
            {
                // Persisting settings is best-effort: a full disk, a read-only
                // install directory or a locked .tmp must not throw at callers.
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); }
                catch { }
                return false;
            }
        }
    }

    /// <summary>
    /// A parsed global hotkey: modifiers plus a WinForms key.
    /// Text syntax, e.g. "Ctrl+Alt+D", "E", "Ctrl+Shift+F5".
    /// </summary>
    public sealed class HotKey
    {
        public Keys Key = Keys.None;
        public bool Ctrl, Alt, Shift;
        public bool IsEmpty { get { return Key == Keys.None; } }

        public uint Modifiers()
        {
            uint m = 0;
            if (Alt) m |= Native.MOD_ALT;
            if (Ctrl) m |= Native.MOD_CONTROL;
            if (Shift) m |= Native.MOD_SHIFT;
            return m;
        }

        public HotKey Clone()
        {
            return new HotKey { Key = Key, Ctrl = Ctrl, Alt = Alt, Shift = Shift };
        }

        public override string ToString()
        {
            if (IsEmpty) return "";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join("+", parts.ToArray());
        }

        /// <summary>Formats a key without the enum's "Oem"/"D1" noise.</summary>
        public static string KeyName(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if (code >= Keys.D0 && code <= Keys.D9)
                return ((int)(code - Keys.D0)).ToString(CultureInfo.InvariantCulture);
            if (code >= Keys.NumPad0 && code <= Keys.NumPad9)
                return "Num" + ((int)(code - Keys.NumPad0)).ToString(CultureInfo.InvariantCulture);
            switch (code)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Space: return "Space";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Capital: return "CapsLock";
                case Keys.Scroll: return "ScrollLock";
                case Keys.Snapshot: return "PrintScreen";
                default: return code.ToString();
            }
        }

        public static HotKey Parse(string text)
        {
            HotKey hk = new HotKey();
            if (string.IsNullOrWhiteSpace(text)) return hk;
            foreach (string rawPart in text.Split('+'))
            {
                string p = rawPart.Trim();
                if (p.Length == 0) continue;
                string low = p.ToLowerInvariant();
                if (low == "ctrl" || low == "control") { hk.Ctrl = true; continue; }
                if (low == "alt") { hk.Alt = true; continue; }
                if (low == "shift") { hk.Shift = true; continue; }
                if (low == "win" || low == "windows") continue; // Win key unsupported by RegisterHotKey here
                Keys k = ParseKey(p);
                if (k != Keys.None) hk.Key = k;
            }
            return hk;
        }

        static Keys ParseKey(string p)
        {
            string s = p.Trim();
            if (s.Length == 1)
            {
                char c = char.ToUpperInvariant(s[0]);
                if (c >= 'A' && c <= 'Z') return (Keys)Enum.Parse(typeof(Keys), c.ToString());
                if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
                switch (c)
                {
                    case '`': return Keys.Oemtilde;
                    case '-': return Keys.OemMinus;
                    case '=': return Keys.Oemplus;
                    case '[': return Keys.OemOpenBrackets;
                    case ']': return Keys.OemCloseBrackets;
                    case '\\': return Keys.OemPipe;
                    case ';': return Keys.OemSemicolon;
                    case '\'': return Keys.OemQuotes;
                    case ',': return Keys.Oemcomma;
                    case '.': return Keys.OemPeriod;
                    case '/': return Keys.OemQuestion;
                }
            }
            // Friendly aliases
            switch (s.ToLowerInvariant())
            {
                case "esc": case "escape": return Keys.Escape;
                case "enter": case "return": return Keys.Return;
                case "space": case "spacebar": return Keys.Space;
                case "tab": return Keys.Tab;
                case "backspace": case "back": return Keys.Back;
                case "del": case "delete": return Keys.Delete;
                case "ins": case "insert": return Keys.Insert;
                case "home": return Keys.Home;
                case "end": return Keys.End;
                case "pageup": case "pgup": return Keys.Prior;
                case "pagedown": case "pgdn": return Keys.Next;
                case "up": return Keys.Up;
                case "down": return Keys.Down;
                case "left": return Keys.Left;
                case "right": return Keys.Right;
                case "printscreen": case "prtsc": return Keys.Snapshot;
                case "capslock": return Keys.Capital;
                case "scrolllock": return Keys.Scroll;
                case "pause": return Keys.Pause;
            }
            if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f'))
            {
                int fn;
                if (int.TryParse(s.Substring(1), out fn) && fn >= 1 && fn <= 24)
                    return Keys.F1 + (fn - 1);
            }
            if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase))
            {
                int d;
                if (int.TryParse(s.Substring(3), out d) && d >= 0 && d <= 9)
                    return Keys.NumPad0 + d;
            }
            try
            {
                return (Keys)Enum.Parse(typeof(Keys), s, true);
            }
            catch { return Keys.None; }
        }
    }
}
