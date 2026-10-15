"""Prove/disprove the mosaic and hotkey-focus failures with real evidence.

B) The old check used GetFocus(), which only reports the focused window of the
   CALLING thread's message queue - from an external process it always returns 0.
   Use GetGUIThreadInfo on the dialog's own thread instead.

C) Mosaic replaces a block with its average colour. Over a FLAT region that is
   pixel-identical, so diff=0.00% is the correct output, not a defect. Measure
   the backdrop's texture first, then retest mosaic over a textured region.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import ImageGrab

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
VK = {"CTRL": 0x11, "SHIFT": 0x10, "TAB": 0x09, "ESC": 0x1B, "E": 0x45,
      "M": 0x4D, "P": 0x50, "K": 0x4B, "T": 0x54, "F2": 0x71}


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hwndActive", wt.HWND), ("hwndFocus", wt.HWND),
                ("hwndCapture", wt.HWND), ("hwndMenuOwner", wt.HWND),
                ("hwndMoveSize", wt.HWND), ("hwndCaret", wt.HWND),
                ("rcCaret", wt.RECT)]


def wins(pid=None):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if pid is None or p.value == pid:
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, pid=p.value, cls=c.value, text=t.value,
                            vis=bool(u.IsWindowVisible(h)),
                            x=r.left, y=r.top, w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def children(parent, tok):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if ("." + tok.upper() + ".") in c.value.upper():
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, text=t.value, x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)


def tap(vk):
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.03)
    u.keybd_event(vk, 0, 2, 0)
    time.sleep(0.12)


def grab(x, y, w, h):
    return np.asarray(ImageGrab.grab(bbox=(x, y, x + w, y + h)).convert("RGB"),
                      dtype=np.int16)


def diff(a, b):
    return float((np.abs(a - b).sum(axis=2) > 24).mean())


def texture(x, y, w, h):
    """Fraction of pixels that differ from the region mean: 0 == perfectly flat."""
    a = grab(x, y, w, h)
    m = a.reshape(-1, 3).mean(axis=0)
    return float((np.abs(a - m).sum(axis=2) > 24).mean())


print("=" * 72)
print("B) hotkey box focus, measured on the dialog's own thread")
print("=" * 72)
kill()
p1 = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)
subprocess.Popen([str(EXE)])
time.sleep(3.2)

s = None
for w in wins(p1.pid):
    if "设置" in w["text"] and w["vis"]:
        s = w
edits = [e for e in children(s["hwnd"], "EDIT") if e["w"] > 0]
edits.sort(key=lambda e: (e["y"], e["x"]))
target = edits[0]
tid = u.GetWindowThreadProcessId(s["hwnd"], None)
print("dialog thread id:", tid)
print("target box:", target["x"], target["y"], target["w"], "x", target["h"],
      repr(target["text"]))

u.SetForegroundWindow(s["hwnd"])
time.sleep(0.4)
u.SetCursorPos(target["x"] + target["w"] // 2, target["y"] + target["h"] // 2)
time.sleep(0.2)
u.mouse_event(0x0002, 0, 0, 0, 0)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.5)

gi = GUITHREADINFO()
gi.cbSize = ctypes.sizeof(GUITHREADINFO)
ok = u.GetGUIThreadInfo(tid, ctypes.byref(gi))
print("GetGUIThreadInfo ok:", bool(ok))
print("  hwndFocus =", gi.hwndFocus, "| target =", target["hwnd"],
      "| MATCH =", gi.hwndFocus == target["hwnd"])
b = ctypes.create_unicode_buffer(128)
u.GetWindowTextW(target["hwnd"], b, 128)
print("  box text right after focus:", repr(b.value))

tap(VK["F2"])
time.sleep(0.5)
u.GetWindowTextW(target["hwnd"], b, 128)
print("  box text after typing F2 :", repr(b.value))
print("  >>> F2 captured:", b.value.strip() == "F2")

kill()

print()
print("=" * 72)
print("C) mosaic over a flat vs a textured backdrop")
print("=" * 72)
# Region the E2E test samples.
print("backdrop texture at the E2E sample region (440,440,340,260):",
      round(texture(440, 440, 340, 260), 4))
print("backdrop texture at the E2E drag region   (500,500,340,260):",
      round(texture(500, 500, 340, 260), 4))

kill()
p = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)
subprocess.Popen([str(EXE)])
time.sleep(3.0)
for w in wins(p.pid):
    if "设置" in w["text"] and w["vis"]:
        u.SetForegroundWindow(w["hwnd"])
time.sleep(0.8)

ov = None
for w in wins(p.pid):
    if w["cls"].startswith("WindowsForms10.Window.8") and w["w"] > 2000:
        ov = w
if ov is None:
    # open the overlay with the toggle hotkey
    tap(VK["CTRL"])
    time.sleep(0.2)
    for w in wins(p.pid):
        if "设置" in w["text"] and w["vis"]:
            u.SetForegroundWindow(w["hwnd"])
    time.sleep(0.5)
    u.keybd_event(VK["CTRL"], 0, 0, 0)
    u.keybd_event(0x12, 0, 0, 0)   # ALT
    u.keybd_event(0x44, 0, 0, 0)   # D
    u.keybd_event(0x44, 0, 2, 0)
    u.keybd_event(0x12, 0, 2, 0)
    u.keybd_event(VK["CTRL"], 0, 2, 0)
    time.sleep(1.2)
    for w in wins(p.pid):
        if w["cls"].startswith("WindowsForms10.Window.8") and w["w"] > 2000:
            ov = w
print("overlay:", (ov["w"], ov["h"]) if ov else None)

if ov:
    u.SetForegroundWindow(ov["hwnd"])
    time.sleep(0.4)
    # Draw mosaic over a TEXTURED strip (the dialog area we just had open is
    # gone, so use the taskbar, which always has text and icons).
    for label, (x0, y0) in (("flat desktop (500,500)", (500, 500)),
                            ("taskbar   (600,1730)", (600, 1730))):
        base = grab(x0 - 60, y0 - 60, 340, 200)
        t = texture(x0 - 60, y0 - 60, 340, 200)
        tap(VK["M"])
        time.sleep(0.25)
        u.SetCursorPos(x0, y0)
        time.sleep(0.15)
        u.mouse_event(0x0002, 0, 0, 0, 0)
        for i in range(20):
            u.SetCursorPos(x0 + i * 9, y0 + i * 4)
            time.sleep(0.01)
        u.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.6)
        got = grab(x0 - 60, y0 - 60, 340, 200)
        print(f"  {label}: backdrop texture={t:.3f}  mosaic diff={diff(base, got):.3%}")
        tap(VK["E"])
        time.sleep(0.4)

kill()
