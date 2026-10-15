"""Dump the settings dialog's real control tree so tests can address controls
by class and text instead of guessing."""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from collections import Counter
from pathlib import Path

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"


def children(parent):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        t = ctypes.create_unicode_buffer(1024)
        u.GetWindowTextW(h, t, 1024)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        out.append(dict(hwnd=h, cls=c.value, text=t.value,
                        x=r.left, y=r.top, w=r.right - r.left, h=r.bottom - r.top,
                        vis=bool(u.IsWindowVisible(h))))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def main():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)
    proc = subprocess.Popen([str(EXE)])
    time.sleep(4.5)

    found = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def top(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == proc.pid and u.IsWindowVisible(h):
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            if "设置" in t.value:
                found.append(h)
        return True

    # The dialog can take a few seconds to appear; poll rather than guess.
    for attempt in range(30):
        del found[:]
        u.EnumWindows(EP(top), 0)
        if found:
            break
        time.sleep(0.5)

    dlg = found[0] if found else None
    if not dlg:
        print("settings dialog not found; visible top-level windows:")
        def allw(h, l):
            p = wt.DWORD()
            u.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == proc.pid and u.IsWindowVisible(h):
                c = ctypes.create_unicode_buffer(256)
                u.GetClassNameW(h, c, 256)
                t = ctypes.create_unicode_buffer(512)
                u.GetWindowTextW(h, t, 512)
                r = wt.RECT()
                u.GetWindowRect(h, ctypes.byref(r))
                print(f"  {c.value:<30} {r.right-r.left}x{r.bottom-r.top} "
                      f"title={t.value[:50]!r}")
            return True
        u.EnumWindows(EP(allw), 0)
        return 1
    print(f"dialog hwnd={dlg}")

    kids = children(dlg)
    print(f"\ntotal child windows = {len(kids)}")
    print("\nclass histogram:")
    for cls, n in Counter(k["cls"] for k in kids).most_common():
        print(f"  {n:4d}  {cls}")

    # The settings form is WinForms, so every control is a real HWND, but the
    # custom HotkeyBox may report as a generic class. Show every distinct class
    # along with a sample of its text.
    print("\nsample of each class (first 6 by y):")
    for cls in sorted(set(k["cls"] for k in kids)):
        grp = sorted([k for k in kids if k["cls"] == cls], key=lambda e: (e["y"], e["x"]))
        print(f"\n  --- {cls}  ({len(grp)} total) ---")
        for k in grp[:6]:
            print(f"      hwnd={k['hwnd']:>10} ({k['x']:>5},{k['y']:>5}) "
                  f"{k['w']:>4}x{k['h']:<4} vis={k['vis']} text={k['text'][:40]!r}")

    # Anything that looks like a hotkey readout.
    print("\nwindows whose text looks like a hotkey:")
    for k in kids:
        t = k["text"].strip()
        if t and ("Ctrl" in t or t in ("P", "L", "R", "O", "A", "T", "M", "K", "F", "E")
                  or "未设置" in t or "可用" in t):
            print(f"  {k['cls']:<20} ({k['x']:>5},{k['y']:>5}) {k['w']:>4}x{k['h']:<4} {t[:50]!r}")

    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
