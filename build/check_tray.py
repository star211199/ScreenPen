"""Verify the ScreenPen tray icon is really registered with the shell.

A NotifyIcon that was added but never made visible - or whose icon failed to
load - is invisible to the user even though the process runs happily. This
asserts the shell itself knows about the icon, and exits non-zero on failure so
it can gate a build.

Why not read the notification area's ToolbarWindow32? On Windows 11 the tray is
XAML-based: Shell_TrayWnd now hosts DesktopWindowXamlSource, and there is no
ToolbarWindow32/TTM_ENUMTOOLS to walk (verified - only TrayNotifyWnd, ReBar and
the XAML bridges exist). Shell_NotifyIconGetRect is the supported query and
works on both Win10 and Win11.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

u = ctypes.windll.user32
shell = ctypes.windll.shell32

try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen"
EXE = ROOT + r"\dist\ScreenPen.exe"
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


class NII(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("hWnd", wt.HWND),
                ("uID", wt.UINT), ("guidItem", ctypes.c_byte * 16)]


shell.Shell_NotifyIconGetRect.restype = ctypes.c_long
shell.Shell_NotifyIconGetRect.argtypes = [ctypes.POINTER(NII), ctypes.POINTER(wt.RECT)]


def icon_rect(hwnd, uid):
    """Return the icon's screen rect, or None when the shell has no such icon."""
    nii = NII()
    nii.cbSize = ctypes.sizeof(NII)
    nii.hWnd = hwnd
    nii.uID = uid
    r = wt.RECT()
    if shell.Shell_NotifyIconGetRect(ctypes.byref(nii), ctypes.byref(r)) == 0:
        return r
    return None


def windows_of(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, cls=c.value, title=t.value,
                            vis=bool(u.IsWindowVisible(h)),
                            w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def find_icon(pid):
    """The icon is owned by one of the app's message windows."""
    for w in windows_of(pid):
        for uid in (1, 2, 0):
            r = icon_rect(w["hwnd"], uid)
            if r is not None:
                return w, uid, r
    return None, None, None


def main():
    print("--- tray icon registration ---")
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)

    proc = subprocess.Popen([EXE, "--tray"], cwd=ROOT + r"\dist")
    time.sleep(4.0)

    record("--tray keeps the process alive", proc.poll() is None,
           f"pid={proc.pid}")

    w, uid, r = find_icon(proc.pid)
    record("the shell has the ScreenPen tray icon registered", w is not None,
           f"hwnd={w['hwnd']} uID={uid} rect=({r.left},{r.top},"
           f"{r.right - r.left}x{r.bottom - r.top})" if w else "no icon found")

    if w is not None:
        # The icon must be on-screen and inside the shell's notification area,
        # not a zero-size or off-screen placeholder.
        sw, sh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
        record("the tray icon has a real on-screen rect",
               0 < r.left < sw and 0 < r.top < sh
               and (r.right - r.left) > 8 and (r.bottom - r.top) > 8,
               f"{r.right - r.left}x{r.bottom - r.top} at ({r.left},{r.top}) "
               f"on {sw}x{sh}")

        record("the icon lives in the taskbar strip, not the middle of the screen",
               r.top >= sh - 160, f"top={r.top} screenH={sh}")

        # Negative control: the rect must disappear when the app exits,
        # otherwise the query is just echoing something unrelated.
        hwnd, uid_keep = w["hwnd"], uid
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except Exception:
            subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                           capture_output=True)
        time.sleep(1.6)
        gone = icon_rect(hwnd, uid_keep) is None
        record("the icon is unregistered after the app exits (negative control)",
               gone, "rect cleared" if gone else "icon still reported present")

    # In --tray mode the app must not leave a visible main window on screen.
    vis = [x for x in windows_of(proc.pid) if x["vis"] and x["w"] > 200 and x["h"] > 200]
    record("--tray mode shows no visible main window", len(vis) == 0,
           f"{len(vis)} large visible window(s)")

    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.5)

    print()
    passed = sum(1 for _, ok, _ in results if ok)
    print(f"=== {passed}/{len(results)} checks passed ===")
    for n, ok, dd in results:
        if not ok:
            print(f"  FAILED: {n}  {dd}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
