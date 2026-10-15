"""Diagnostic: does the duplicate-key confirmation prompt actually appear?

verify_hotkeys.py used SendMessageW(BM_CLICK), which BLOCKS until the button's
click handler returns. If that handler opens a modal MessageBox, the probe can
never observe the prompt - and if it ever hangs, the whole test hangs.

This script uses PostMessageW (asynchronous) and polls for the dialog, so it can
tell the difference between "no prompt" and "prompt present but unobservable".
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time

u = ctypes.windll.user32

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "dist", "ScreenPen.exe")
CFG = os.path.join(ROOT, "dist", "ScreenPen.settings.json")

BM_CLICK = 0x00F5
WM_SETTEXT = 0x000C
WM_GETTEXT = 0x000D
WM_COMMAND = 0x0111
IDYES = 6


def kill_all():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"],
                   capture_output=True)
    time.sleep(1.0)


def read_cfg():
    with open(CFG, "r", encoding="utf-8") as f:
        return json.load(f)


def write_cfg(d):
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)


def enum_windows(pid):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid:
            return True
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, cls, 256)
        ttl = ctypes.create_unicode_buffer(512)
        u.GetWindowTextW(h, ttl, 512)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        out.append(dict(hwnd=h, cls=cls.value, title=ttl.value,
                        vis=bool(u.IsWindowVisible(h)),
                        w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def enum_children(parent, cls=None):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if cls and cls.lower() not in c.value.lower():
            return True
        t = ctypes.create_unicode_buffer(512)
        u.SendMessageW(h, WM_GETTEXT, 512, t)
        out.append(dict(hwnd=h, cls=c.value, text=t.value))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def main():
    kill_all()
    # Snapshot first: this script deliberately writes a conflicting config, and
    # a diagnostic that leaves the user's settings mutated is a bug of its own
    # (an earlier version did exactly that and silently polluted later runs).
    original = read_cfg()
    try:
        return run(original)
    finally:
        kill_all()
        write_cfg(original)
        print("restored original settings")


def run(original):
    d = dict(original)
    d["ClearKey"] = "E"
    d["PenKey"] = "P"          # clean baseline
    d["ZoomInKey"] = "Ctrl+Up"
    write_cfg(d)
    kill_all()

    proc = subprocess.Popen([EXE])
    time.sleep(5.0)

    dlg = None
    for w in enum_windows(proc.pid):
        if "设置" in (w["title"] or ""):
            dlg = w
    if not dlg:
        print("FAIL: settings dialog not found")
        return 1
    print("dialog:", dlg["title"])

    boxes = [c for c in enum_children(dlg["hwnd"], "Edit")]
    def box_with(text):
        for b in boxes:
            t = ctypes.create_unicode_buffer(256)
            u.SendMessageW(b["hwnd"], WM_GETTEXT, 256, t)
            if t.value.strip() == text:
                return b
        return None

    pen = box_with("P")
    zoom = box_with("Ctrl+Up")
    print("pen box:", bool(pen), "zoom box:", bool(zoom))
    if not pen or not zoom:
        kill_all()
        return 1

    u.SendMessageW(zoom["hwnd"], WM_SETTEXT, 0, "Ctrl+Alt+U")
    time.sleep(0.3)
    u.SendMessageW(pen["hwnd"], WM_SETTEXT, 0, "E")
    time.sleep(0.6)

    statics = [c["text"] for c in enum_children(dlg["hwnd"], "Static")]
    print("live readouts:", [s for s in statics if "占用" in s or "重复" in s])

    save = next((b for b in enum_children(dlg["hwnd"], "Button")
                 if "保存" in b["text"]), None)
    if not save:
        print("FAIL: save button not found")
        kill_all()
        return 1

    # Asynchronous click, so the modal loop can be observed.
    u.PostMessageW(save["hwnd"], BM_CLICK, 0, 0)

    prompt = None
    for i in range(40):        # up to 8 s
        time.sleep(0.2)
        for w in enum_windows(proc.pid):
            if w["hwnd"] == dlg["hwnd"]:
                continue
            if "快捷键重复" in (w["title"] or ""):
                prompt = w
                break
        if prompt:
            print("prompt appeared after %.1fs" % (0.2 * (i + 1)))
            break

    if prompt:
        print("prompt title:", prompt["title"], "class:", prompt["cls"])
        for c in enum_children(prompt["hwnd"]):
            if c["text"].strip():
                print("   ", c["cls"], "|", c["text"][:70].replace("\n", " / "))
        u.PostMessageW(prompt["hwnd"], WM_COMMAND, IDYES, 0)
        time.sleep(2.5)
    else:
        print("NO prompt observed within 8s")
        print("windows:", [(w["title"], w["vis"]) for w in enum_windows(proc.pid)])

    after = read_cfg()
    print("PenKey    =", after.get("PenKey"))
    print("ClearKey  =", after.get("ClearKey"))
    print("ZoomInKey =", after.get("ZoomInKey"))
    ok = (after.get("PenKey") == "E"
          and after.get("ZoomInKey") == "Ctrl+Alt+U")
    print("RESULT:", "PASS" if ok else "FAIL")
    kill_all()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
