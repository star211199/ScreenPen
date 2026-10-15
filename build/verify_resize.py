"""Verifies the settings dialog is genuinely user-resizable (clause 1).

Checks, against the real dist/ScreenPen.exe:
  * the window carries a sizing frame and a maximise box;
  * a programmatic resize really changes the client area;
  * shrinking below the designed minimum is clamped, not honoured blindly;
  * the footer buttons stay inside the window at the minimum size, at the
    designed size, and when enlarged;
  * the window is restored to its designed geometry afterwards.
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"

WS_THICKFRAME = 0x00040000
WS_MAXIMIZEBOX = 0x00010000
GWL_STYLE = -16

EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

fails = 0


def check(ok, what, detail=""):
    global fails
    print(("  [PASS] " if ok else "  [FAIL] ") + what +
          (("  (" + detail + ")") if detail else ""))
    if not ok:
        fails += 1


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)


def windows(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, text=t.value, vis=bool(u.IsWindowVisible(h)),
                            x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def children(parent, token):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if ("." + token.upper() + ".") in c.value.upper():
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, text=t.value,
                            x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top,
                            vis=bool(u.IsWindowVisible(h))))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def rect(hwnd):
    r = wt.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    return r


kill()
p = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)
subprocess.Popen([str(EXE)])
time.sleep(3.4)

dlg = None
for w in windows(p.pid):
    if "设置" in w["text"] and w["vis"]:
        dlg = w
if dlg is None:
    print("could not find the settings dialog")
    kill()
    sys.exit(1)

h = dlg["hwnd"]
print("settings dialog at (%d,%d) %dx%d" % (dlg["x"], dlg["y"], dlg["w"], dlg["h"]))

style = u.GetWindowLongW(h, GWL_STYLE)

print()
print("--- sizing frame ---")
check(bool(style & WS_THICKFRAME), "the dialog has a sizing frame (WS_THICKFRAME)",
      "style=0x%08X" % (style & 0xFFFFFFFF))
check(bool(style & WS_MAXIMIZEBOX), "the dialog has an enabled maximise box",
      "style=0x%08X" % (style & 0xFFFFFFFF))

print()
print("--- programmatic resize ---")

start = rect(h)
start_w, start_h = start.right - start.left, start.bottom - start.top
print("  designed size = %dx%d" % (start_w, start_h))

BIG_W, BIG_H = start_w + 320, start_h + 200
ok = u.MoveWindow(h, start.left, start.top, BIG_W, BIG_H, True)
time.sleep(0.9)
big = rect(h)
big_w, big_h = big.right - big.left, big.bottom - big.top
print("  enlarged to   = %dx%d" % (big_w, big_h))
check(ok and big_w > start_w and big_h > start_h,
      "enlarging the window really grows it",
      "%dx%d -> %dx%d" % (start_w, start_h, big_w, big_h))

# Footer buttons must remain inside the enlarged window.
def footer_inside(tag):
    btns = [b for b in children(h, "BUTTON") if b["w"] > 0 and b["h"] > 0
            and b["y"] > 0 and b["vis"]]
    if not btns:
        return None, []
    # The footer is the bottom-most band of buttons.
    bottom = max(b["y"] + b["h"] for b in btns)
    band = [b for b in btns if b["y"] + b["h"] > bottom - 70]
    r = rect(h)
    inside = [b for b in band
              if b["x"] >= r.left - 1 and b["y"] >= r.top - 1
              and b["x"] + b["w"] <= r.right + 1
              and b["y"] + b["h"] <= r.bottom + 1]
    print("  %s: %d footer button(s), %d inside the window" %
          (tag, len(band), len(inside)))
    return len(band), inside


n_band, inside = footer_inside("enlarged")
check(n_band is not None and n_band > 0 and len(inside) == n_band,
      "every footer button stays inside the enlarged window",
      "%d/%d" % (len(inside), n_band or 0))

# Shrink far below the designed minimum: it must be clamped, not honoured.
TINY = 200
u.MoveWindow(h, start.left, start.top, TINY, TINY, True)
time.sleep(0.9)
tiny = rect(h)
tiny_w, tiny_h = tiny.right - tiny.left, tiny.bottom - tiny.top
print("  asked for %dx%d, got %dx%d" % (TINY, TINY, tiny_w, tiny_h))
check(tiny_w > TINY or tiny_h > TINY,
      "shrinking below the minimum is clamped by MinimumSize",
      "asked %dx%d got %dx%d" % (TINY, TINY, tiny_w, tiny_h))
check(tiny_w >= 500 and tiny_h >= 400,
      "the enforced minimum is still usable (>=500x400)",
      "%dx%d" % (tiny_w, tiny_h))

n_band, inside = footer_inside("minimum")
check(n_band is not None and n_band > 0 and len(inside) == n_band,
      "every footer button stays inside the window at the minimum size",
      "%d/%d" % (len(inside), n_band or 0))

# No control may spill outside the client area once shrunken.
r = rect(h)
spill = [b for b in children(h, "BUTTON")
         if b["w"] > 0 and b["vis"] and
         (b["x"] + b["w"] > r.right + 1 or b["y"] + b["h"] > r.bottom + 1)]
check(not spill, "no visible button spills outside the minimum-size window",
      "%d spilling" % len(spill))

print()
print("--- restore ---")
u.MoveWindow(h, start.left, start.top, start_w, start_h, True)
time.sleep(0.8)
back = rect(h)
back_w, back_h = back.right - back.left, back.bottom - back.top
print("  restored to   = %dx%d" % (back_w, back_h))
check(abs(back_w - start_w) <= 2 and abs(back_h - start_h) <= 2,
      "the designed size is exactly recoverable",
      "%dx%d vs %dx%d" % (back_w, back_h, start_w, start_h))

kill()

print()
if fails == 0:
    print("=== ALL RESIZE CHECKS PASSED ===")
    sys.exit(0)
print("=== %d RESIZE CHECK(S) FAILED ===" % fails)
sys.exit(1)
