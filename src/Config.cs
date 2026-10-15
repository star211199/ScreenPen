using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ScreenPen
{
    /// <summary>Drawing tool selected by the user.</summary>
    public enum Tool
    {
        Pen = 0,
        Line = 1,
        Rectangle = 2,
        Ellipse = 3,
        Arrow = 4,
        /// <summary>Click to place a text label.</summary>
        Text = 5,
        /// <summary>Drag a rectangle whose contents are pixelated.</summary>
        Mosaic = 6,
        /// <summary>Dim everything outside an ellipse to highlight an area.</summary>
        Spotlight = 7
    }

    /// <summary>
    /// A single persisted line/shape stroke. Points are stored in *screen*
    /// (virtual-desktop) coordinates so that strokes survive pan and zoom.
    /// </summary>
    public sealed class Stroke
    {
        public Tool Tool = Tool.Pen;
        public Color Color = Color.Red;
        public float Width = 3f;
        public List<PointF> Points = new List<PointF>();

        /// <summary>True when this stroke is a screen-space snapshot (pinned).</summary>
        public bool Pinned;

        /// <summary>Baseline zoom at capture time, for pinned snapshots.</summary>
        public float PinZoom = 1f;

        /// <summary>Label text, for <see cref="Tool.Text"/> strokes.</summary>
        public string Text;

        /// <summary>Font size in screen units, for text strokes.</summary>
        public float FontSize = 22f;

        /// <summary>Pixel block size in screen units, for mosaic strokes.</summary>
        public float BlockSize = 12f;

        /// <summary>Whether the text is drawn with a contrasting outline.</summary>
        public bool TextShadow = true;
    }

    /// <summary>
    /// A pinned snapshot of the screen, frozen at a zoom level so the user can
    /// write on it while continuing to pan/zoom underneath (ScreenInk-style).
    /// </summary>
    public sealed class Pin
    {
        public Bitmap Image;
        public PointF Origin;     // top-left in screen coords
        public float Zoom = 1f;
        public Size ScreenSize;   // logical size in screen units
    }

    /// <summary>
    /// Application settings, persisted as JSON next to the executable so the
    /// app stays fully portable (no registry writes, no installer).
    /// </summary>
    public sealed class Settings
    {
        public string ToggleKey = "Ctrl+Alt+D";
        public string LiveZoomKey = "Ctrl+Alt+Z";
        public string ScreenshotKey = "Ctrl+Alt+S";
        public string CopyShotKey = "Ctrl+Alt+C";
        public string ClearKey = "E";
        public string UndoKey = "Ctrl+Z";
        public string SaveKey = "Ctrl+S";
        public string CopyKey = "Ctrl+C";
        public string ZoomInKey = "Ctrl+Up";
        public string ZoomOutKey = "Ctrl+Down";

        // In-overlay tool keys. These are plain keys handled locally by the
        // overlay (never registered globally) so they cannot hijack typing.
        public string PenKey = "P";
        public string LineKey = "L";
        public string RectKey = "R";
        public string EllipseKey = "O";
        public string ArrowKey = "A";
        public string TextKey = "T";
        public string MosaicKey = "M";
        public string SpotlightKey = "K";
        /// <summary>Toggles scroll-follow (live document) mode while annotating.</summary>
        public string FollowKey = "F";

        public int PenWidth = 3;
        public int MinPenWidth = 1;
        public int MaxPenWidth = 20;
        /// <summary>Palette; index 0 is the default colour.</summary>
        public List<string> Palette = new List<string>
        {
            "#FF3B30", "#FF9500", "#FFCC00", "#34C759",
            "#007AFF", "#AF52DE", "#FFFFFF", "#000000"
        };

        public bool RunAtStartup = false;
        public bool ShowTrayIcon = true;
        public bool StartMinimized = false;
        public bool MagnifierDuringLiveZoom = true;
        public int ZoomStepPercent = 25;
        public float MinZoom = 0.5f;
        public float MaxZoom = 8f;
        public bool SmoothLines = true;
        public bool SaveScreenshotsToDefaultFolder = false;
        public string ScreenshotFolder = "";
        public string ScreenshotFormat = "png";
        public bool PlaySoundOnScreenshot = false;

        // ---- region screenshot ----------------------------------------------
        /// <summary>Dim the area outside the region being selected.</summary>
        public bool RegionDimOutside = true;
        /// <summary>Show the pixel size next to the region cursor.</summary>
        public bool RegionShowSize = true;

        // ---- new tools -------------------------------------------------------
        /// <summary>Text tool font size, in screen pixels.</summary>
        public float TextSize = 22f;
        /// <summary>Mosaic pixel block size, in screen pixels.</summary>
        public float MosaicBlock = 12f;
        /// <summary>Spotlight dim strength, 0..1 (0.55 = fairly dark).</summary>
        public float SpotlightDim = 0.55f;

        // ---- modifier overrides ---------------------------------------------
        /// <summary>Holding Ctrl while dragging draws a rectangle.</summary>
        public bool ModCtrlRectangle = true;
        /// <summary>Holding Tab while dragging draws an ellipse.</summary>
        public bool ModTabEllipse = true;
        /// <summary>Holding Shift while dragging draws a straight line.</summary>
        public bool ModShiftLine = true;
        /// <summary>Holding Ctrl+Shift while dragging draws an arrow.</summary>
        public bool ModCtrlShiftArrow = true;

        // ---- brush cursor ----------------------------------------------------
        /// <summary>Draw the brush-cursor image at the pointer while annotating.</summary>
        public bool ShowBrushCursor = true;
        /// <summary>On-screen size of the brush cursor, in pixels.</summary>
        public int BrushCursorSize = 40;

        // ---- scroll-follow mode ---------------------------------------------
        /// <summary>
        /// When on, the wheel scrolls the document/web page underneath and any
        /// annotations are shifted to stay glued to the scrolled content.
        /// </summary>
        public bool ScrollFollowEnabled = true;
        /// <summary>
        /// Plain wheel scrolls the underlying document while annotating. When
        /// off, the plain wheel adjusts pen width instead (Ctrl+wheel always
        /// adjusts pen width).
        /// </summary>
        public bool WheelScrollsDocument = true;
        /// <summary>Set the overlay click-through while in scroll-follow mode.</summary>
        public bool ScrollFollowClickThrough = true;

        // ---- colour helpers -------------------------------------------------

        public Color[] ColorArray()
        {
            var list = new List<Color>();
            foreach (string hex in Palette)
            {
                Color c;
                if (TryParseColor(hex, out c)) list.Add(c);
            }
            if (list.Count == 0) list.Add(Color.Red);
            return list.ToArray();
        }

        public static bool TryParseColor(string hex, out Color color)
        {
            color = Color.Red;
            if (string.IsNullOrEmpty(hex)) return false;
            string s = hex.Trim().TrimStart('#');
            if (s.Length == 3)
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6) return false;
            int r, g, b;
            if (!int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
            if (!int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
            if (!int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            color = Color.FromArgb(255, r, g, b);
            return true;
        }

        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        // ---- location / io --------------------------------------------------

        public static string ExePath()
        {
            try { return System.Reflection.Assembly.GetEntryAssembly().Location; }
            catch { return Application.ExecutablePath; }
        }

        public static string SettingsPath()
        {
            return Path.ChangeExtension(ExePath(), ".settings.json");
        }

        public void Clamp()
        {
            if (MinPenWidth < 1) MinPenWidth = 1;
            if (MaxPenWidth < MinPenWidth) MaxPenWidth = MinPenWidth;
            if (PenWidth < MinPenWidth) PenWidth = MinPenWidth;
            if (PenWidth > MaxPenWidth) PenWidth = MaxPenWidth;
            if (ZoomStepPercent < 5) ZoomStepPercent = 5;
            if (ZoomStepPercent > 200) ZoomStepPercent = 200;
            if (MinZoom < 0.1f) MinZoom = 0.1f;
            if (MaxZoom < MinZoom) MaxZoom = MinZoom;
            if (Palette == null) Palette = new List<string>();
            if (Palette.Count == 0) Palette.Add("#FF3B30");
            // Whitelist the screenshot format: the value is concatenated into a
            // file name, so an unvalidated string such as "png/../../x" would let
            // Path.Combine normalise the write outside the screenshot folder.
            string f = (ScreenshotFormat ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (f == "jpeg") f = "jpg";
            if (f != "png" && f != "jpg" && f != "bmp") f = "png";
            ScreenshotFormat = f;

            if (TextSize < 8f) TextSize = 8f;
            if (TextSize > 200f) TextSize = 200f;
            if (MosaicBlock < 3f) MosaicBlock = 3f;
            if (MosaicBlock > 80f) MosaicBlock = 80f;
            if (SpotlightDim < 0.05f) SpotlightDim = 0.05f;
            if (SpotlightDim > 0.95f) SpotlightDim = 0.95f;
            if (BrushCursorSize < 16) BrushCursorSize = 16;
            if (BrushCursorSize > 128) BrushCursorSize = 128;

            // A blank tool key would make that tool unreachable, so restore the
            // shipped default rather than silently disabling the tool.
            Settings def = null;
            if (string.IsNullOrWhiteSpace(PenKey)) { def = def ?? new Settings(); PenKey = def.PenKey; }
            if (string.IsNullOrWhiteSpace(LineKey)) { def = def ?? new Settings(); LineKey = def.LineKey; }
            if (string.IsNullOrWhiteSpace(RectKey)) { def = def ?? new Settings(); RectKey = def.RectKey; }
            if (string.IsNullOrWhiteSpace(EllipseKey)) { def = def ?? new Settings(); EllipseKey = def.EllipseKey; }
            if (string.IsNullOrWhiteSpace(ArrowKey)) { def = def ?? new Settings(); ArrowKey = def.ArrowKey; }
            if (string.IsNullOrWhiteSpace(TextKey)) { def = def ?? new Settings(); TextKey = def.TextKey; }
            if (string.IsNullOrWhiteSpace(MosaicKey)) { def = def ?? new Settings(); MosaicKey = def.MosaicKey; }
            if (string.IsNullOrWhiteSpace(SpotlightKey)) { def = def ?? new Settings(); SpotlightKey = def.SpotlightKey; }
            if (string.IsNullOrWhiteSpace(CopyShotKey)) { def = def ?? new Settings(); CopyShotKey = def.CopyShotKey; }
            if (string.IsNullOrWhiteSpace(FollowKey)) { def = def ?? new Settings(); FollowKey = def.FollowKey; }
        }

        /// <summary>
        /// Maps a configured single-key string to a tool, or null when the key
        /// is not one of the tool bindings.
        /// </summary>
        public Tool? ToolForKey(Keys code)
        {
            if (MatchesToolKey(PenKey, code)) return Tool.Pen;
            if (MatchesToolKey(LineKey, code)) return Tool.Line;
            if (MatchesToolKey(RectKey, code)) return Tool.Rectangle;
            if (MatchesToolKey(EllipseKey, code)) return Tool.Ellipse;
            if (MatchesToolKey(ArrowKey, code)) return Tool.Arrow;
            if (MatchesToolKey(TextKey, code)) return Tool.Text;
            if (MatchesToolKey(MosaicKey, code)) return Tool.Mosaic;
            if (MatchesToolKey(SpotlightKey, code)) return Tool.Spotlight;
            return null;
        }

        static bool MatchesToolKey(string configured, Keys code)
        {
            HotKey hk = HotKey.Parse(configured);
            return hk.Key != Keys.None && hk.Key == code &&
                   !hk.Ctrl && !hk.Alt && !hk.Shift;
        }

        public Settings Clone()
        {
            Settings s = (Settings)MemberwiseClone();
            s.Palette = new List<string>(Palette);
            return s;
        }
    }

    /// <summary>
    /// Extremely small JSON reader/writer. Kept in-project so the executable
    /// has zero external dependencies and still builds with csc.exe alone.
    /// </summary>
    public static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Parses a flat JSON object into a string map (arrays become
        /// a pipe-delimited string, which is enough for our palette).</summary>
        public static Dictionary<string, string> ParseObject(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            int i = 0, n = text.Length;
            SkipWs(text, ref i);
            if (i >= n || text[i] != '{') return map;
            i++;
            while (i < n)
            {
                SkipWs(text, ref i);
                if (i < n && text[i] == '}') break;
                if (i >= n || text[i] != '"') break;
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                if (i >= n || text[i] != ':') break;
                i++;
                SkipWs(text, ref i);
                string val = ReadValue(text, ref i);
                if (key != null) map[key] = val;
                SkipWs(text, ref i);
                if (i < n && text[i] == ',') { i++; continue; }
                if (i < n && text[i] == '}') break;
            }
            return map;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string ReadValue(string s, ref int i)
        {
            if (i >= s.Length) return "";
            if (s[i] == '"') return ReadString(s, ref i);
            if (s[i] == '[')
            {
                int depth = 0;
                StringBuilder raw = new StringBuilder();
                var items = new List<string>();
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '"')
                    {
                        string str = ReadString(s, ref i);
                        if (depth == 1) items.Add(str);
                        continue;
                    }
                    if (c == '[') depth++;
                    if (c == ']')
                    {
                        depth--;
                        i++;
                        if (depth == 0) break;
                        continue;
                    }
                    i++;
                }
                return string.Join("|", items.ToArray());
            }
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && !char.IsWhiteSpace(s[i])) i++;
            return s.Substring(start, i - start);
        }

        // ---- typed getters --------------------------------------------------

        public static string Str(Dictionary<string, string> m, string k, string def)
        {
            string v;
            return (m.TryGetValue(k, out v) && v != null) ? v : def;
        }

        public static int Int(Dictionary<string, string> m, string k, int def)
        {
            string v;
            int r;
            if (m.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        public static float Flt(Dictionary<string, string> m, string k, float def)
        {
            string v;
            float r;
            if (m.TryGetValue(k, out v) && float.TryParse(v, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        public static bool Bool(Dictionary<string, string> m, string k, bool def)
        {
            string v;
            if (!m.TryGetValue(k, out v) || v == null) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes") return true;
            if (v == "false" || v == "0" || v == "no") return false;
            return def;
        }

        public static List<string> List(Dictionary<string, string> m, string k, List<string> def)
        {
            string v;
            if (!m.TryGetValue(k, out v) || string.IsNullOrEmpty(v)) return def;
            var parts = new List<string>();
            foreach (string p in v.Split('|'))
                if (!string.IsNullOrWhiteSpace(p)) parts.Add(p.Trim());
            return parts.Count > 0 ? parts : def;
        }
    }
}
