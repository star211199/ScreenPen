"""The user's complaint is 'hotkey changes do not take effect after save', which
is a SAVE-pipeline bug, not a keystroke-capture bug (keystroke capture is proven
in-process by hotkey_record_probe.cs).

Can the harness set a box's text cross-process with WM_SETTEXT and then click
保存设置? WM_SETTEXT is a system message (< WM_USER) so it is marshalled across
processes, unlike the keystroke path.
"""
import ctypes
import ctypes.wintypes as wt
import json
import shutil
import subprocess
import time
from pathlib import Path

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"
CFG = ROOT / "dist" / "ScreenPen.settings.json"
BAK = ROOT / "build" / "obj" / "diag_cfg_backup.json"
EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
WM_SETTEXT = 0x000C
BM_CLICK = 0x00F5


def wins(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            out.append((h, t.value, bool(u.IsWindowVisible(h))))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def children(parent, tok):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if ("." + tok.upper() + ".") in c.value.upper():
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, text=t.value, x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def txt(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, b, 256)
    return b.value


shutil.copy2(CFG, BAK)
try:
    print("before:", json.loads(CFG.read_text(encoding="utf-8"))["ToggleKey"])

    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)
    p = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.8)
    subprocess.Popen([str(EXE)])
    time.sleep(3.2)

    s = None
    for h, t, v in wins(p.pid):
        if "设置" in t and v:
            s = h
    edits = [e for e in children(s, "EDIT") if e["w"] > 0]
    edits.sort(key=lambda e: (e["y"], e["x"]))
    box = edits[0]
    print("box before:", repr(box["text"]))

    # 1. Simulate what the recorder would have written.
    u.SendMessageW(box["hwnd"], WM_SETTEXT, 0, ctypes.c_wchar_p("F2"))
    time.sleep(0.4)
    print("box after WM_SETTEXT:", repr(txt(box["hwnd"])))

    # 2. Click 保存设置.
    save = None
    for b in children(s, "BUTTON"):
        if "保存" in b["text"]:
            save = b
    print("save button:", repr(save["text"]) if save else None)
    u.SendMessageW(save["hwnd"], BM_CLICK, 0, 0)
    time.sleep(2.0)

    d = json.loads(CFG.read_text(encoding="utf-8"))
    print("after save ToggleKey:", repr(d.get("ToggleKey")))
    print(">>> WM_SETTEXT + BM_CLICK drives the real save pipeline:",
          d.get("ToggleKey") == "F2")

    # 3. Does the rebound key actually work after a restart?
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)
    p2 = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.8)
    u.keybd_event(0x71, 0, 0, 0)   # F2
    time.sleep(0.05)
    u.keybd_event(0x71, 0, 2, 0)
    time.sleep(1.2)
    ov = None
    for h, t, v in wins(p2.pid):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        if c.value.startswith("WindowsForms10.Window.8") and (r.right - r.left) > 2000:
            ov = (r.right - r.left, r.bottom - r.top)
    print("overlay after pressing F2:", ov)
    print(">>> rebound F2 opens the overlay:", ov is not None)
finally:
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.7)
    shutil.copy2(BAK, CFG)
    time.sleep(0.3)
    print("restored ToggleKey:",
          json.loads(CFG.read_text(encoding="utf-8"))["ToggleKey"])
