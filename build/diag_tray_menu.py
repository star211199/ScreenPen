"""Can we observe the tray context menu after right-clicking the icon rect?"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import time

u = ctypes.windll.user32
shell = ctypes.windll.shell32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
ROOT = r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen"
WM_RBUTTONDOWN, WM_RBUTTONUP = 0x0204, 0x0205


class NII(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("hWnd", wt.HWND),
                ("uID", wt.UINT), ("guidItem", ctypes.c_byte * 16)]


shell.Shell_NotifyIconGetRect.restype = ctypes.c_long
shell.Shell_NotifyIconGetRect.argtypes = [ctypes.POINTER(NII), ctypes.POINTER(wt.RECT)]


def wins(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            t = ctypes.create_unicode_buffer(256)
            u.GetWindowTextW(h, t, 256)
            out.append((h, c.value, t.value, bool(u.IsWindowVisible(h))))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.8)
p = subprocess.Popen([ROOT + r"\dist\ScreenPen.exe", "--tray"])
time.sleep(4.0)

h_icon = None
for h, c, t, v in wins(p.pid):
    nii = NII()
    nii.cbSize = ctypes.sizeof(NII)
    nii.hWnd = h
    nii.uID = 1
    r = wt.RECT()
    if shell.Shell_NotifyIconGetRect(ctypes.byref(nii), ctypes.byref(r)) == 0:
        h_icon = (h, r)
print("icon:", h_icon[1].left, h_icon[1].top) if h_icon else print("no icon")

if h_icon:
    h, r = h_icon
    cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    before = {x[0] for x in wins(p.pid)}
    u.SetCursorPos(cx, cy)
    time.sleep(0.3)
    u.mouse_event(0x0008, 0, 0, 0, 0)   # RIGHTDOWN
    time.sleep(0.15)
    u.mouse_event(0x0010, 0, 0, 0, 0)   # RIGHTUP
    time.sleep(1.5)
    print("\nwindows after right-click:")
    for x in wins(p.pid):
        mark = "NEW" if x[0] not in before else "   "
        print(f"  {mark} hwnd={x[0]} cls={x[1]!r} text={x[2]!r} vis={x[3]}")
    # Any popup menu class?
    for x in wins(p.pid):
        if "#32768" in x[1] or "Menu" in x[1]:
            print(f"  >>> MENU WINDOW: {x}")
    u.keybd_event(0x1B, 0, 0, 0)
    u.keybd_event(0x1B, 0, 2, 0)
    time.sleep(0.6)

p.terminate()
time.sleep(0.5)
