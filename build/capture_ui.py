"""Capture the ScreenPen settings window so the UI can be visually verified.

Launches the app, finds its main window, brings it to the foreground, and
saves a PNG. Then closes the app. Also optionally clicks through the
navigation rail to capture every page.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

EXE = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen\dist\ScreenPen.exe")
OUT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen\build\shots")
OUT.mkdir(parents=True, exist_ok=True)

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# Match the target's per-monitor-v2 awareness. Without this, Windows
# virtualizes coordinates and every rect we compute is scaled, so clicks land
# in the wrong place and captures are cropped.
try:
    if not user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
        raise OSError
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        user32.SetProcessDPIAware()


def find_windows(pid):
    """Return [(hwnd, title, visible)] for top-level windows of pid."""
    result = []
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, lparam):
        wpid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid:
            n = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 2)
            user32.GetWindowTextW(hwnd, buf, n + 2)
            rect = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            result.append((hwnd, buf.value, bool(user32.IsWindowVisible(hwnd)), rect))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return result


def client_crop(hwnd):
    """Offset of the client area inside the whole-window image.

    PrintWindow renders the window *including* its non-client frame, so the
    resulting PNG starts with the OS caption (a light band, ~62px at 200% DPI)
    and the app's own content only begins below it. verify_ui.py's geometry
    constants (NAV_W, HEADER_H, FOOTER_H) are all client-relative, so an
    uncropped image makes "header band is a card" measure the caption instead of
    the app's header. Returns (left, top, width, height) in window coordinates.
    """
    wr = wt.RECT()
    cr = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(wr))
    user32.GetClientRect(hwnd, ctypes.byref(cr))

    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))

    ox = origin.x - wr.left
    oy = origin.y - wr.top
    cw = cr.right - cr.left
    ch = cr.bottom - cr.top
    if cw <= 0 or ch <= 0 or ox < 0 or oy < 0:
        return None
    return (ox, oy, cw, ch)


def capture(hwnd, rect, path, crop=None):
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    if w <= 0 or h <= 0:
        return None
    hdc = user32.GetWindowDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mdc, bmp)
    # PW_RENDERFULLCONTENT = 2 : captures DWM-composited content
    user32.PrintWindow(hwnd, mdc, 2)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                    ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h           # negative => top-down
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1)
    img = img.convert("RGB")

    if crop is not None:
        ox, oy, cw, ch = crop
        if ox + cw <= w and oy + ch <= h:
            img = img.crop((ox, oy, ox + cw, oy + ch))

    img.save(path)
    return img

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(hwnd, hdc)
    return img


def click(hwnd, x, y):
    """Click in SCREEN coords using the real input queue.

    SetForegroundWindow is required first: these are ordinary child buttons, so
    a synthetic PostMessage may be ignored by the message loop when the window
    is not active.
    """
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.15)
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.12)
    user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.06)
    user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
    time.sleep(0.5)


NAV_LABELS = ["快捷键", "画笔", "缩放", "截图", "常规", "关于"]
SHOT_NAMES = ["hotkeys", "brush", "zoom", "screenshot", "general", "about"]


def find_nav_buttons(hwnd):
    """Return the nav-rail buttons as (label, screen_x, screen_y) centres.

    The button positions are discovered from the live control tree instead of
    being recomputed with arithmetic: the rail sits inside a docked panel, so
    window-relative formulas are off by the header height and every click lands
    on the wrong page.
    """
    found = []
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, cls, 256)
        if "BUTTON" not in cls.value:
            return True
        n = user32.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(h, buf, n + 2)
        label = buf.value.strip()
        # Nav labels carry a leading glyph, so match by suffix.
        for want in NAV_LABELS:
            if label.endswith(want):
                r = wt.RECT()
                user32.GetWindowRect(h, ctypes.byref(r))
                found.append((want,
                              r.left + (r.right - r.left) // 2,
                              r.top + (r.bottom - r.top) // 2))
        return True

    user32.EnumChildWindows(hwnd, EnumProc(cb), 0)
    # Keep rail order.
    found.sort(key=lambda t: NAV_LABELS.index(t[0]))
    return found


def main():
    proc = subprocess.Popen([str(EXE)], cwd=str(EXE.parent))
    time.sleep(4.0)

    wins = find_windows(proc.pid)
    target = None
    for hwnd, title, visible, rect in wins:
        if visible and title.strip():
            target = (hwnd, title, rect)
            break

    if not target:
        print("FAIL: no visible titled window found")
        print("windows:", [(t, v) for _, t, v, _ in wins])
        proc.terminate()
        return 1

    hwnd, title, rect = target
    print(f"window: {title!r}  rect=({rect.left},{rect.top},{rect.right},{rect.bottom})")

    user32.SetForegroundWindow(hwnd)
    time.sleep(0.5)

    rect_local = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect_local))

    crop = client_crop(hwnd)
    if crop is None:
        print("WARN: could not resolve the client area; capturing the whole window")
    else:
        print(f"client area: {crop[2]}x{crop[3]} at +{crop[0]}+{crop[1]} inside the window")

    buttons = find_nav_buttons(hwnd)
    print(f"nav buttons found: {[b[0] for b in buttons]}")
    if len(buttons) != len(NAV_LABELS):
        print(f"WARN: expected {len(NAV_LABELS)} nav buttons, found {len(buttons)}")

    # The first page is already selected on launch, so capture it as-is, then
    # click each remaining rail entry and capture the result.
    #
    # The click is verified, not assumed: the first synthetic click after
    # SetForegroundWindow is sometimes swallowed by the activation, and the
    # result was a page saved under the wrong name (01_brush.png showing 快捷键).
    # A capture identical to the previous page means the switch did not happen,
    # so click again.
    previous = None
    for i, (label, bx, by) in enumerate(buttons):
        img = None
        for attempt in range(3):
            if i > 0 or attempt > 0:
                click(hwnd, bx, by)
            img = capture(hwnd, rect_local, OUT / f"{i:02d}_{SHOT_NAMES[i]}.png", crop)
            if img is None:
                break
            if previous is None or img.tobytes() != previous.tobytes():
                break
            print(f"  retry {attempt + 1}: page did not change after clicking '{label}'")

        if img is None:
            print(f"FAILED   {i:02d}_{SHOT_NAMES[i]}.png")
        else:
            print(f"captured {i:02d}_{SHOT_NAMES[i]}.png  (nav '{label}' at {bx},{by})")
            previous = img

    proc.terminate()
    try:
        proc.wait(timeout=5)
    except Exception:
        proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())

