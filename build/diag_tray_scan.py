"""Can we read the notification-area toolbar to prove the tray icon exists?"""
import ctypes
import ctypes.wintypes as wt

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def find(cls, parent=0):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if c.value == cls:
            out.append(h)
        return True

    u.EnumWindows(EP(cb), 0) if parent == 0 else u.EnumChildWindows(parent, EP(cb), 0)
    return out


for cls in ("Shell_TrayWnd", "TrayNotifyWnd", "SysPager", "ToolbarWindow32",
            "NotifyIconOverflowWindow", "Shell_SecondaryTrayWnd"):
    w = find(cls)
    print(f"{cls:30s} top-level: {len(w)} {w}")

tray = find("Shell_TrayWnd")
if tray:
    for cls in ("TrayNotifyWnd", "SysPager", "ToolbarWindow32"):
        print(f"  under Shell_TrayWnd -> {cls}: {find(cls, tray[0])}")
    for cls in ("TrayNotifyWnd",):
        for h in find(cls, tray[0]):
            for c2 in ("SysPager", "ToolbarWindow32"):
                print(f"    under TrayNotifyWnd -> {c2}: {find(c2, h)}")
            for h2 in find("SysPager", h):
                for c3 in ("ToolbarWindow32",):
                    print(f"      under SysPager -> {c3}: {find(c3, h2)}")
