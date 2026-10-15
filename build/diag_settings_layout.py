"""Diagnose verify_upgrade.py's four remaining failures with real data.

Answers, per failure:
  A) "no label is clipped outside the dialog"  - are the 29 outside labels
     genuinely unreachable, or just below the fold of a scrollable page?
  B) "hotkey box records a modifier-less F2"   - did the box ever get focus?
  C) "mosaic draws" diff=0.00%                 - is the sample region uniform
     (mosaic cannot show on a flat backdrop), or does mosaic really not paint?
  D) "modifier results are visually distinct"  - are the diffs truly equal, or
     merely equal after rounding to 4 dp?
"""
import ctypes
import ctypes.wintypes as wt
import json
import subprocess
import time
from pathlib import Path

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "dist" / "ScreenPen.exe"
CFG = ROOT / "dist" / "ScreenPen.settings.json"

EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
WS_VSCROLL = 0x00200000
WS_HSCROLL = 0x00100000


def wins(pid=None):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if pid is None or p.value == pid:
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            out.append(dict(hwnd=h, pid=p.value, cls=c.value, text=t.value,
                            vis=bool(u.IsWindowVisible(h)),
                            x=r.left, y=r.top, w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def children(parent, cls_token=None):
    out = []

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        if cls_token is None or ("." + cls_token.upper() + ".") in c.value.upper():
            t = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, t, 512)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            st = u.GetWindowLongW(h, -16)
            par = u.GetParent(h)
            pr = wt.RECT()
            u.GetWindowRect(par, ctypes.byref(pr))
            out.append(dict(hwnd=h, cls=c.value, text=t.value,
                            x=r.left, y=r.top, w=r.right - r.left, h=r.bottom - r.top,
                            style=st, parent=par,
                            px=pr.left, py=pr.top, pw=pr.right - pr.left,
                            ph=pr.bottom - pr.top))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)


print("=" * 72)
print("A/B) settings dialog focus + label reachability")
print("=" * 72)
kill()
p1 = subprocess.Popen([str(EXE), "--tray"])
time.sleep(2.8)
subprocess.Popen([str(EXE)])
time.sleep(3.2)

s = None
for w in wins(p1.pid):
    if "设置" in w["text"] and w["vis"]:
        s = w
print("dialog:", s["x"], s["y"], s["w"], "x", s["h"])
d_bottom, d_top = s["y"] + s["h"], s["y"]

labels = [l for l in children(s["hwnd"], "STATIC") if l["text"].strip() and l["w"] > 0]
print("labels found:", len(labels))
outside = [l for l in labels if l["y"] + l["h"] > d_bottom + 2 or l["y"] < d_top - 2]
print("outside dialog:", len(outside))

# Is each outside label inside a scrollable ancestor (i.e. reachable by scrolling)?
def scrollable_ancestor(lbl):
    h = lbl["parent"]
    for _ in range(12):
        if not h:
            return None
        st = u.GetWindowLongW(h, -16)
        if st & (WS_VSCROLL | WS_HSCROLL):
            c = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, c, 256)
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            return dict(hwnd=h, cls=c.value, scroll=bool(st & WS_VSCROLL),
                        y=r.top, h=r.bottom - r.top)
        h = u.GetParent(h)
    return None


reach = 0
unreach = []
for l in outside:
    a = scrollable_ancestor(l)
    if a is not None:
        reach += 1
    else:
        unreach.append(l)
print("  reachable via a scrollable ancestor:", reach)
print("  genuinely unreachable             :", len(unreach))
for l in unreach[:10]:
    print("     UNREACHABLE", repr(l["text"][:26]), "y=", l["y"], "h=", l["h"])

# Internal clipping: is each label's own box tall enough for its font?
print()
print("--- internal text clipping (box height vs measured text height) ---")
hdc = u.GetDC(s["hwnd"])
worst = []
for l in labels:
    # Measure with the dialog's default UI font is not directly available; use
    # the box height itself as the signal: a WinForms Label with AutoSize=false
    # that is too short clips its glyphs. Flag boxes shorter than 14px, which no
    # legible CJK label can be at 192 DPI.
    if l["h"] < 14:
        worst.append(l)
print("  labels shorter than 14px tall:", len(worst))
for l in worst[:10]:
    print("     SHORT", repr(l["text"][:26]), "h=", l["h"])
u.ReleaseDC(s["hwnd"], hdc)

# B) hotkey box focus
print()
print("--- hotkey box focus ---")
edits = [e for e in children(s["hwnd"], "EDIT") if e["w"] > 0]
edits.sort(key=lambda e: (e["y"], e["x"]))
print("edit boxes:", len(edits))
target = edits[0]
print("target box:", target["x"], target["y"], target["w"], "x", target["h"],
      "text=", repr(target["text"]))
u.SetForegroundWindow(s["hwnd"])
time.sleep(0.4)
cx, cy = target["x"] + target["w"] // 2, target["y"] + target["h"] // 2
u.SetCursorPos(cx, cy)
time.sleep(0.2)
u.mouse_event(0x0002, 0, 0, 0, 0)
u.mouse_event(0x0004, 0, 0, 0, 0)
time.sleep(0.4)
foc = u.GetFocus()
print("focus after click:", foc, "| target hwnd:", target["hwnd"],
      "| focus == target:", foc == target["hwnd"])
print("text after click (OnEnter should show a prompt):", repr(
    ctypes.create_unicode_buffer(128).value or ""))
b = ctypes.create_unicode_buffer(128)
u.GetWindowTextW(target["hwnd"], b, 128)
print("  read-back:", repr(b.value))

kill()
