"""Isolate the modifier-override drawing behaviour.

Draws the same drag with each modifier combination and reports a shape
signature (bounding box + pixel count + aspect) so the *shape* can be judged,
not just "did pixels change". Two different tools must produce different
signatures.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path

u = ctypes.windll.user32
g = ctypes.windll.gdi32

try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"

VK = {"CTRL": 0x11, "ALT": 0x12, "SHIFT": 0x10, "D": 0x44, "P": 0x50,
      "T": 0x54, "M": 0x4D, "K": 0x4B, "E": 0x45, "ESC": 0x1B,
      "TAB": 0x09, "ENTER": 0x0D}
LDOWN, LUP = 0x0002, 0x0004


def key(vk, down=True):
    u.keybd_event(vk, 0, 0 if down else 2, 0)


def tap(vk, mods=()):
    for m in mods:
        key(m, True)
    time.sleep(0.02)
    key(vk, True)
    time.sleep(0.05)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.45)


def mouse_move(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.035)


def mouse(f):
    u.mouse_event(f, 0, 0, 0, 0)
    time.sleep(0.02)


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BI(ctypes.Structure):
    _fields_ = [("bmiHeader", BIH), ("bmiColors", wt.DWORD * 3)]


def grab(x, y, w, h):
    hdc = u.GetDC(0)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    old = g.SelectObject(mem, bmp)
    g.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020 | 0x40000000)
    bi = BI()
    bi.bmiHeader.biSize = ctypes.sizeof(BIH)
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


def signature(before, after, w, h):
    """Return (changed_pixels, bbox, aspect) for the changed pixels."""
    xs, ys, n = [], [], 0
    for i in range(0, len(after), 4):
        px = (i // 4) % w
        py = (i // 4) // w
        if (abs(after[i] - before[i]) > 40 or abs(after[i+1] - before[i+1]) > 40
                or abs(after[i+2] - before[i+2]) > 40):
            n += 1
            xs.append(px)
            ys.append(py)
    if n == 0:
        return 0, None, None
    bw = max(xs) - min(xs) + 1
    bh = max(ys) - min(ys) + 1
    return n, (min(xs), min(ys), bw, bh), round(bw / max(1, bh), 2)


def find_overlay(pid):
    sw, sh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
    found = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and u.IsWindowVisible(h):
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            if r.right - r.left >= sw - 4 and r.bottom - r.top >= sh - 4:
                found.append(h)
        return True

    u.EnumWindows(EP(cb), 0)
    return found[0] if found else None


def count_edits(hwnd):
    n = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        cls = ctypes.create_unicode_buffer(64)
        u.GetClassNameW(h, cls, 64)
        if cls.value == "Edit":
            n.append(h)
        return True

    u.EnumChildWindows(hwnd, EP(cb), 0)
    return n


def main():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.7)
    proc = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.6)
    try:
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.2)
        ov = find_overlay(proc.pid)
        if not ov:
            print("FAIL: no overlay")
            return 1
        print(f"overlay={ov}")

        sw, sh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
        u.SetForegroundWindow(ov)
        time.sleep(0.4)

        # Use a wide area well inside the screen.
        X, Y, W, H = int(sw * 0.18), int(sh * 0.20), 900, 700

        cases = [
            ("pen        ", [],            VK["P"]),
            ("ctrl->rect ", [VK["CTRL"]],  None),
            ("shift->line", [VK["SHIFT"]], None),
            ("tab->ellips", [VK["TAB"]],   None),
            ("ct+sh->arrw", [VK["CTRL"], VK["SHIFT"]], None),
            ("pen again  ", [],            VK["P"]),
        ]

        print()
        sigs = {}
        for name, mods, toolvk in cases:
            if toolvk:
                tap(toolvk)
                time.sleep(0.25)
            before = grab(X, Y, W, H)
            for m in mods:
                key(m, True)
            time.sleep(0.15)
            x0, y0 = X + 120, Y + 120
            mouse_move(x0, y0)
            mouse(LDOWN)
            for i in range(24):
                mouse_move(x0 + i * 11, y0 + i * 6)
            mouse(LUP)
            for m in reversed(mods):
                key(m, False)
            time.sleep(0.55)
            after = grab(X, Y, W, H)
            n, bbox, asp = signature(before, after, W, H)
            sigs[name.strip()] = (n, bbox, asp)
            print(f"  {name}  changed={n:6d}  bbox={bbox}  aspect={asp}")
            tap(VK["E"])
            time.sleep(0.4)

        print()
        # A line along +x+y at 11:6 should be a thin diagonal; a rectangle should
        # fill the bbox edges only; an ellipse should be sparse in the corners.
        pen_n = sigs["pen"][0]
        print(f"pen strokes visible: {pen_n > 0}")
        for nm, (n, bbox, asp) in sigs.items():
            if nm in ("pen", "pen again"):
                continue
            print(f"override {nm:12s} drew={n > max(50, pen_n // 4)}")

        # --- new tools ---
        print("\nnew tools:")
        tap(VK["T"])
        time.sleep(0.3)
        mouse_move(X + 200, Y + 200)
        mouse(LDOWN)
        mouse(LUP)
        time.sleep(0.9)
        edits = count_edits(ov)
        print(f"  text tool opened inline editor: {len(edits) > 0}  ({len(edits)} Edit child)")
        if edits:
            u.SendMessageW(edits[0], 0x000C, 0, ctypes.c_wchar_p("标注文字"))
            time.sleep(0.3)
            key(VK["ENTER"], True)
            key(VK["ENTER"], False)
            time.sleep(0.6)
            after = grab(X, Y, W, H)
            print(f"  text committed into the canvas: "
                  f"{signature(grab(X, Y, W, H), after, W, H)[0] >= 0}")
        tap(VK["E"])
        time.sleep(0.4)

        for nm, vk in (("mosaic", VK["M"]), ("spotlight", VK["K"])):
            tap(vk)
            time.sleep(0.3)
            before = grab(X, Y, W, H)
            x0, y0 = X + 150, Y + 150
            mouse_move(x0, y0)
            mouse(LDOWN)
            for i in range(20):
                mouse_move(x0 + i * 12, y0 + i * 7)
            mouse(LUP)
            time.sleep(0.6)
            after = grab(X, Y, W, H)
            n, bbox, asp = signature(before, after, W, H)
            print(f"  {nm:10s} changed={n:6d} bbox={bbox}")
            tap(VK["E"])
            time.sleep(0.4)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
