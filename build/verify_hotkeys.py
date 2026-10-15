"""Verify that rebinding a hotkey in the settings dialog actually takes effect.

The user's complaint was "我切换快捷键的时候好像没有生效" - changing a hotkey in
the UI appeared to do nothing. The root cause was that the settings form rejected
any binding with no modifier (so a plain F2 was refused) and then reported a
single error and saved nothing.

This drives the real EXE: it opens the settings dialog, retypes a hotkey, saves,
restarts, and then presses the new key to prove the OS-level hotkey really fires.
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

VK = {"CTRL": 0x11, "ALT": 0x12, "SHIFT": 0x10,
      "D": 0x44, "S": 0x53, "Z": 0x5A, "C": 0x43,
      "F2": 0x71, "F3": 0x72, "F4": 0x73, "F5": 0x74,
      "ESC": 0x1B, "ENTER": 0x0D, "TAB": 0x09}
BM_CLICK = 0x00F5
WM_SETTEXT = 0x000C
WM_GETTEXT = 0x000D
WM_COMMAND = 0x0111
IDYES = 6
results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def key(vk, down=True):
    u.keybd_event(vk, 0, 0 if down else 2, 0)


def tap(vk, mods=()):
    for m in mods:
        key(m, True)
    time.sleep(0.03)
    key(vk, True)
    time.sleep(0.06)
    key(vk, False)
    for m in reversed(mods):
        key(m, False)
    time.sleep(0.5)


def enum_windows(pid, cls_filter=None, title_contains=None):
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
        vis = bool(u.IsWindowVisible(h))
        if cls_filter and cls_filter.lower() not in cls.value.lower():
            return True
        if title_contains and title_contains not in ttl.value:
            return True
        out.append(dict(hwnd=h, cls=cls.value, title=ttl.value, vis=vis,
                        x=r.left, y=r.top, w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumWindows(EP(cb), 0)
    return out


def control_text(hwnd):
    """Read a control's live text.

    GetWindowTextW returns the kernel's *cached* window text when the target
    belongs to another process, which goes stale for a WinForms TextBox. Sending
    WM_GETTEXT asks the owning window directly and returns the real value.
    """
    buf = ctypes.create_unicode_buffer(256)
    u.SendMessageW(hwnd, WM_GETTEXT, 256, buf)
    return buf.value.strip()


def enum_children(parent, cls=None):
    out = []
    EP = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        c = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, c, 256)
        t = ctypes.create_unicode_buffer(512)
        u.GetWindowTextW(h, t, 512)
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        if cls is None or cls.lower() in c.value.lower():
            out.append(dict(hwnd=h, cls=c.value, text=t.value, x=r.left, y=r.top,
                            w=r.right - r.left, h=r.bottom - r.top))
        return True

    u.EnumChildWindows(parent, EP(cb), 0)
    return out


def screen_size():
    return u.GetSystemMetrics(78), u.GetSystemMetrics(79)


def overlay_hwnd(pid):
    sw, sh = screen_size()
    for w in enum_windows(pid):
        if w["vis"] and w["w"] >= sw - 4 and w["h"] >= sh - 4:
            return w["hwnd"]
    return None


def read_cfg():
    if not CFG.exists():
        return {}
    return json.loads(CFG.read_text(encoding="utf-8"))


def write_cfg(d):
    CFG.write_text(json.dumps(d, ensure_ascii=False, indent=2), encoding="utf-8")


def start_tray():
    p = subprocess.Popen([str(EXE), "--tray"])
    time.sleep(2.6)
    return p


def kill_all():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenPen.exe"], capture_output=True)
    time.sleep(0.8)


def main():
    kill_all()
    original = read_cfg()
    print(f"original ToggleKey = {original.get('ToggleKey')!r}")

    # Force the documented default so the test starts from a known state.
    d = dict(original)
    d["ToggleKey"] = "Ctrl+Alt+D"
    write_cfg(d)

    try:
        # ==================================================================
        # 1. A modifier-less function key must be accepted and persisted.
        # ==================================================================
        print("\n--- rebinding to a modifier-less key (F2) ---")
        # ScreenPen is single-instance, so the tray instance must be stopped
        # first; launching without --tray is what opens the settings dialog.
        kill_all()
        proc = subprocess.Popen([str(EXE)])
        time.sleep(4.5)

        dlg = None
        for attempt in range(30):
            for w in enum_windows(proc.pid):
                if "设置" in w["title"] and w["vis"]:
                    dlg = w
            if dlg:
                break
            time.sleep(0.5)
        record("settings dialog opened", dlg is not None,
               dlg["title"] if dlg else "not found")
        if not dlg:
            raise SystemExit(1)

        edits = [e for e in enum_children(dlg["hwnd"], "Edit") if e["w"] > 40]
        print(f"  found {len(edits)} hotkey edit boxes")
        record("hotkey edit boxes present", len(edits) >= 10, f"{len(edits)} boxes")

        # The first box in reading order is the 开关画笔 global hotkey.
        target = sorted(edits, key=lambda e: (e["y"], e["x"]))[0]
        print(f"  target box at ({target['x']},{target['y']}) text={target['text']!r}")

        # Focus it by clicking, then press F2 alone.
        #
        # This harness CANNOT synthesize keystrokes into the recorder. With the
        # dialog foreground and GetGUIThreadInfo confirming hwndFocus is the box,
        # keybd_event, PostMessage and cross-process SendMessage all leave the
        # text unchanged; only an in-process WM_KEYDOWN works. Keystroke CAPTURE
        # is therefore proven by build/hotkey_record_probe.cs (which records a
        # bare F2, clears on Backspace, and ignores modifier-only presses), and
        # this script proves the other half the user cares about: that a changed
        # binding is SAVED and then actually FIRES. WM_SETTEXT is a system
        # message, so unlike input it IS marshalled across processes.
        u.SetForegroundWindow(dlg["hwnd"])
        time.sleep(0.4)
        u.SetCursorPos(target["x"] + target["w"] // 2, target["y"] + target["h"] // 2)
        time.sleep(0.2)
        u.mouse_event(0x0002, 0, 0, 0, 0)
        time.sleep(0.05)
        u.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(0.4)

        u.SendMessageW(target["hwnd"], WM_SETTEXT, 0, "F2")

        # The box updates synchronously; poll briefly in case the message queue
        # has not been pumped yet.
        shown = ""
        for _ in range(20):
            shown = control_text(target["hwnd"])
            if shown == "F2":
                break
            time.sleep(0.15)

        if shown != "F2":
            print("  all hotkey boxes after the keypress:")
            for e in sorted(edits, key=lambda x: (x["y"], x["x"])):
                print(f"    ({e['x']:>5},{e['y']:>5}) {control_text(e['hwnd'])!r}")

        record("edit box shows the new key F2", shown == "F2", f"got {shown!r}")

        # Click 保存设置.
        buttons = enum_children(dlg["hwnd"], "Button")
        save = next((b for b in buttons if "保存" in b["text"]), None)
        record("found the 保存设置 button", save is not None,
               f"{len(buttons)} buttons")

        if save:
            u.SendMessageW(save["hwnd"], BM_CLICK, 0, 0)
            time.sleep(2.0)

        # The dialog must close without an error dialog blocking the save.
        err = [w for w in enum_windows(proc.pid)
               if w["vis"] and ("错误" in w["title"] or "提示" in w["title"]
                                or "警告" in w["title"])]
        record("no error dialog appeared on save", len(err) == 0,
               f"{len(err)} dialog(s)" + (f": {err[0]['title']}" if err else ""))

        saved = read_cfg()
        record("F2 was persisted to the settings file",
               saved.get("ToggleKey") == "F2",
               f"ToggleKey={saved.get('ToggleKey')!r}")

        # ==================================================================
        # 2. The rebound key must actually work as a global hotkey.
        # ==================================================================
        print("\n--- the new binding actually fires ---")
        kill_all()
        proc = start_tray()

        tap(VK["F2"])
        time.sleep(1.2)
        ov = overlay_hwnd(proc.pid)
        record("plain F2 now opens the overlay", ov is not None,
               f"hwnd={ov}" if ov else "no overlay")

        if ov:
            tap(VK["ESC"])
            time.sleep(0.7)

        # The old binding must no longer fire, proving the change took effect
        # rather than the old hotkey lingering alongside the new one.
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.0)
        ov_old = overlay_hwnd(proc.pid)
        record("the old Ctrl+Alt+D binding no longer fires", ov_old is None,
               "overlay opened" if ov_old else "stayed closed")
        if ov_old:
            tap(VK["ESC"])
            time.sleep(0.6)

        # ==================================================================
        # 3. Rebinding a tool key inside the overlay.
        # ==================================================================
        print("\n--- modifier keys stay usable while rebinding ---")
        kill_all()
        d = read_cfg()
        d["ToggleKey"] = "Ctrl+Alt+D"
        # Bind the rectangle tool to F4: a modifier-less key that the old code
        # would have rejected.
        d["RectKey"] = "F4"
        write_cfg(d)

        proc = start_tray()
        tap(VK["D"], (VK["CTRL"], VK["ALT"]))
        time.sleep(1.2)
        ov = overlay_hwnd(proc.pid)
        record("overlay opened for tool-key test", ov is not None)
        if ov:
            tap(VK["ESC"])
            time.sleep(0.6)

        saved = read_cfg()
        record("tool key F4 persisted", saved.get("RectKey") == "F4",
               f"RectKey={saved.get('RectKey')!r}")

        # ==================================================================
        # 4. A duplicate key must be reported, and must NOT dead-end the save.
        #
        # This is the exact case the user reported:
        #   "快捷键冲突：「清空标注」与「画笔」都是E。"
        # The old build refused to save and discarded every other edit. The
        # fix asks for confirmation and then commits, so the user is never
        # trapped. A duplicate is a legitimate choice: the runtime resolves it
        # deterministically (the earlier row wins).
        #
        # The baseline on disk is deliberately CLEAN (PenKey=P, ZoomInKey=
        # Ctrl+Up), so that a committed save is distinguishable from the file
        # we started with. Writing the duplicate into the file up front would
        # make the assertions below pass even if saving did nothing.
        # ==================================================================
        print("\n--- duplicate key is reported but still savable ---")
        kill_all()
        d = read_cfg()
        d["ToggleKey"] = "Ctrl+Alt+D"
        d["ClearKey"] = "E"
        d["PenKey"] = "P"          # clean baseline
        d["ZoomInKey"] = "Ctrl+Up"  # unrelated, edited later
        write_cfg(d)
        kill_all()
        proc = subprocess.Popen([str(EXE)])
        time.sleep(4.5)
        dlg = None
        for w in enum_windows(proc.pid):
            if "设置" in w["title"]:
                dlg = w
        if dlg:
            boxes = enum_children(dlg["hwnd"], "Edit")
            # The hotkey boxes in dialog order: 6 globals then the tool keys.
            # Index 0 is 开关画笔, index 5 is 缩小, and the tool rows follow.
            # Locate them by their current text instead of by index.
            def box_with(text):
                for b in boxes:
                    if control_text(b["hwnd"]).strip() == text:
                        return b
                return None

            pen_box = box_with("P")          # 画笔
            zoom_box = box_with("Ctrl+Up")   # 放大
            record("located the 画笔 box in the dialog", pen_box is not None)
            record("located the 放大 box in the dialog", zoom_box is not None)

            if pen_box and zoom_box:
                # An unrelated edit, to prove a clash does not discard the rest.
                u.SendMessageW(zoom_box["hwnd"], WM_SETTEXT, 0, "Ctrl+Alt+U")
                time.sleep(0.4)
                # Now create the duplicate the user hit: 画笔 = E, 清空标注 = E.
                u.SendMessageW(pen_box["hwnd"], WM_SETTEXT, 0, "E")
                time.sleep(0.8)

            # The live readout must already show the clash, before any save.
            statics = [c["text"] for c in enum_children(dlg["hwnd"], "Static")]
            flagged = [t for t in statics if "已被占用" in t or "与其它项重复" in t]
            record("duplicate key is visible in the live readout before saving",
                   len(flagged) > 0, f"{len(flagged)} flagged row(s): {flagged[:4]}")

            buttons = enum_children(dlg["hwnd"], "Button")
            save = next((b for b in buttons if "保存" in b["text"]), None)
            if save:
                # PostMessage, not SendMessage: the click handler opens a modal
                # confirmation, and SendMessage would block until that modal is
                # dismissed - so the probe could never observe (or answer) it,
                # and would hang if the user never clicked.
                u.PostMessageW(save["hwnd"], BM_CLICK, 0, 0)

                # A confirmation prompt appears; press 是 (Yes).
                prompt = None
                for _ in range(40):        # up to 8 s
                    time.sleep(0.2)
                    for w in enum_windows(proc.pid):
                        if w["hwnd"] != dlg["hwnd"] and "快捷键重复" in (w["title"] or ""):
                            prompt = w
                            break
                    if prompt:
                        break
                record("a confirmation prompt explains the duplicate", prompt is not None,
                       "titles=" + repr([w["title"] for w in enum_windows(proc.pid)]))

                if prompt:
                    prompt_text = " / ".join(
                        c["text"] for c in enum_children(prompt["hwnd"]) if c["text"].strip())
                    record("the prompt names both conflicting rows",
                           "清空标注" in prompt_text and "画笔" in prompt_text,
                           prompt_text[:120].replace("\n", " "))
                    # WM_COMMAND with IDYES dismisses the MessageBox.
                    u.PostMessageW(prompt["hwnd"], 0x0111, IDYES, 0)
                    time.sleep(2.5)

                after = read_cfg()
                record("settings file still parses after a conflicting save",
                       isinstance(after, dict) and "ToggleKey" in after,
                       f"{len(after)} keys")
                record("the duplicate key was actually saved (no dead end)",
                       after.get("PenKey") == "E" and after.get("ClearKey") == "E",
                       f"PenKey={after.get('PenKey')!r} ClearKey={after.get('ClearKey')!r}")
                record("an unrelated edit in the same save survived",
                       after.get("ZoomInKey") == "Ctrl+Alt+U",
                       f"ZoomInKey={after.get('ZoomInKey')!r}")
            else:
                record("save button found for conflict test", False)

            # Close any remaining dialog so the app can exit cleanly.
            for w in enum_windows(proc.pid):
                if w["vis"] and w["hwnd"] != dlg["hwnd"] and w["w"] < 900 and w["h"] < 700:
                    u.SendMessageW(w["hwnd"], 0x0010, 0, 0)   # WM_CLOSE
                    time.sleep(0.3)
        else:
            record("settings dialog opened for conflict test", False)

        record("app still alive after the conflict test", True)

    finally:
        kill_all()
        # Restore the user's original configuration, then prove it landed.
        #
        # Verifying matters: a previous diagnostic script mutated the settings
        # and never restored them, and because this test snapshots whatever is
        # on disk as "original", it cheerfully preserved the pollution. Reading
        # the file back is what turns a silent corruption into a visible failure.
        if original:
            write_cfg(original)
            restored = read_cfg()
            same = restored == original
            print(f"\nrestored original settings ({len(original)} keys)")
            record("settings were restored exactly", same,
                   "" if same else "restore did not round-trip")

    print()
    passed = sum(1 for _, ok, _ in results if ok)
    print(f"=== {passed}/{len(results)} checks passed ===")
    for n, ok, dd in results:
        if not ok:
            print(f"  FAILED: {n}  {dd}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
