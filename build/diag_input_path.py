"""Pin down exactly where cross-process keystrokes are lost.

Strategy 2 made the dialog foreground but the box still kept its old value, so
either the click never focused the box, or keybd_event output is not reaching a
foreground dialog. Check focus with GetGUIThreadInfo (GetFocus always returns 0
from another process) and try SendMessage straight to the control.
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
VK_MENU, VK_F2, VK_BACK = 0x12, 0x71, 0x08
UP = 2


class GTI(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hwndActive", wt.HWND), ("hwndFocus", wt.HWND),
                ("hwndCapture", wt.HWND), ("hwndMenuOwner", wt.HWND),
                ("hwndMoveSize", wt.HWND), ("hwndCaret", wt.HWND),
                ("rcCaret", wt.RECT)]


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


def txt(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, b, 256)
    return b.value


def focus_of(dlg):
    tid = u.GetWindowThreadProcessId(dlg, None)
    gi = GTI()
    gi.cbSize = ctypes.sizeof(GTI)
    if not u.GetGUIThreadInfo(tid, ctypes.byref(gi)):
        return 0
    return gi.hwndFocus


def tap(vk):
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    u.keybd_event(vk, 0, UP, 0)
    time.sleep(0.15)


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
edits = [e for e in children(s, "EDIT") if e["w"] > 0]
edits.sort(key=lambda e: (e["y"], e["x"]))
box = edits[0]
print("box:", repr(box["text"]), "hwnd", box["hwnd"])

print()
print("=== 1. force foreground with the ALT trick, then click the box ===")
u.keybd_event(VK_MENU, 0, 0, 0)
u.SetForegroundWindow(s)
u.keybd_event(VK_MENU, 0, UP, 0)
time.sleep(0.7)
print("  foreground==dialog:", u.GetForegroundWindow() == s)
u.SetCursorPos(box["x"] + box["w"] // 2, box["y"] + box["h"] // 2)
time.sleep(0.25)
u.mouse_event(0x0002, 0, 0, 0, 0)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.5)
foc = focus_of(s)
print("  hwndFocus==box:", foc == box["hwnd"], "(focus", foc, ")")

print()
print("=== 2. keybd_event with focus confirmed ===")
tap(VK_F2)
time.sleep(0.5)
print("  after F2:", repr(txt(box["hwnd"])), "| recorded:", txt(box["hwnd"]).strip() == "F2")

print()
print("=== 3. SendMessage straight to the control (no focus, no foreground) ===")
u.SendMessageW(box["hwnd"], WM_KEYDOWN, VK_BACK, 0)
u.SendMessageW(box["hwnd"], WM_KEYUP, VK_BACK, 0)
time.sleep(0.3)
print("  after Backspace:", repr(txt(box["hwnd"])))
u.SendMessageW(box["hwnd"], WM_KEYDOWN, VK_F2, 0)
u.SendMessageW(box["hwnd"], WM_KEYUP, VK_F2, 0)
time.sleep(0.4)
print("  after F2       :", repr(txt(box["hwnd"])),
      "| recorded:", txt(box["hwnd"]).strip() == "F2")

subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.6)
