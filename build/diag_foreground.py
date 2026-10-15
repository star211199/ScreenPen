"""Break the foreground lock so real synthetic keystrokes reach the dialog.

A background process cannot SetForegroundWindow (the OS refuses it), so
keybd_event output lands in the console. The documented workaround is to inject
an ALT keypress first: that makes the calling process the last-input owner and
SetForegroundWindow then succeeds. Compare three strategies.
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
VK_MENU, VK_F2, VK_BACK = 0x12, 0x71, 0x08
KEYEVENTF_KEYUP = 2


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


def tap(vk):
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    u.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
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
print("dialog:", s, "| box:", repr(box["text"]), "at", box["x"], box["y"])

print()
print("=== strategy 1: plain SetForegroundWindow ===")
u.SetForegroundWindow(s)
time.sleep(0.6)
print("  foreground == dialog:", u.GetForegroundWindow() == s)
u.SetCursorPos(box["x"] + box["w"] // 2, box["y"] + box["h"] // 2)
time.sleep(0.2)
u.mouse_event(0x0002, 0, 0, 0, 0)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.4)
tap(VK_F2)
time.sleep(0.4)
print("  after F2:", repr(text_of(box["hwnd"])))

print()
print("=== strategy 2: ALT-keypress trick, then SetForegroundWindow ===")
u.keybd_event(VK_MENU, 0, 0, 0)
u.SetForegroundWindow(s)
u.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)
time.sleep(0.7)
fg = u.GetForegroundWindow()
print("  foreground == dialog:", fg == s, "(fg=", fg, ")")
u.SetCursorPos(box["x"] + box["w"] // 2, box["y"] + box["h"] // 2)
time.sleep(0.2)
u.mouse_event(0x0002, 0, 0, 0, 0)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.4)
tap(VK_BACK)
time.sleep(0.3)
print("  after Backspace:", repr(text_of(box["hwnd"])))
tap(VK_F2)
time.sleep(0.5)
print("  after F2       :", repr(text_of(box["hwnd"])))
print("  >>> recorder driven cross-process:",
      text_of(box["hwnd"]).strip() == "F2")

subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.6)
