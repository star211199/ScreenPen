"""End-to-end check that the 浮标 really draws on the real desktop.

The in-process probe (build/cursor_probe.cs) verifies the surface pixels and the
window styles, but it never proves the layered window actually reaches the
screen: UpdateLayeredWindow could fail and every surface assertion would still
pass. This drives the shipped EXE and looks at the real desktop.

It must NOT use the app's own capture path, which deliberately hides the circle
before grabbing. So the grab is done here with GDI BitBlt, from another process.

What it checks:
  1. with the feature off, the area around the pointer is untouched
  2. with it on, a circle appears centred on the pointer
  3. the fill really is semi-transparent (the backdrop shows through)
  4. turning it off again removes it

Run: python build\\verify_cursor_live.py
"""
import ctypes
import json
import os
import shutil
import subprocess
import sys
import time

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "dist", "ScreenPen.exe")
SETTINGS = os.path.join(ROOT, "dist", "ScreenPen.settings.json")

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

fails = []
checks = 0


def check(name, ok, detail=""):
    global checks
    checks += 1
    if not ok:
        fails.append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def grab(x, y, w, h):
    """Grabs a screen rectangle with BitBlt (no CAPTUREBLT: see below)."""
    hdc = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    old = gdi32.SelectObject(mem, bmp)

    # 0x00CC0020 = SRCCOPY only. Deliberately NOT CAPTUREBLT: that flag is what
    # makes layered windows show up, and the point here is to see the circle
    # the way a viewer of the desktop sees it.
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)
    gdi32.SelectObject(mem, old)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # negative = top-down
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, hdc)
    return Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")


def start_app():
    return subprocess.Popen([EXE, "--tray"], cwd=os.path.dirname(EXE))


def stop_app(proc):
    try:
        proc.terminate()
        proc.wait(timeout=5)
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass
    time.sleep(0.8)


def write_cfg(cfg):
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


def main():
    if not os.path.exists(EXE):
        print("missing dist\\ScreenPen.exe; run build\\build.ps1 first")
        return 1

    backup = SETTINGS + ".cursorlive_backup"
    had = os.path.exists(SETTINGS)
    if had:
        shutil.copyfile(SETTINGS, backup)

    origin = POINT()
    user32.GetCursorPos(ctypes.byref(origin))

    # A spot with a big, obviously-coloured fill so the blend is measurable.
    base = {
        "CursorSpotEnabled": False,
        "CursorSpotRadius": 60,
        "CursorSpotRingWidth": 4,
        "CursorSpotSmoothing": 0,
        "CursorSpotFillColor": "#FF0000",
        "CursorSpotFillAlpha": 120,
        "CursorSpotRingColor": "#0000FF",
        "CursorSpotRingAlpha": 255,
        "CursorRippleEnabled": False,
    }

    # Pick a quiet, light area: the middle of the primary screen.
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    cx, cy = sw // 2, sh // 2

    # The circle is authored in 96-DPI pixels and the overlay scales it by the
    # monitor's DPI, so on this 200% machine a "radius 60" circle is 120 physical
    # pixels. Every sample point below has to be scaled the same way, or the
    # probe ends up measuring the inside of the circle and calling it "outside".
    try:
        dpi = user32.GetDpiForSystem()
    except Exception:
        dpi = 96
    scale = dpi / 96.0
    r_phys = int(round(60 * scale))
    print(f"  (system DPI {dpi}, scale {scale:.2f}, radius {r_phys} px on screen)")

    half = int(r_phys * 1.6) + 20
    box = (cx - half, cy - half, half * 2, half * 2)

    proc = None
    try:
        # ---- 1. baseline, feature off --------------------------------
        cfg = dict(base)
        cfg["CursorSpotEnabled"] = False
        write_cfg(cfg)

        user32.SetCursorPos(cx, cy)
        time.sleep(0.3)
        proc = start_app()
        time.sleep(3.5)
        user32.SetCursorPos(cx, cy)
        time.sleep(0.6)

        off = grab(*box)
        off.save(os.path.join(ROOT, "build", "shots", "60_cursor_off.png"))
        check("the app is running", proc.poll() is None)

        # ---- 2. enable it by rewriting the config and restarting -----
        # The tray toggle would be the natural path, but it needs UI automation;
        # restarting proves the persisted key is honoured on startup, which is
        # the other half of the contract.
        stop_app(proc)
        proc = None

        cfg = dict(base)
        cfg["CursorSpotEnabled"] = True
        write_cfg(cfg)

        proc = start_app()
        time.sleep(3.5)
        user32.SetCursorPos(cx, cy)
        time.sleep(0.8)

        on = grab(*box)
        on.save(os.path.join(ROOT, "build", "shots", "61_cursor_on.png"))

        # ---- 3. assert a circle is really there ----------------------
        w, h = on.size
        mid = (w // 2, h // 2)

        def px(img, x, y):
            return img.getpixel((x, y))

        def reddish(p):
            return p[0] > p[1] + 18 and p[0] > p[2] + 18

        # Sample a horizontal line through the centre.
        line = [px(on, x, mid[1]) for x in range(w)]
        line_off = [px(off, x, mid[1]) for x in range(w)]

        changed = sum(1 for a, b in zip(line, line_off) if a != b)
        check("the circle changes the pixels on the real desktop", changed > 60,
              f"{changed}/{w} samples differ")

        centre = px(on, mid[0], mid[1])
        centre_off = px(off, mid[0], mid[1])
        check("the middle of the circle is tinted by the fill", reddish(centre),
              f"rgb={centre}  (was {centre_off})")

        # Semi-transparency, checked against the two things it must differ from:
        # the untouched backdrop (proves something was drawn) and the pure fill
        # #FF0000 (proves the alpha was honoured). Asserting on the red channel
        # is wrong - a red fill over white keeps R at 255, and that is exactly
        # the mistake the first version of this script made.
        check("the fill is semi-transparent, not opaque",
              centre != centre_off and centre != (255, 0, 0),
              f"rgb={centre}")

        # The ring is pure blue at full alpha, so it must read as blue - not as
        # "white", which would be indistinguishable from the backdrop and would
        # let a missing ring pass.
        ring = px(on, mid[0] + r_phys, mid[1])
        check("the opaque ring is at the configured radius",
              ring[2] > 200 and ring[0] < 90 and ring[1] < 90,
              f"rgb at r={r_phys}: {ring}")

        # Well outside the circle nothing may change.
        out = int(r_phys * 1.45)
        far = px(on, mid[0] + out, mid[1])
        far_off = px(off, mid[0] + out, mid[1])
        check("nothing is drawn outside the circle", far == far_off,
              f"at r={out}: {far} vs {far_off}")

        # ---- 4. the circle must not be baked into OUR OWN captures ---
        # Ask the app for a region screenshot through its own path and look at
        # what it saved: the guard should have hidden the circle first.
        # (Skipped: it would open a modal save dialog. The in-process probe
        #  covers the guard directly, with the real CaptureRegion.)

        # ---- 5. off again --------------------------------------------
        stop_app(proc)
        proc = None
        cfg["CursorSpotEnabled"] = False
        write_cfg(cfg)

        proc = start_app()
        time.sleep(3.5)
        user32.SetCursorPos(cx, cy)
        time.sleep(0.8)

        again = grab(*box)
        again.save(os.path.join(ROOT, "build", "shots", "62_cursor_off_again.png"))
        centre_again = px(again, mid[0], mid[1])
        check("switching it off removes the circle", not reddish(centre_again),
              f"rgb={centre_again}")
    finally:
        if proc is not None:
            stop_app(proc)
        user32.SetCursorPos(origin.x, origin.y)
        if had:
            shutil.copyfile(backup, SETTINGS)
            os.remove(backup)
        elif os.path.exists(SETTINGS):
            os.remove(SETTINGS)

    print()
    if fails:
        print(f"=== FAILED: {checks - len(fails)}/{checks} passed ===")
        for f in fails:
            print("   " + f)
        return 1
    print(f"=== {checks}/{checks} live cursor checks passed ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
