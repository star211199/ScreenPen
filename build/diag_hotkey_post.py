"""Can we drive the hotkey recorder cross-process without relying on the
foreground lock?

keybd_event goes to whatever window owns the foreground queue. A background
process cannot reliably SetForegroundWindow (the OS refuses it), so the injected
F2 can land in the console - which is exactly why verify_upgrade.py saw the box
keep its old value even though GetGUIThreadInfo proved it had focus.

Posting WM_KEYDOWN straight to the control's own handle bypasses focus and the
foreground lock entirely and still runs the product's real OnKeyDown.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import time
from pathlib import Path

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
WM_KEYDOWN, WM_KEYUP = 0x0100, 0x0101
VK_F2, VK_BACK, VK_TAB = 0x71, 0x08, 0x09


def wins(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            out.append((h, t.value, bool(u.IsWindowVisible(h))))
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


def text_of(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, b, 256)
    return b.value


subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.8)
p = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)
subprocess.Popen([str(EXE)])
time.sleep(3.2)

s = None
for h, t, v in wins(p.pid):
    if "设置" in t and v:
        s = h
print("dialog hwnd:", s)
print("GetForegroundWindow:", u.GetForegroundWindow(), "| dialog is foreground:",
      u.GetForegroundWindow() == s)

edits = [e for e in children(s, "EDIT") if e["w"] > 0]
edits.sort(key=lambda e: (e["y"], e["x"]))
box = edits[0]
print("box:", box["x"], box["y"], box["w"], "x", box["h"], repr(box["text"]))

print()
print("--- posting WM_KEYDOWN directly to the control (no focus needed) ---")
u.PostMessageW(box["hwnd"], WM_KEYDOWN, VK_F2, 0)
u.PostMessageW(box["hwnd"], WM_KEYUP, VK_F2, 0)
time.sleep(0.5)
print("  after F2      :", repr(text_of(box["hwnd"])))

u.PostMessageW(box["hwnd"], WM_KEYDOWN, VK_BACK, 0)
u.PostMessageW(box["hwnd"], WM_KEYUP, VK_BACK, 0)
time.sleep(0.4)
print("  after Backspace:", repr(text_of(box["hwnd"])))

u.PostMessageW(box["hwnd"], WM_KEYDOWN, VK_TAB, 0)
u.PostMessageW(box["hwnd"], WM_KEYUP, VK_TAB, 0)
time.sleep(0.4)
print("  after Tab     :", repr(text_of(box["hwnd"])))

ok = text_of(box["hwnd"]).strip() == "Tab"
print()
print(">>> cross-process WM_KEYDOWN drives the recorder:", ok)

subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.6)
