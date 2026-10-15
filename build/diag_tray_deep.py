"""Enumerate ALL descendants of Shell_TrayWnd to find an accessible tray path."""
import ctypes
import ctypes.wintypes as wt

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def cls_of(h):
    c = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, c, 256)
    return c.value


def text_of(h):
    n = u.GetWindowTextLengthW(h)
    b = ctypes.create_unicode_buffer(n + 2)
    u.GetWindowTextW(h, b, n + 2)
    return b.value


def walk(h, depth=0, maxd=4):
    if depth > maxd:
        return
    kids = []

    def cb(c, l):
        kids.append(c)
        return True

    u.EnumChildWindows(h, EP(cb), 0)
    for c in kids:
        print("  " * depth + f"{cls_of(c)!r} {text_of(c)!r}")
        walk(c, depth + 1, maxd)


roots = []


def top(h, l):
    if cls_of(h) == "Shell_TrayWnd":
        roots.append(h)
    return True


u.EnumWindows(EP(top), 0)
for r in roots:
    print(f"Shell_TrayWnd {r}")
    walk(r, 1)
