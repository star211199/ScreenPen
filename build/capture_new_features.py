"""Visual check for the two new UI behaviours.

Captures, as PNGs:
  * the settings dialog at its enforced minimum size, where geometry assertions
    cannot see text clipping or overlapping artwork;
  * a real pin-to-desktop window, produced by driving the region selector's
    贴图 button, so the actual rendered pin (border, close button, image) and
    its always-on-top placement can be eyeballed.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
u = user32

try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"
# NOT build/shots: verify_ui.py globs build/shots/*.png and compares every pair
# for size mismatch, so dropping differently-sized captures in there makes it
# report ~15 bogus failures. Keep this script's output in its own directory.
OUT = ROOT / "build" / "featshots"
OUT.mkdir(parents=True, exist_ok=True)

EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
WS_THICKFRAME = 0x00040000


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


def grab(hwnd, rect, path):
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0:
        return False
    hdc = u.GetWindowDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mdc, bmp)
    u.PrintWindow(hwnd, mdc, 2)          # PW_RENDERFULLCONTENT
    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount, bi.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB").save(path)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    u.ReleaseDC(hwnd, hdc)
    return True


def rect_of(hwnd):
    r = wt.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def windows(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            out.append(dict(hwnd=h, text=t.value, cls=c.value,
                            vis=bool(u.IsWindowVisible(h)), rect=rect_of(h)))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def children(parent, token=None):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if token is None or ("." + token.upper() + ".") in c.value.upper():
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            out.append(dict(hwnd=h, cls=c.value, text=t.value, rect=rect_of(h)))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)


kill()
p = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)

# ---------------------------------------------------------------------------
# 1. Settings dialog at its enforced minimum.
# ---------------------------------------------------------------------------
subprocess.Popen([str(EXE)])
time.sleep(3.4)

dlg = None
for w in windows(p.pid):
    if "设置" in w["text"] and w["vis"]:
        dlg = w
if dlg is None:
    print("FAIL: no settings dialog")
    kill()
    sys.exit(1)

h = dlg["hwnd"]
r0 = rect_of(h)
print("designed geometry: %dx%d" % (r0.right - r0.left, r0.bottom - r0.top))

start = (r0.left, r0.top, r0.right - r0.left, r0.bottom - r0.top)
u.MoveWindow(h, start[0], start[1], 200, 200, True)
time.sleep(1.0)
rmin = rect_of(h)
mw, mh = rmin.right - rmin.left, rmin.bottom - rmin.top
print("minimum geometry : %dx%d" % (mw, mh))
grab(h, rmin, OUT / "20_settings_min.png")
print("captured 20_settings_min.png")

# A wider-than-designed window: only the footer re-anchors, so this shows
# whether the extra space looks sane rather than broken.
u.MoveWindow(h, start[0], start[1], start[2] + 420, start[3] + 160, True)
time.sleep(1.0)
rbig = rect_of(h)
grab(h, rbig, OUT / "21_settings_wide.png")
print("captured 21_settings_wide.png  (%dx%d)"
      % (rbig.right - rbig.left, rbig.bottom - rbig.top))

# Restore, then leave the dialog open so it can be closed by the screenshot
# worker below rather than leaving a stray modal window.
u.MoveWindow(h, start[0], start[1], start[2], start[3], True)
time.sleep(0.8)

# ---------------------------------------------------------------------------
# 2. A real pin, driven through the region selector's 贴图 button.
# ---------------------------------------------------------------------------
print()
print("--- pin capture ---")
# Close the settings dialog so it cannot cover the pin.
subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
time.sleep(1.0)
subprocess.Popen([str(EXE), "--tray"])
time.sleep(3.0)

# Trigger the pin flow with the region hotkey (default Ctrl+Shift+4 style is
# configurable, so read it from the shipped config).
import json
cfg = json.loads((ROOT / "dist" / "ScreenPen.settings.json").read_text("utf-8"))


def parse_hotkey(s):
    """'Ctrl+Shift+4' -> (mods, vk)."""
    MOD = {"ctrl": 0x0002, "shift": 0x0004, "alt": 0x0001, "win": 0x0008}
    mods, vk = 0, 0
    for part in str(s or "").split("+"):
        p = part.strip().lower()
        if p in MOD:
            mods |= MOD[p]
        elif p:
            if len(p) == 1:
                vk = ord(p.upper())
            elif p.startswith("f") and p[1:].isdigit():
                vk = 0x70 + int(p[1:]) - 1
            else:
                vk = {"esc": 0x1B, "space": 0x20, "tab": 0x09,
                      "enter": 0x0D}.get(p, 0)
    return mods, vk


hk = cfg.get("ScreenshotKey")
print("region hotkey from config:", hk)
mods, vk = parse_hotkey(hk)

# ALTERNATE approach that avoids depending on the hotkey parsing: use the tray
# menu's 区域截图并钉在桌面 entry via its window message. Simpler and more
# faithful is to just synthesize the hotkey.
KEYEVENTF_KEYUP = 0x0002


def press(mods, vk):
    for m in (0x0002, 0x0004, 0x0001, 0x0008):
        if mods & m:
            u.keybd_event(m, 0, 0, 0)
    time.sleep(0.15)
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.12)
    u.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.12)
    for m in (0x0008, 0x0001, 0x0004, 0x0002):
        if mods & m:
            u.keybd_event(m, 0, KEYEVENTF_KEYUP, 0)


# Find the tray process' pid by name: the Popen handle above was killed before
# the pin flow started, and the relaunched instance has a different pid.
import subprocess as sp
out = sp.run(["tasklist", "/FI", "IMAGENAME eq ScreenPen.exe", "/FO", "CSV"],
             capture_output=True, text=True).stdout
for line in out.splitlines()[1:]:
    parts = [x.strip('"') for x in line.split('","')]
    if parts and parts[0].lower() == "screenpen.exe":
        pid = int(parts[1])
        break
print("ScreenPen pid:", pid)

if pid is None:
    print("FAIL: could not find the running process")
    kill()
    sys.exit(1)

press(mods, vk)
time.sleep(2.0)

sel = None
for w in windows(pid):
    c = w["cls"]
    if w["vis"] and "WindowsForms" in c:
        r = w["rect"]
        area = (r.right - r.left) * (r.bottom - r.top)
        if area > 1_000_000 and not sel:
            sel = w
if sel is None:
    # The selector is a full-desktop borderless window; find the largest.
    best = None
    for w in windows(pid):
        if not w["vis"]:
            continue
        r = w["rect"]
        a = (r.right - r.left) * (r.bottom - r.top)
        if best is None or a > best[0]:
            best = (a, w)
    if best:
        sel = best[1]

if sel is None:
    print("FAIL: the region selector never appeared")
    kill()
    sys.exit(1)

rsel = sel["rect"]
print("selector: %dx%d at (%d,%d)" % (rsel.right - rsel.left,
                                      rsel.bottom - rsel.top,
                                      rsel.left, rsel.top))

# Drag a selection in the middle of the screen.
u.SetForegroundWindow(sel["hwnd"])
time.sleep(0.4)
x1, y1 = rsel.left + 600, rsel.top + 400
x2, y2 = rsel.left + 1500, rsel.top + 900
u.SetCursorPos(x1, y1)
time.sleep(0.3)
u.mouse_event(0x0002, 0, 0, 0, 0)
time.sleep(0.25)
for i in range(1, 11):
    u.SetCursorPos(x1 + (x2 - x1) * i // 10, y1 + (y2 - y1) * i // 10)
    time.sleep(0.06)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.9)

# Click the 贴图 button in the toolbar.
btns = [b for b in children(sel["hwnd"], "BUTTON") if b["text"].strip()]
print("toolbar buttons:", [(b["text"], b["rect"].left, b["rect"].top) for b in btns])
pin_btn = None
for b in btns:
    if "贴图" in b["text"]:
        pin_btn = b
if pin_btn is None:
    print("FAIL: no 贴图 button found")
    kill()
    sys.exit(1)

rb = pin_btn["rect"]
cx, cy = (rb.left + rb.right) // 2, (rb.top + rb.bottom) // 2
u.SetCursorPos(cx, cy)
time.sleep(0.3)
u.mouse_event(0x0002, 0, 0, 0, 0)
time.sleep(0.08)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(1.6)

# Capture the pin: a borderless visible window smaller than the screen.
pin = None
for w in windows(pid):
    r = w["rect"]
    ww, hh = r.right - r.left, r.bottom - r.top
    if w["vis"] and 100 < ww < 2000 and 60 < hh < 1400 and "WindowsForms" in w["cls"]:
        pin = w
if pin is None:
    print("FAIL: no pin window appeared")
    kill()
    sys.exit(1)

rp = pin["rect"]
print("pin window: %dx%d at (%d,%d) text=%r"
      % (rp.right - rp.left, rp.bottom - rp.top, rp.left, rp.top, pin["text"]))
# Grow it so the close button and border are clearly visible in the capture.
u.MoveWindow(pin["hwnd"], rp.left, rp.top,
             rp.right - rp.left, rp.bottom - rp.top, True)
time.sleep(0.6)
# Hover so the close button paints.
u.SetCursorPos(rp.right - 20, rp.top + 20)
time.sleep(0.6)
grab(pin["hwnd"], rect_of(pin["hwnd"]), OUT / "22_pin_window.png")
print("captured 22_pin_window.png")

kill()
print()
print("done")
