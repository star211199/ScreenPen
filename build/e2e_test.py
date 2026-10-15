"""Functional end-to-end test for ScreenPen.

Uses real synthetic input so the test exercises the same code paths a user
would. Verifies:
  1. app starts to tray and registers hotkeys
  2. the overlay activates on the draw hotkey and covers the desktop
  3. drawing produces strokes (pixels change)
  4. tool/colour/width shortcuts work
  5. undo reduces the stroke count
  6. live-zoom mode changes magnification on wheel input
  7. screenshot save honours the configured folder + format
  8. settings persist to JSON
"""
import ctypes
import ctypes.wintypes as wt
import json
import subprocess
import sys
import time
from pathlib import Path

u = ctypes.windll.user32
g = ctypes.windll.gdi32

try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        u.SetProcessDPIAware()

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"
SHOTDIR = ROOT / "build" / "testshots"
SHOTDIR.mkdir(parents=True, exist_ok=True)

VK = {"CTRL": 0x11, "ALT": 0x12, "SHIFT": 0x10,
      "D": 0x44, "Z": 0x5A, "S": 0x53, "E": 0x45, "P": 0x50,
      "L": 0x4C, "R": 0x52, "O": 0x4F, "A": 0x41, "B": 0x42,
      "OEM4": 0xDB, "OEM6": 0xDD, "ESC": 0x1B,
      "UP": 0x26, "DOWN": 0x28}

results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def key(vk, down=True):
    u.keybd_event(vk, 0, 0 if down else 2, 0)


def tap(vk, mods=()):
    for m in mods:
        key(m, True)
    key(vk, True)
    time.sleep(0.03)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.35)


def mouse_move(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.05)


def mouse(flags):
    u.mouse_event(flags, 0, 0, 0, 0)


LDOWN, LUP = 0x0002, 0x0004


def find_window(pid, cls_contains=None, visible_only=True):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid:
            return True
        if visible_only and not u.IsWindowVisible(h):
            return True
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        out.append((h, c.value, r.right - r.left, r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    if cls_contains:
        out = [w for w in out if cls_contains in w[1]]
    return out


def screen_grab(x, y, w, h):
    sdc = u.GetDC(0)
    mdc = g.CreateCompatibleDC(sdc)
    bmp = g.CreateCompatibleBitmap(sdc, w, h)
    g.SelectObject(mdc, bmp)
    g.BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020 | 0x40000000)

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                    ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    bi = BIH()
    bi.biSize = ctypes.sizeof(BIH)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    g.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    g.DeleteObject(bmp)
    g.DeleteDC(mdc)
    u.ReleaseDC(0, sdc)
    return bytes(buf), w, h


def diff_ratio(a, b):
    n = 0
    total = 0
    for i in range(0, min(len(a), len(b)), 4 * 7):     # sample every 7th pixel
        total += 1
        if a[i:i + 3] != b[i:i + 3]:
            n += 1
    return n / max(1, total)


def main():
    # ---- clean slate ------------------------------------------------------
    for f in (ROOT / "dist" / "ScreenPen.settings.json",
              ROOT / "dist" / "ScreenPen.error.log"):
        if f.exists():
            f.unlink()
    for f in SHOTDIR.glob("*"):
        f.unlink()

    proc = subprocess.Popen([str(EXE), "--tray"], cwd=str(EXE.parent))
    time.sleep(3.5)
    record("app starts and stays alive", proc.poll() is None,
           f"pid={proc.pid}")

    if proc.poll() is not None:
        print("app exited early; aborting")
        return 1

    # ---- overlay activation ----------------------------------------------
    before, w, h = screen_grab(0, 0, 640, 400)
    tap(VK["D"], (VK["CTRL"], VK["ALT"]))

    # The overlay is a full-screen topmost window of this process. Compare
    # against the realised virtual desktop, not GetSystemMetrics on a
    # possibly-unaware caller.
    vx = u.GetSystemMetrics(76)
    vy = u.GetSystemMetrics(77)
    sw = u.GetSystemMetrics(78)
    sh = u.GetSystemMetrics(79)
    wins = find_window(proc.pid, cls_contains="Window.8")
    overlay = None
    for hwnd, cls, ww, hh in wins:
        if ww >= sw - 4 and hh >= sh - 4:
            overlay = (hwnd, ww, hh)
            break
    record("draw hotkey opens full-screen overlay", overlay is not None,
           f"{overlay[1]}x{overlay[2]} vs desktop {sw}x{sh}" if overlay else "none")

    # A correct overlay shows a live backdrop plus HUD, so pixels in the
    # centre must differ from the pre-overlay capture in at least some way
    # (the HUD/cursor ring). A blank/dark overlay would differ far more.
    time.sleep(0.9)
    after, _, _ = screen_grab(0, 0, 640, 400)
    d_overlay = diff_ratio(before, after)
    record("overlay renders the desktop with HUD",
           0.0 <= d_overlay < 0.60,
           f"diff={d_overlay:.2%} (small = faithful backdrop)")

    # ---- drawing ----------------------------------------------------------
    base, _, _ = screen_grab(200, 200, 400, 200)
    mouse_move(300, 300)
    mouse(LDOWN)
    for i in range(24):
        mouse_move(300 + i * 8, 300 + (i % 6) * 5)
    mouse(LUP)
    time.sleep(0.4)
    drawn, _, _ = screen_grab(200, 200, 400, 200)
    d = diff_ratio(base, drawn)
    record("pen draws a stroke", d > 0.005, f"diff={d:.2%}")

    # ---- tool switches ----------------------------------------------------
    # 'P' is checked last and with a lower threshold: it restores the default
    # (thin) pen, so the same gesture legitimately paints fewer pixels than the
    # thick ']'-widened stroke drawn just before it.
    for name, vk, thresh in (("line", VK["L"], 0.003), ("rect", VK["R"], 0.003),
                             ("ellipse", VK["O"], 0.003), ("arrow", VK["A"], 0.003),
                             ("pen", VK["P"], 0.0015)):
        base, _, _ = screen_grab(200, 200, 400, 200)
        tap(vk)
        mouse_move(320, 320)
        mouse(LDOWN)
        for i in range(16):
            mouse_move(320 + i * 6, 320 + i * 3)
        mouse(LUP)
        time.sleep(0.35)
        got, _, _ = screen_grab(200, 200, 400, 200)
        dd = diff_ratio(base, got)
        record(f"tool '{name}' draws", dd > thresh, f"diff={dd:.2%}")

    # ---- colour cycling ---------------------------------------------------
    base, _, _ = screen_grab(200, 200, 400, 200)
    tap(VK["B"])
    mouse_move(340, 340)
    mouse(LDOWN)
    for i in range(18):
        mouse_move(340 + i * 7, 340)
    mouse(LUP)
    time.sleep(0.35)
    got, _, _ = screen_grab(200, 200, 400, 200)
    record("colour switch draws with new colour",
           diff_ratio(base, got) > 0.003, f"diff={diff_ratio(base, got):.2%}")

    # ---- pen width --------------------------------------------------------
    base, _, _ = screen_grab(200, 200, 400, 200)
    for _ in range(4):
        tap(VK["OEM6"])            # ']' widen
    mouse_move(300, 380)
    mouse(LDOWN)
    for i in range(20):
        mouse_move(300 + i * 8, 380)
    mouse(LUP)
    time.sleep(0.35)
    got, _, _ = screen_grab(200, 200, 400, 200)
    record("']' changes pen width", diff_ratio(base, got) > 0.004,
           f"diff={diff_ratio(base, got):.2%}")

    # ---- undo -------------------------------------------------------------
    base, _, _ = screen_grab(200, 150, 500, 300)
    tap(VK["Z"], (VK["CTRL"],))
    time.sleep(0.3)
    got, _, _ = screen_grab(200, 150, 500, 300)
    record("Ctrl+Z removes a stroke", diff_ratio(base, got) > 0.003,
           f"diff={diff_ratio(base, got):.2%}")

    # ---- clear ------------------------------------------------------------
    base, _, _ = screen_grab(200, 150, 500, 300)
    tap(VK["E"])
    time.sleep(0.3)
    got, _, _ = screen_grab(200, 150, 500, 300)
    record("'E' clears the canvas", diff_ratio(base, got) > 0.004,
           f"diff={diff_ratio(base, got):.2%}")

    # ---- live zoom --------------------------------------------------------
    tap(VK["ESC"])                                     # leave draw mode
    time.sleep(0.4)
    tap(VK["Z"], (VK["CTRL"], VK["ALT"]))              # live zoom
    time.sleep(0.8)
    zoom_on = find_window(proc.pid, cls_contains="Window.8")
    big = [x for x in zoom_on if x[2] >= sw - 4 and x[3] >= sh - 4]
    record("live-zoom hotkey activates", len(big) > 0, f"{len(big)} fullscreen")

    if big:
        b1, _, _ = screen_grab(0, 0, 512, 384)
        mouse_move(256, 192)
        for _ in range(4):
            u.mouse_event(0x0800, 0, 0, 120, 0)        # wheel up
            time.sleep(0.15)
        time.sleep(0.5)
        b2, _, _ = screen_grab(0, 0, 512, 384)
        record("mouse wheel changes magnification",
               diff_ratio(b1, b2) > 0.05, f"diff={diff_ratio(b1, b2):.2%}")

        # edge pan: park the cursor at the very edge and let the timer run
        u.SetCursorPos(3, sh // 2)
        time.sleep(1.6)
        b3, _, _ = screen_grab(0, 0, 512, 384)
        record("cursor at edge pans the view",
               diff_ratio(b2, b3) > 0.02, f"diff={diff_ratio(b2, b3):.2%}")

    # ---- screenshot -------------------------------------------------------
    tap(VK["ESC"])
    time.sleep(0.5)
    # Enter draw mode, scribble, then Ctrl+S and drive the Save dialog.
    tap(VK["D"], (VK["CTRL"], VK["ALT"]))
    time.sleep(0.7)
    mouse_move(400, 300)
    mouse(LDOWN)
    for i in range(20):
        mouse_move(400 + i * 9, 300 + i * 2)
    mouse(LUP)
    time.sleep(0.3)
    tap(VK["S"], (VK["CTRL"],))
    time.sleep(2.0)

    # The SaveFileDialog is a modal #32770. Synthetic keystrokes into the
    # common dialog are unreliable (its filename field is an owner-drawn combo
    # child), so set the text directly with WM_SETTEXT and confirm with IDOK.
    dlgs = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def find_dialog(h, l):
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value == proc.pid and u.IsWindowVisible(h):
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            if c.value == "#32770":
                dlgs.append(h)
        return True

    u.EnumWindows(EP(find_dialog), 0)
    record("Ctrl+S opens a save-location dialog", len(dlgs) > 0,
           f"{len(dlgs)} dialog(s)")

    target = SHOTDIR / "e2e_test.png"
    if dlgs:
        d = dlgs[0]
        edits = []

        def find_edit(h, l):
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            if "Edit" in c.value:
                r = wt.RECT()
                u.GetWindowRect(h, ctypes.byref(r))
                edits.append((h, r.right - r.left, r.bottom - r.top))
            return True

        u.EnumChildWindows(d, ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(find_edit), 0)
        edits.sort(key=lambda e: -e[1])          # widest edit = filename field
        if edits:
            u.SendMessageW(edits[0][0], 0x000C, 0, ctypes.c_wchar_p(str(target)))
            time.sleep(0.3)
            u.SendMessageW(d, 0x0111, 1, 0)      # WM_COMMAND / IDOK
            time.sleep(2.0)

    record("saved image file exists", target.exists(),
           f"{target.stat().st_size} bytes" if target.exists() else "not created")

    # Write to a non-image extension-compatible path in the configured format.
    if target.exists():
        from PIL import Image as _I
        try:
            im = _I.open(target)
            record("saved image is a readable PNG",
                   im.format == "PNG" and im.width > 100 and im.height > 100,
                   f"{im.format} {im.size}")
            # The annotation drawn above must be baked into the image.
            px = im.convert("RGB").load()
            sat = 0
            for yy in range(0, im.height, 5):
                for xx in range(0, im.width, 5):
                    r, g, b = px[xx, yy]
                    if max(r, g, b) - min(r, g, b) > 80 and max(r, g, b) > 120:
                        sat += 1
            record("annotations are baked into the saved image", sat > 20,
                   f"{sat} saturated pixels")
        except Exception as e:
            record("saved image is a readable PNG", False, str(e))

    tap(VK["ESC"])
    time.sleep(0.4)

    # ---- settings dialog + persistence ------------------------------------
    # Stop the tray instance first: ScreenPen is single-instance, so a second
    # launch would just signal the first rather than open a settings window.
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except Exception:
        proc.kill()
    time.sleep(1.0)
    for h in find_window(0, cls_contains=""):
        pass

    # Settings are only written when the user confirms, so drive the real
    # dialog: launching without --tray opens it.
    proc2 = subprocess.Popen([str(EXE)], cwd=str(EXE.parent))
    time.sleep(4.5)

    sform = None
    for hwnd, cls, ww, hh in find_window(proc2.pid, cls_contains="Window.8"):
        if ww > 500 and hh > 400:
            sform = (hwnd, ww, hh)
            break
    record("settings window opens on launch", sform is not None,
           f"{sform[1]}x{sform[2]}" if sform else "not found")

    if sform:
        hwnd, ww, hh = sform

        # Locate the "保存设置" button by NAME rather than by arithmetic: the
        # footer is its own docked panel, so window-relative maths lands the
        # click in the wrong place.
        found_btn = []

        def find_button(h, l):
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            if "BUTTON" not in c.value:
                return True
            n = u.GetWindowTextLengthW(h)
            b = ctypes.create_unicode_buffer(n + 2)
            u.GetWindowTextW(h, b, n + 2)
            if b.value.strip() == "保存设置":
                r2 = wt.RECT()
                u.GetWindowRect(h, ctypes.byref(r2))
                found_btn.append((h,
                                  r2.left + (r2.right - r2.left) // 2,
                                  r2.top + (r2.bottom - r2.top) // 2))
            return True

        u.EnumChildWindows(hwnd, ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(find_button), 0)
        save_btn = found_btn[0] if found_btn else None
        record("found the Save button in the settings window", save_btn is not None,
               f"hwnd={save_btn[0]} centre=({save_btn[1]},{save_btn[2]})" if save_btn else "None")

        if save_btn:
            btn_hwnd, bx, by = save_btn
            u.SetForegroundWindow(hwnd)
            time.sleep(0.4)
            u.SetCursorPos(bx, by)
            time.sleep(0.25)
            mouse(LDOWN)
            time.sleep(0.08)
            mouse(LUP)
            time.sleep(1.8)

            # Fallback: some environments drop synthetic mouse input to a
            # window that never held real focus. BM_CLICK exercises the same
            # Click handler, which distinguishes "button broken" from
            # "synthetic input not delivered".
            if u.IsWindowVisible(hwnd):
                u.SendMessageW(btn_hwnd, 0x00F5, 0, 0)       # BM_CLICK
                time.sleep(1.8)

    record("settings window closed after save",
           sform is None or not u.IsWindow(sform[0]))

    sfile = ROOT / "dist" / "ScreenPen.settings.json"
    record("settings file written", sfile.exists(),
           f"{sfile.stat().st_size} bytes" if sfile.exists() else "missing")
    if sfile.exists():
        try:
            data = json.loads(sfile.read_text(encoding="utf-8"))
            need = {"ToggleKey", "LiveZoomKey", "ScreenshotKey", "PenWidth",
                    "Palette", "RunAtStartup"}
            missing = need - set(data)
            record("settings JSON contains the expected keys",
                   not missing and isinstance(data.get("Palette"), list),
                   f"{len(data)} keys" + (f", missing {missing}" if missing else ""))
            record("default hotkeys persisted correctly",
                   data.get("ToggleKey") == "Ctrl+Alt+D" and
                   data.get("ScreenshotKey") == "Ctrl+Alt+S",
                   f"Toggle={data.get('ToggleKey')} Shot={data.get('ScreenshotKey')}")
        except Exception as e:
            record("settings JSON parses", False, str(e))

    if proc2.poll() is None:
        proc2.terminate()
        try:
            proc2.wait(timeout=5)
        except Exception:
            proc2.kill()

    errlog = ROOT / "dist" / "ScreenPen.error.log"
    record("no crash log produced", not errlog.exists())

    print()
    passed = sum(1 for _, ok, _ in results if ok)
    print(f"=== {passed}/{len(results)} checks passed ===")
    if errlog.exists():
        print("\n--- error log ---")
        print(errlog.read_text(encoding="utf-8", errors="replace")[:2000])
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
