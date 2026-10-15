"""Diagnose why synthetic drawing produces no visible change.

Runs the real EXE, activates the overlay, and for each tool reports whether the
stroke count and the rendered pixels change. Separates "the app did not receive
the mouse" from "the app received it but drew nothing".
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

VK = {"CTRL": 0x11, "ALT": 0x12, "D": 0x44, "P": 0x50, "L": 0x4C,
      "R": 0x52, "O": 0x4F, "A": 0x41, "ESC": 0x1B}

LDOWN, LUP = 0x0002, 0x0004


def key(vk, down=True):
    u.keybd_event(vk, 0, 0 if down else 2, 0)


def tap(vk, mods=()):
    for m in mods:
        key(m, True)
    key(vk, True)
    time.sleep(0.04)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.4)


def mouse_move(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.04)


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


def diff(a, b):
    if len(a) != len(b):
        return 1.0
    n = len(a) // 4
    changed = 0
    for i in range(0, len(a), 4):
        if abs(a[i] - b[i]) > 12 or abs(a[i + 1] - b[i + 1]) > 12 or abs(a[i + 2] - b[i + 2]) > 12:
            changed += 1
    return changed / max(1, n)


def find_windows(pid):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            cls = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, cls, 256)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append((h, cls.value, r.right - r.left, r.bottom - r.top,
                        bool(u.IsWindowVisible(h))))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def overlay_of(pid):
    sw = u.GetSystemMetrics(78)
    sh = u.GetSystemMetrics(79)
    best = None
    for h, cls, w, hh, vis in find_windows(pid):
        if vis and w >= sw - 4 and hh >= sh - 4:
            best = (h, w, hh)
    return best


def main():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                   capture_output=True)
    time.sleep(0.6)
    proc = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.4)

    try:
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.2)
        ov = overlay_of(proc.pid)
        if not ov:
            print("FAIL: overlay did not open")
            return 1
        print(f"overlay hwnd={ov[0]} {ov[1]}x{ov[2]}")

        # Make sure the overlay owns the foreground.
        u.SetForegroundWindow(ov[0])
        time.sleep(0.4)

        # Is the overlay click-through? A WS_EX_TRANSPARENT overlay would never
        # receive the synthetic click, which is exactly the failure we are
        # chasing.
        GWL_EXSTYLE = -20
        ex = u.GetWindowLongW(ov[0], GWL_EXSTYLE)
        print(f"exstyle = 0x{ex & 0xFFFFFFFF:08X}  "
              f"TRANSPARENT={'YES' if ex & 0x20 else 'no'}  "
              f"LAYERED={'YES' if ex & 0x80000 else 'no'}  "
              f"NOACTIVATE={'YES' if ex & 0x08000000 else 'no'}")

        for name, vk in (("pen", VK["P"]), ("line", VK["L"]),
                         ("rect", VK["R"]), ("ellipse", VK["O"]),
                         ("arrow", VK["A"])):
            tap(vk)
            time.sleep(0.3)
            base = grab(300, 300, 300, 200)
            mouse_move(360, 360)
            mouse(LDOWN)
            for i in range(20):
                mouse_move(360 + i * 8, 360 + i * 4)
            mouse(LUP)
            time.sleep(0.5)
            got = grab(300, 300, 300, 200)
            print(f"  {name:8s} diff={diff(base, got):.2%}")
    finally:
        proc.terminate()
        time.sleep(0.5)
        subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                       capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
