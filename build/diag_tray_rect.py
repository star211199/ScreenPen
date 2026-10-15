"""Verify the tray icon via Shell_NotifyIconGetRect, which works on Win11 where
the classic ToolbarWindow32 notification area no longer exists.

If the icon is registered the shell returns its screen rect; if not, it fails.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import time

u = ctypes.windll.user32
shell = ctypes.windll.shell32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
ROOT = r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen"


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
            out.append((h, c.value, t.value))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(0.8)
p = subprocess.Popen([ROOT + r"\dist\ScreenPen.exe", "--tray"])
time.sleep(4.0)

print(f"pid={p.pid} alive={p.poll() is None}")
cands = wins(p.pid)
for h, c, t in cands:
    print(f"  hwnd={h} cls={c!r} text={t!r}")

print()
for h, c, t in cands:
    for uid in (0, 1, 2):
        nii = NII()
        nii.cbSize = ctypes.sizeof(NII)
        nii.hWnd = h
        nii.uID = uid
        r = wt.RECT()
        rc = shell.Shell_NotifyIconGetRect(ctypes.byref(nii), ctypes.byref(r))
        if rc == 0:
            print(f"  FOUND icon hwnd={h} uid={uid} rect=({r.left},{r.top},"
                  f"{r.right-r.left}x{r.bottom-r.top}) cls={c!r}")
        else:
            print(f"  no icon hwnd={h} uid={uid} hr=0x{rc & 0xFFFFFFFF:08X}")

p.terminate()
time.sleep(0.5)
