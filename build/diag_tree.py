"""Diagnose why the settings cards are not visible.

Dumps every control with its ABSOLUTE screen rectangle, visibility, parent
chain and BackColor, so we can tell whether the cards are (a) not created,
(b) created with zero size, (c) created but off-screen, or (d) painted but
covered by a sibling.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import time

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        u.SetProcessDPIAware()

EXE = r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen\dist\ScreenPen.exe"

p = subprocess.Popen([EXE], cwd=r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen\dist")
time.sleep(4.5)

found = []
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def cb(h, l):
    pid = wt.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    if pid.value == p.pid and u.IsWindowVisible(h):
        n = u.GetWindowTextLengthW(h)
        b = ctypes.create_unicode_buffer(n + 2)
        u.GetWindowTextW(h, b, n + 2)
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, cls, 256)
        if b.value.strip() and "Window.8" in cls.value:
            found.append((h, b.value))
    return True


u.EnumWindows(EP(cb), 0)
if not found:
    print("no window")
    p.terminate()
    raise SystemExit(1)

top, title = found[0]
print(f"top: {title!r}")

# Walk the child tree manually, tracking absolute origin so we can report
# positions relative to the top window.
def client_to_screen(hwnd, pt):
    p2 = wt.POINT(pt[0], pt[1])
    u.ClientToScreen(hwnd, ctypes.byref(p2))
    return (p2.x, p2.y)


rows = []


def walk(parent, depth):
    kids = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb2(h, l):
        # only direct children: verify by comparing parent
        if u.GetParent(h) == parent:
            kids.append(h)
        return True

    u.EnumChildWindows(parent, CB(cb2), 0)
    for h in kids:
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        n = u.GetWindowTextLengthW(h)
        b = ctypes.create_unicode_buffer(n + 2)
        u.GetWindowTextW(h, b, n + 2)
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, cls, 256)
        short = cls.value.replace("WindowsForms10.", "").split(".app")[0]
        rows.append((depth, r.left, r.top, r.right - r.left, r.bottom - r.top,
                     bool(u.IsWindowVisible(h)), short, b.value[:36]))
        if depth < 8:
            walk(h, depth + 1)


walk(top, 0)

print(f'\n{"d":<2} {"x":>6} {"y":>6} {"w":>5} {"h":>5}  {"vis":<5} {"class":<10} text')
print("-" * 88)
for d, x, y, w, h, v, c, t in sorted(rows, key=lambda r: (r[0], r[2], r[1])):
    flag = ""
    if w == 0 or h == 0:
        flag = "  ZERO"
    print(f'{d:<2} {x:>6} {y:>6} {w:>5} {h:>5}  {str(v):<5} {c:<10} {"  "*d}{t}{flag}')

print()
panels = [r for r in rows if r[6] == "Window" and r[3] > 100 and r[4] > 50]
print(f"large container panels ({len(panels)}):")
for d, x, y, w, h, v, c, t in panels:
    print(f"   depth={d} at ({x},{y}) size {w}x{h} visible={v}")

p.terminate()
