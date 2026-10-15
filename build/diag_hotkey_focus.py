"""Explain the contradiction: after pressing F2 over a HotkeyBox, no box reports
'F2' on readback, yet the settings file ends up with ToggleKey='F2'.

Checks (a) which window actually holds focus, (b) whether the box text changes
when read with an explicit WM_GETTEXT, and (c) whether the persisted value can be
produced without the box ever being focused.
"""
import ctypes
import ctypes.wintypes as wt
import json
import subprocess
import sys
import time
from pathlib import Path

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    u.SetProcessDPIAware()

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"
CFG = ROOT / "dist" / "ScreenPen.settings.json"
WM_GETTEXT = 0x000D
WM_SETTEXT = 0x000C


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD),
                ("hwndActive", wt.HWND), ("hwndFocus", wt.HWND),
                ("hwndCapture", wt.HWND), ("hwndMenuOwner", wt.HWND),
                ("hwndMoveSize", wt.HWND), ("hwndCaret", wt.HWND),
                ("rcCaret", wt.RECT)]


def text_via_message(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    n = u.SendMessageW(hwnd, WM_GETTEXT, 256, buf)
    return buf.value, n


def text_via_getwindowtext(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(hwnd, buf, 256)
    return buf.value


def kids(parent):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        out.append(dict(hwnd=h, cls=c.value, x=r.left, y=r.top,
                        w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def tops(pid):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and u.IsWindowVisible(h):
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            out.append((h, t.value))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def focused():
    tid = u.GetWindowThreadProcessId(u.GetForegroundWindow(), None)
    gi = GUITHREADINFO()
    gi.cbSize = ctypes.sizeof(GUITHREADINFO)
    if u.GetGUIThreadInfo(tid, ctypes.byref(gi)):
        return gi.hwndFocus, gi.hwndActive
    return None, None


def main():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)

    # Start from a known value.
    cfg = json.loads(CFG.read_text(encoding="utf-8"))
    cfg["ToggleKey"] = "Ctrl+Alt+D"
    CFG.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

    proc = subprocess.Popen([str(EXE)])
    dlg = None
    for _ in range(40):
        for h, t in tops(proc.pid):
            if "设置" in t:
                dlg = h
        if dlg:
            break
        time.sleep(0.5)
    print(f"dialog={dlg}")
    time.sleep(1.0)

    edits = sorted([k for k in kids(dlg) if "EDIT" in k["cls"].upper()],
                   key=lambda e: (e["y"], e["x"]))
    box = edits[0]
    print(f"first box hwnd={box['hwnd']} at ({box['x']},{box['y']})")
    print(f"  initial text: msg={text_via_message(box['hwnd'])[0]!r} "
          f"gwt={text_via_getwindowtext(box['hwnd'])!r}")

    # Focus via click.
    u.SetForegroundWindow(dlg)
    time.sleep(0.5)
    u.SetCursorPos(box["x"] + box["w"] // 2, box["y"] + box["h"] // 2)
    time.sleep(0.2)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.6)

    foc, act = focused()
    print(f"  foreground active={act} focus={foc}")
    print(f"  focus == box? {foc == box['hwnd']}")
    for k in edits[:4]:
        if k["hwnd"] == foc:
            print(f"  -> focused box is index {edits.index(k)} "
                  f"at ({k['x']},{k['y']})")

    # Press F2 and watch every box.
    print("\n  pressing F2 ...")
    u.keybd_event(0x71, 0, 0, 0)
    time.sleep(0.15)
    u.keybd_event(0x71, 0, 2, 0)
    time.sleep(1.0)

    changed = []
    for i, k in enumerate(edits):
        m, _ = text_via_message(k["hwnd"])
        g = text_via_getwindowtext(k["hwnd"])
        if m != "Ctrl+Alt+D" or i > 0:
            if m in ("F2",) or g in ("F2",):
                changed.append((i, k["x"], k["y"], m, g))
    print(f"  boxes now reading 'F2': {changed}")

    print("\n  full readback (msg | gwt):")
    for i, k in enumerate(edits):
        m, _ = text_via_message(k["hwnd"])
        g = text_via_getwindowtext(k["hwnd"])
        flag = "  <== CHANGED" if (m == "F2" or g == "F2") else ""
        print(f"    [{i:>2}] ({k['x']:>5},{k['y']:>5}) msg={m!r:<14} gwt={g!r}{flag}")

    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
