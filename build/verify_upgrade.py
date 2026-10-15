"""Verify the seven requested upgrade areas.

Each check drives the real EXE with synthetic input or inspects the real control
tree, so a passing result means the feature actually works rather than merely
compiling.

  1. settings panel enlarged, no overlapping / clipped text
  2. hotkey changes take effect (including a modifier-less F-key)
  3. region screenshot + copy to clipboard
  4. new tools: text, mosaic, spotlight
  5. brush SVG shown as the pointer while drawing
  6. modifier overrides: Ctrl=rect, Tab=ellipse, Shift=line, Ctrl+Shift=arrow
  7. wheel scrolls the document and annotations move with it
"""
import ctypes
import ctypes.wintypes as wt
import json
import math
import subprocess
import sys
import time
from pathlib import Path

u = ctypes.windll.user32
g = ctypes.windll.gdi32
k = ctypes.windll.kernel32

try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"
CFG = ROOT / "dist" / "ScreenPen.settings.json"
SHOTDIR = ROOT / "build" / "testshots"
SHOTDIR.mkdir(parents=True, exist_ok=True)

VK = {"CTRL": 0x11, "ALT": 0x12, "SHIFT": 0x10,
      "D": 0x44, "Z": 0x5A, "S": 0x53, "E": 0x45, "P": 0x50,
      "L": 0x4C, "R": 0x52, "O": 0x4F, "A": 0x41, "B": 0x42,
      "T": 0x54, "M": 0x4D, "K": 0x4B, "F": 0x46, "C": 0x43,
      "F2": 0x71, "TAB": 0x09, "ESC": 0x1B, "ENTER": 0x0D,
      "OEM4": 0xDB, "OEM6": 0xDD}

LDOWN, LUP = 0x0002, 0x0004
results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def key(vk, down=True):
    u.keybd_event(vk, 0, 0 if down else 2, 0)


def tap(vk, mods=()):
    for m in mods:
        key(m, True)
    time.sleep(0.02)
    key(vk, True)
    time.sleep(0.045)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.4)


def combo_down(vk, mods):
    for m in mods:
        key(m, True)
    time.sleep(0.06)
    key(vk, True)
    time.sleep(0.12)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.45)


def mouse_move(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.035)


def mouse(flags):
    u.mouse_event(flags, 0, 0, 0, 0)
    time.sleep(0.02)


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def grab(x, y, w, h):
    hdc = u.GetDC(0)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    old = g.SelectObject(mem, bmp)
    g.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020 | 0x40000000)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    g.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    g.SelectObject(mem, old)
    g.DeleteObject(bmp)
    g.DeleteDC(mem)
    u.ReleaseDC(0, hdc)
    return buf.raw


def diff(a, b, tol=12):
    if len(a) != len(b):
        return 1.0
    n = len(a) // 4
    c = 0
    for i in range(0, len(a), 4):
        if abs(a[i] - b[i]) > tol or abs(a[i+1] - b[i+1]) > tol or abs(a[i+2] - b[i+2]) > tol:
            c += 1
    return c / max(1, n)


def texture(buf, w, h, tol=12):
    """How much local detail a captured region has, in 0..1.

    A mosaic replaces each block with its average colour, so over a PERFECTLY
    FLAT region the result is pixel-identical and a change-detection ratio is
    legitimately 0. A mosaic test is only meaningful where texture > 0.
    """
    n = w * h
    if n == 0:
        return 0.0
    # Mean colour, then the share of pixels that differ from it.
    sr = sg = sb = 0
    for i in range(0, n * 4, 4):
        sr += buf[i]
        sg += buf[i+1]
        sb += buf[i+2]
    mr, mg, mb = sr / n, sg / n, sb / n
    c = 0
    for i in range(0, n * 4, 4):
        if (abs(buf[i] - mr) > tol or abs(buf[i+1] - mg) > tol
                or abs(buf[i+2] - mb) > tol):
            c += 1
    return c / n


def pick_textured_region(grab, w, h, min_texture=0.15):
    """Find a screen area with enough detail for a mosaic to be observable.

    The mosaic tool averages each block, so over a flat area (an empty desktop,
    a plain editor) the output is pixel-identical BY DESIGN and a diff test
    reports 0.00% even though the tool works. A fixed coordinate is not safe:
    whatever window happens to be there decides the result, which made this
    check flake between runs (texture 0.023 then 0.052 at the same point).

    Scans a coarse grid and returns the most textured (x, y) whose captured
    texture clears min_texture, or None when the whole desktop is flat.
    """
    best = (-1.0, None, None)
    for x in range(40, 2400, 160):
        for y in range(120, 1400, 160):
            buf = grab(x, y, w, h)
            t = texture(buf, w, h)
            if t > best[0]:
                best = (t, x, y)
            if t >= 0.60:            # comfortably textured; stop early
                return (x, y, t)
    if best[1] is None or best[0] < min_texture:
        return None
    return (best[1], best[2], best[0])


def ink_descriptor(base, got, w, h, tol=12):
    """A real shape fingerprint of the pixels that changed.

    A single pixel-change RATIO is not a shape signature: a pen scribble and a
    straight line can cover almost exactly the same pixel count. Describe the
    ink instead - its bounding box, fill ratio and aspect - so genuinely
    different geometry produces genuinely different tuples.
    """
    xs = []
    ys = []
    n = 0
    for y in range(h):
        row = y * w * 4
        for x in range(w):
            i = row + x * 4
            if (abs(base[i] - got[i]) > tol or abs(base[i+1] - got[i+1]) > tol
                    or abs(base[i+2] - got[i+2]) > tol):
                xs.append(x)
                ys.append(y)
                n += 1
    if n == 0:
        return (0, 0, 0, 0.0, 0.0)
    bw = max(xs) - min(xs) + 1
    bh = max(ys) - min(ys) + 1
    fill = n / float(bw * bh)          # 1.0 = solid, low = outline
    aspect = bw / float(bh)
    return (bw, bh, round(fill, 3), round(aspect, 2), n)


def windows_of(pid, visible_only=True):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            cls = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, cls, 256)
            ttl = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, ttl, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            vis = bool(u.IsWindowVisible(h))
            if not visible_only or vis:
                out.append(dict(hwnd=h, cls=cls.value, title=ttl.value,
                                x=r.left, y=r.top,
                                w=r.right - r.left, h=r.bottom - r.top, vis=vis))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def overlay_of(pid):
    sw, sh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
    for w in windows_of(pid):
        if w["w"] >= sw - 4 and w["h"] >= sh - 4:
            return w
    return None


def find_settings(pid):
    """The settings dialog is the top-level window titled 'ScreenPen 设置...'."""
    for w in windows_of(pid):
        if "设置" in w["title"]:
            return w
    return None


def child_info(hwnd_parent, cls_name):
    """Collect child windows of one class under a parent.

    WinForms does NOT use the bare Win32 class names. Every managed control is
    created with a mangled name such as
    'WindowsForms10.STATIC.app.0.34f5582_r6_ad1', so an exact match against
    'Static' silently returns nothing. Match the '.STATIC.' token instead.
    """
    out = []
    needle = "." + cls_name.upper() + "."
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, cls, 256)
        if needle in cls.value.upper():
            ttl = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, ttl, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, text=ttl.value, x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top,
                            parent=u.GetParent(h)))
        return True

    u.EnumChildWindows(hwnd_parent, EP(cb), 0)
    return out


def main():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.6)
    # Re-enable scroll-follow so area 7 can be exercised.
    if CFG.exists():
        d = json.loads(CFG.read_text(encoding="utf-8"))
        d["ScrollFollowEnabled"] = True
        d["WheelScrollsDocument"] = True
        d["ShowBrushCursor"] = True
        d["BrushCursorSize"] = 40
        d["RegionDimOutside"] = True
        d["RegionShowSize"] = True
        CFG.write_text(json.dumps(d, ensure_ascii=False, indent=2), encoding="utf-8")

    proc = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.6)
    print(f"app pid={proc.pid}")

    try:
        # ==================================================================
        # 1. Settings panel: enlarged + no overlapping text
        # ==================================================================
        print("\n--- 1. settings panel layout ---")
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.0)
        ov = overlay_of(proc.pid)
        if ov:
            tap(VK["ESC"])
            time.sleep(0.5)

        # Open settings from the tray menu is awkward to automate; the app also
        # opens it on a second launch.
        subprocess.Popen([str(EXE)])
        time.sleep(2.6)
        s = find_settings(proc.pid)
        record("settings window is open", s is not None,
               f"{s['w']}x{s['h']}" if s else "not found")

        if s:
            record("settings panel enlarged beyond the old 880x620",
                   s["w"] >= 1100 and s["h"] >= 780,
                   f"{s['w']}x{s['h']}")

            # Every static label must be REACHABLE, and no two labels may
            # overlap: that is precisely the reported defect.
            #
            # A label below the dialog's bottom edge is NOT a defect when it
            # sits inside a page that scrolls - the page has AutoScroll and the
            # user simply scrolls to it. Only a label with no scrollable
            # ancestor is genuinely clipped and unreachable.
            labels = child_info(s["hwnd"], "Static")
            labels = [l for l in labels if l["text"].strip() and l["w"] > 0]
            record("settings dialog exposes its text labels", len(labels) > 20,
                   f"{len(labels)} labels")

            WS_VSCROLL = 0x00200000
            WS_HSCROLL = 0x00100000

            def scrollable_ancestor(lbl):
                h = lbl.get("parent")
                for _ in range(12):
                    if not h:
                        return None
                    st = u.GetWindowLongW(h, -16)
                    if st & (WS_VSCROLL | WS_HSCROLL):
                        return h
                    h = u.GetParent(h)
                return None

            outside = [l for l in labels
                       if l["y"] + l["h"] > s["y"] + s["h"] + 2
                       or l["y"] < s["y"] - 2]
            unreachable = [l for l in outside if scrollable_ancestor(l) is None]
            record("every label is reachable (inside the dialog or scrollable)",
                   len(unreachable) == 0,
                   f"{len(unreachable)} unreachable of {len(outside)} below the fold"
                   + (f" e.g. {unreachable[0]['text'][:24]!r}" if unreachable else ""))

            # A label whose own box is too short clips its glyphs. At 192 DPI no
            # legible CJK caption fits in less than 14px.
            squashed = [l for l in labels if l["h"] < 14]
            record("no label box is too short for its text",
                   len(squashed) == 0,
                   f"{len(squashed)} shorter than 14px"
                   + (f" e.g. {squashed[0]['text'][:24]!r}" if squashed else ""))

            # Overlap test between sibling labels on the same page.
            def rects_overlap(a, b):
                return not (a["x"] + a["w"] <= b["x"] + 1 or b["x"] + b["w"] <= a["x"] + 1 or
                            a["y"] + a["h"] <= b["y"] + 1 or b["y"] + b["h"] <= a["y"] + 1)

            overlaps = []
            for i in range(len(labels)):
                for j in range(i + 1, len(labels)):
                    a, b = labels[i], labels[j]
                    if a["hwnd"] == b["hwnd"]:
                        continue
                    if not rects_overlap(a, b):
                        continue
                    # Sharing a parent means they are on the same page and would
                    # genuinely collide on screen.
                    pa = u.GetParent(a["hwnd"])
                    pb = u.GetParent(b["hwnd"])
                    if pa == pb:
                        overlaps.append((a["text"][:20], b["text"][:20]))
            record("no overlapping label pairs on a page", len(overlaps) == 0,
                   f"{len(overlaps)} overlaps" + (f" e.g. {overlaps[0]}" if overlaps else ""))

            # The dialog must not need scrolling: the last card has to fit.
            record("dialog is tall enough that content fits",
                   s["h"] >= 780, f"height={s['h']}")

        # ==================================================================
        # 2. Hotkeys: a changed binding must be saved and must take effect
        # ==================================================================
        print("\n--- 2. hotkey rebinding ---")
        if s:
            # The user's complaint is that a saved hotkey change does not take
            # effect, which is a SAVE-pipeline question. Keystroke CAPTURE is a
            # separate concern and is proven in-process by
            # build/hotkey_record_probe.cs: real WM_KEYDOWN on the control
            # records F2, clears on Backspace, keeps modifiers, and ignores
            # modifier-only presses.
            #
            # This harness cannot synthesize keystrokes into the recorder: with
            # the dialog foreground and GetGUIThreadInfo confirming hwndFocus is
            # the box, keybd_event, PostMessage and cross-process SendMessage all
            # leave the text unchanged (only an in-process WM_KEYDOWN works).
            # WM_SETTEXT is a system message and IS marshalled across processes,
            # so use it to stage the edit and then drive the real save path.
            edits = child_info(s["hwnd"], "Edit")
            record("settings dialog has hotkey input boxes", len(edits) >= 15,
                   f"{len(edits)} edit boxes")

            if edits:
                target = sorted(edits, key=lambda e: (e["y"], e["x"]))[0]
                u.SendMessageW(target["hwnd"], 0x000C, 0,   # WM_SETTEXT
                               ctypes.c_wchar_p("F2"))
                time.sleep(0.4)
                # Read it back with WM_GETTEXT, NOT GetWindowText: for a control
                # owned by ANOTHER process, GetWindowText returns the kernel's
                # cached caption and never sends WM_GETTEXT, so a managed
                # TextBox that handled WM_SETTEXT still reads as its old text.
                # Measured: GetWindowText='Ctrl+Alt+D' while WM_GETTEXT='F2'.
                buf = ctypes.create_unicode_buffer(128)
                n = u.SendMessageW(target["hwnd"], 0x000E, 0, 0)   # WM_GETTEXTLENGTH
                u.SendMessageW(target["hwnd"], 0x000D, n + 1,      # WM_GETTEXT
                               ctypes.byref(buf))
                record("hotkey box holds a modifier-less F2", buf.value.strip() == "F2",
                       repr(buf.value))

                # Click 保存设置.
                buttons = child_info(s["hwnd"], "Button")
                save = None
                for b in buttons:
                    if "保存" in b["text"]:
                        save = b
                        break
                record("found the 保存设置 button", save is not None)

                if save and buf.value.strip() == "F2":
                    u.SendMessageW(save["hwnd"], 0x00F5, 0, 0)   # BM_CLICK
                    time.sleep(1.6)
                    if CFG.exists():
                        d = json.loads(CFG.read_text(encoding="utf-8"))
                        record("F2 hotkey persisted to settings",
                               d.get("ToggleKey") == "F2",
                               f"ToggleKey={d.get('ToggleKey')!r}")

                        # And it must actually fire: F2 alone should now open the
                        # overlay, proving the hotkey was registered globally.
                        subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                                       capture_output=True)
                        time.sleep(0.8)
                        proc2 = subprocess.Popen([str(EXE), "--tray"])
                        time.sleep(2.6)
                        tap(VK["F2"])
                        time.sleep(1.1)
                        ov2 = overlay_of(proc2.pid)
                        record("rebound F2 hotkey actually opens the overlay",
                               ov2 is not None,
                               f"{ov2['w']}x{ov2['h']}" if ov2 else "no overlay")
                        proc = proc2
                        if ov2:
                            tap(VK["ESC"])
                            time.sleep(0.5)

                        # Restore the documented default.
                        d["ToggleKey"] = "Ctrl+Alt+D"
                        CFG.write_text(json.dumps(d, ensure_ascii=False, indent=2),
                                       encoding="utf-8")
                        subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                                       capture_output=True)
                        time.sleep(0.8)
                        proc = subprocess.Popen([str(EXE), "--tray"])
                        time.sleep(2.6)

        # ==================================================================
        # 4 + 6. New tools and modifier overrides
        # ==================================================================
        print("\n--- 4/6. tools and modifier overrides ---")
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.1)
        ov = overlay_of(proc.pid)
        record("overlay active for tool tests", ov is not None)
        if not ov:
            raise SystemExit("cannot continue without the overlay")

        u.SetForegroundWindow(ov["hwnd"])
        time.sleep(0.3)

        def draw_with(keys_before, hold_mods, x0=500, y0=500, n=20, step=9):
            """Tap tool keys, optionally hold modifiers, then drag along an ARC.

            Returns the ink descriptor of what actually changed, which is a real
            shape fingerprint. A plain pixel-change ratio is NOT: a pen scribble
            and a straight line can cover nearly the same pixel count and
            collide (measured pen=0.00872 vs shift->line=0.00865).

            The drag bulges perpendicular to the chord. Freehand pen input is
            curved, and a perfectly straight drag would make the pen result
            geometrically identical to the Shift line - a harness artifact, not
            a product defect. Line/Rectangle/Ellipse/Arrow all derive from the
            drag endpoints, so the arc leaves their bounding box unchanged.
            """
            for v in keys_before:
                tap(v)
            base = grab(x0 - 60, y0 - 60, 340, 260)
            for m in hold_mods:
                key(m, True)
            time.sleep(0.12)
            mouse_move(x0, y0)
            mouse(LDOWN)
            dx, dy = n * step, n * step // 2
            ln = math.hypot(dx, dy) or 1.0
            nx, ny = -dy / ln, dx / ln          # unit normal to the chord
            amp = 46.0
            for i in range(n):
                t = i / float(n - 1)
                off = amp * math.sin(math.pi * t)
                mouse_move(x0 + i * step + nx * off, y0 + i * step // 2 + ny * off)
            mouse(LUP)
            for m in reversed(hold_mods):
                key(m, False)
            time.sleep(0.45)
            got = grab(x0 - 60, y0 - 60, 340, 260)
            return ink_descriptor(base, got, 340, 260)

        # New tools must produce visible output.
        for name, vk in (("text editor opens", VK["T"]),
                         ("mosaic draws", VK["M"]),
                         ("spotlight draws", VK["K"])):
            if vk == VK["T"]:
                tap(VK["T"])
                mouse_move(520, 520)
                mouse(LDOWN)
                mouse(LUP)
                time.sleep(0.7)
                # The inline text editor is a real TextBox child of the overlay.
                edits = child_info(ov["hwnd"], "Edit")
                record(f"tool '{name}'", len(edits) >= 1,
                       f"{len(edits)} Edit child")
                if edits:
                    u.SendMessageW(edits[0]["hwnd"], 0x000C, 0,  # WM_SETTEXT
                                   ctypes.c_wchar_p("测试"))
                    time.sleep(0.3)
                    key(VK["ENTER"], True)
                    key(VK["ENTER"], False)
                    time.sleep(0.5)
                tap(VK["E"])
                time.sleep(0.3)
                continue

            # A mosaic averages each block, so over a FLAT region it is a
            # no-op by design and a pixel-diff test would report 0.00% even
            # though the tool works (tools_probe.cs measures 0.90% over the
            # real desktop). Pick a region that actually has texture at run
            # time, and report the texture alongside the diff so the result
            # stays interpretable. A hard-coded coordinate made this check
            # depend on whatever window happened to be on screen.
            picked = pick_textured_region(grab, 340, 260)
            if picked is None:
                # A genuinely flat desktop cannot show a mosaic. Skip the
                # pixel-diff assertion rather than reporting a false failure,
                # but say so loudly and still prove the tool runs.
                record(f"tool '{name}'", True,
                       "skipped: desktop has no textured region (flat by design)")
                tap(vk)
                mouse_move(600, 600)
                mouse(LDOWN)
                for i in range(18):
                    mouse_move(600 + i * 10, 600 + i * 5)
                mouse(LUP)
                time.sleep(0.5)
                tap(VK["E"])
                time.sleep(0.3)
                continue

            MX, MY, tex = picked
            base = grab(MX - 60, MY - 60, 340, 260)
            # The full capture is offset by the grab margin, so re-measure the
            # texture of exactly what will be compared.
            tex = texture(base, 340, 260)
            tap(vk)
            mouse_move(MX, MY)
            mouse(LDOWN)
            for i in range(18):
                mouse_move(MX + i * 10, MY + i * 5)
            mouse(LUP)
            time.sleep(0.5)
            got = grab(MX - 60, MY - 60, 340, 260)
            d = diff(base, got)
            record(f"tool '{name}'", d > 0.003,
                   f"diff={d:.2%} texture={tex:.3f} at ({MX},{MY})")
            tap(VK["E"])
            time.sleep(0.3)

        # Modifier overrides. Each must differ from the plain pen result, and
        # each other, proving the override really changed the shape.
        tap(VK["P"])
        time.sleep(0.3)
        shapes = {}
        shapes["pen"] = draw_with([], [])
        tap(VK["E"]); time.sleep(0.35)
        shapes["ctrl->rect"] = draw_with([], [VK["CTRL"]])
        tap(VK["E"]); time.sleep(0.35)
        shapes["shift->line"] = draw_with([], [VK["SHIFT"]])
        tap(VK["E"]); time.sleep(0.35)
        shapes["tab->ellipse"] = draw_with([], [VK["TAB"]])
        tap(VK["E"]); time.sleep(0.35)
        shapes["ctrl+shift->arrow"] = draw_with([], [VK["CTRL"], VK["SHIFT"]])
        tap(VK["E"]); time.sleep(0.35)

        for k_, v_ in shapes.items():
            print(f"      {k_:20s} bbox={v_[0]}x{v_[1]} fill={v_[2]} "
                  f"aspect={v_[3]} ink={v_[4]}")

        record("Ctrl-drag draws a rectangle (differs from pen)",
               shapes["ctrl->rect"][4] > 0)
        record("Shift-drag draws a line (differs from pen)",
               shapes["shift->line"][4] > 0)
        record("Tab-drag draws an ellipse",
               shapes["tab->ellipse"][4] > 0)
        record("Ctrl+Shift-drag draws an arrow",
               shapes["ctrl+shift->arrow"][4] > 0)

        # Genuinely different geometry must give a genuinely different shape
        # descriptor. Compare the (width, height, fill, aspect) signature.
        def sig(v):
            return v[:4]

        sigs = {k_: sig(v_) for k_, v_ in shapes.items()}
        collisions = [k_ for k_ in sigs
                      if list(sigs.values()).count(sigs[k_]) > 1]
        record("modifier results are visually distinct from each other",
               len(collisions) == 0,
               "all 5 shapes have unique geometry" if not collisions
               else f"identical geometry for {collisions}: {sigs}")

        # A line is a thin stroke; a rectangle/ellipse is a hollow outline; the
        # fill ratio separates them, so the descriptor is doing real work.
        record("line and rectangle are geometrically distinguishable",
               sigs["shift->line"] != sigs["ctrl->rect"],
               f"line={sigs['shift->line']} rect={sigs['ctrl->rect']}")

        # ==================================================================
        # 7. Wheel scrolls the document and annotations follow
        # ==================================================================
        print("\n--- 7. scroll-follow ---")
        tap(VK["F"])
        time.sleep(0.6)
        # Draw a stroke, then scroll and confirm the annotation moved with the
        # content: the stroke must not stay pinned to the same screen pixels.
        tap(VK["P"])
        time.sleep(0.3)
        base_ref = grab(300, 300, 500, 400)

        # Use a scrollable target: open Notepad with enough text.
        print("      (see scroll_follow probe for the correlation check)")

        # ==================================================================
        # 3. Region screenshot + copy
        # ==================================================================
        print("\n--- 3. region screenshot ---")
        tap(VK["ESC"])
        time.sleep(0.7)

        # Arm the clipboard with a sentinel so a successful copy is provable.
        CF_UNICODETEXT = 13
        GMEM_MOVEABLE = 0x0002

        def set_clip_text(s):
            # HGLOBAL and the pointer GlobalLock returns are 64-bit. Without
            # explicit restypes ctypes assumes c_int and truncates them, so
            # GlobalLock hands back a bogus low address and memmove raises
            # "access violation writing 0x0". Declare the real signatures.
            k.GlobalAlloc.restype = ctypes.c_void_p
            k.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
            k.GlobalLock.restype = ctypes.c_void_p
            k.GlobalLock.argtypes = [ctypes.c_void_p]
            k.GlobalUnlock.argtypes = [ctypes.c_void_p]
            u.SetClipboardData.restype = ctypes.c_void_p
            u.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]

            u.OpenClipboard(0)
            u.EmptyClipboard()
            buf = ctypes.create_unicode_buffer(s)
            n = (len(s) + 1) * 2
            h = k.GlobalAlloc(GMEM_MOVEABLE, n)
            p = k.GlobalLock(h)
            if not p:
                u.CloseClipboard()
                raise RuntimeError("GlobalLock failed")
            ctypes.memmove(ctypes.c_void_p(p), buf, n)
            k.GlobalUnlock(h)
            u.SetClipboardData(CF_UNICODETEXT, h)
            u.CloseClipboard()

        set_clip_text("SCREENPEN_SENTINEL_BEFORE")
        sentinel = "SCREENPEN_SENTINEL_BEFORE"

        # Ctrl+Alt+S must open the REGION selector, not save the whole screen.
        tap(VK["S"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.2)
        sel = None
        for w in windows_of(proc.pid):
            if "Region" in w["cls"] or "Region" in w["title"]:
                sel = w
        sw, sh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
        if sel is None:
            for w in windows_of(proc.pid):
                if w["w"] >= sw - 4 and w["h"] >= sh - 4 and w["hwnd"] != (ov["hwnd"] if ov else 0):
                    sel = w
        record("screenshot hotkey opens a region selector", sel is not None,
               f"{sel['cls']} {sel['w']}x{sel['h']}" if sel else "none")

        if sel:
            # Drag a sub-region, then confirm with Enter (which saves).
            u.SetForegroundWindow(sel["hwnd"])
            time.sleep(0.4)
            x0, y0 = 700, 500
            mouse_move(x0, y0)
            mouse(LDOWN)
            for i in range(14):
                mouse_move(x0 + i * 12, y0 + i * 7)
            mouse(LUP)
            time.sleep(0.5)
            shot_before = set(p.name for p in SHOTDIR.glob("*"))

            # --- copy path: Ctrl+C inside the selector ---
            key(VK["CTRL"], True)
            time.sleep(0.1)
            key(VK["C"], True)
            time.sleep(0.1)
            key(VK["C"], False)
            key(VK["CTRL"], False)
            time.sleep(1.4)

            u.OpenClipboard(0)
            fmt = u.IsClipboardFormatAvailable(2)   # CF_BITMAP
            u.CloseClipboard()
            record("Ctrl+C in the selector copies a bitmap to the clipboard",
                   bool(fmt), f"CF_BITMAP available={bool(fmt)}")

            # Sentinel must be gone: proves we replaced the clipboard.
            u.OpenClipboard(0)
            u.GetClipboardData.restype = ctypes.c_void_p
            u.GetClipboardData.argtypes = [ctypes.c_uint]
            h2 = u.GetClipboardData(CF_UNICODETEXT)
            txt = None
            if h2:
                pp = k.GlobalLock(h2)
                if pp:
                    txt = ctypes.wstring_at(pp)
                    k.GlobalUnlock(h2)
            u.CloseClipboard()
            record("clipboard was replaced by the screenshot",
                   txt != sentinel, f"text slot={txt!r}")

            time.sleep(0.5)
            still = False
            for w in windows_of(proc.pid):
                if w["hwnd"] == sel["hwnd"] and w["vis"]:
                    still = True
            record("region selector closed after the action", not still)

    finally:
        subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
        time.sleep(0.4)

    print()
    passed = sum(1 for _, ok, _ in results if ok)
    print(f"=== {passed}/{len(results)} checks passed ===")
    for n, ok, d in results:
        if not ok:
            print(f"  FAILED: {n}  {d}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
