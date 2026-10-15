"""Why does the box read back 'Ctrl+Alt+D' even though the save wrote F2?

GetWindowText cannot retrieve the text of a CONTROL owned by another process: it
returns the kernel-cached caption instead of sending WM_GETTEXT. A WinForms
TextBox handles WM_SETTEXT in managed code, so that kernel cache stays stale,
while Static/Button controls forward to DefWindowProc and look correct.

Read the box with an explicit WM_GETTEXT SendMessage instead.
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
WM_GETTEXT, WM_GETTEXTLENGTH, WM_SETTEXT = 0x000D, 0x000E, 0x000C


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


def get_text_msg(h):
    """Read a control's text the way a cross-process tool must."""
    n = u.SendMessageW(h, WM_GETTEXTLENGTH, 0, 0)
    buf = ctypes.create_unicode_buffer(n + 2)
    u.SendMessageW(h, WM_GETTEXT, n + 1, ctypes.byref(buf))
    return buf.value


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

print("GetWindowTextW     :", repr(box["text"]))
print("WM_GETTEXT         :", repr(get_text_msg(box["hwnd"])))

u.SendMessageW(box["hwnd"], WM_SETTEXT, 0, ctypes.c_wchar_p("F2"))
time.sleep(0.4)
print()
print("after WM_SETTEXT('F2'):")
print("  GetWindowTextW   :", repr(ctypes.create_unicode_buffer(
    u.GetWindowTextW(box["hwnd"], ctypes.create_unicode_buffer(128), 128))))
b = ctypes.create_unicode_buffer(128)
u.GetWindowTextW(box["hwnd"], b, 128)
print("  GetWindowTextW   :", repr(b.value))
print("  WM_GETTEXT       :", repr(get_text_msg(box["hwnd"])))
print()
print(">>> WM_GETTEXT reflects the edit:", get_text_msg(box["hwnd"]) == "F2")

subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.6)
