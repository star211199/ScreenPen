"""Verify the README's documented settings schema matches the real program.

Extracts the JSON block from README.md, feeds it to the app as its settings
file, and confirms the app starts cleanly and echoes the values back. This
catches documentation drift (a documented key the program ignores, or a wrong
default) rather than merely proof-reading the text.
"""
import ctypes
import ctypes.wintypes as wt
import io
import json
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen")
EXE = ROOT / "dist" / "ScreenPen.exe"
SETTINGS = ROOT / "dist" / "ScreenPen.settings.json"
README = ROOT / "README.md"

u = ctypes.windll.user32
try:
    u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass

fails = []


def check(name, ok, detail=""):
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))
    if not ok:
        fails.append(name)


# ---- extract the documented JSON ------------------------------------------
text = io.open(README, encoding="utf-8").read()
blocks = re.findall(r"```jsonc\r?\n(.*?)```", text, re.S)
if not blocks:
    print("FAIL: no jsonc block found in README")
    sys.exit(1)

raw = blocks[0]
# Strip // comments (careful not to touch // inside strings; the schema uses
# none, so a simple line-based strip is safe here).
clean = "\n".join(re.sub(r"//.*$", "", ln) for ln in raw.splitlines())
clean = re.sub(r",(\s*[}\]])", r"\1", clean)      # tolerate trailing commas
try:
    documented = json.loads(clean)
except Exception as e:
    print(f"FAIL: README json block is not valid JSON after comment strip: {e}")
    sys.exit(1)

print(f"documented keys: {len(documented)}")

# ---- the program's real schema --------------------------------------------
# Read field names straight out of Config.cs so the check follows the source.
cfg = io.open(ROOT / "src" / "Config.cs", encoding="utf-8").read()
body = cfg[cfg.index("class Settings"):]
fields = set(re.findall(r"public\s+[\w<>\[\]\.]+\s+(\w+)\s*=", body))
print(f"program keys:    {len(fields)}")

# A setting whose persisted key deliberately differs from its field name.
#
# ScrollFollowClickThrough is stored under ...V2 because the original key was
# written to every settings file (default true) while no code ever read it, so
# honouring that value now would silently make the annotation overlay
# un-drawable during scroll-follow mode. Reading a fresh key lets those installs
# fall back to the safe default. The mismatch is intentional, not drift, so it is
# declared here rather than flagged as a documentation error.
JSON_KEY_OVERRIDES = {"ScrollFollowClickThrough": "ScrollFollowClickThroughV2"}

documented_keys = set(documented)
for field, key in JSON_KEY_OVERRIDES.items():
    if key in documented_keys:
        documented_keys.discard(key)
        documented_keys.add(field)

missing_in_doc = sorted(fields - documented_keys)
extra_in_doc = sorted(documented_keys - fields)

print("\n=== schema comparison ===")
check("README documents every program setting", not missing_in_doc,
      f"undocumented: {missing_in_doc}" if missing_in_doc else "all present")
check("README documents no non-existent setting", not extra_in_doc,
      f"bogus: {extra_in_doc}" if extra_in_doc else "none")

# ---- round-trip through the real app --------------------------------------
print("\n=== round-trip: hand the documented JSON to the app ===")
SETTINGS.parent.mkdir(parents=True, exist_ok=True)

# The app's settings file is the user's live configuration, and this verifier
# has to overwrite it to feed the app the documented schema. Keep a copy and put
# it back at the end: running a check must not silently reset someone's
# preferences, which is exactly the kind of side effect that makes a test
# untrustworthy.
SETTINGS_BACKUP = None
if SETTINGS.exists():
    SETTINGS_BACKUP = Path(str(SETTINGS) + ".verify_readme_backup")
    SETTINGS_BACKUP.write_bytes(SETTINGS.read_bytes())

io.open(SETTINGS, "w", encoding="utf-8").write(json.dumps(documented, ensure_ascii=False, indent=2))

for stale in ("ScreenPen.error.log",):
    p = ROOT / "dist" / stale
    if p.exists():
        p.unlink()

proc = subprocess.Popen([str(EXE), "--tray"], cwd=str(EXE.parent))
time.sleep(4.0)
alive = proc.poll() is None
check("app starts with the documented config", alive)

if not alive:
    # ScreenPen is single-instance: a second launch broadcasts to the first one
    # and exits immediately. A leftover process therefore looks exactly like
    # "the app will not start with the documented config", which sends you
    # hunting for a config bug that is not there. Say what actually happened.
    others = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq ScreenPen.exe", "/NH"],
        capture_output=True, text=True).stdout
    if "ScreenPen.exe" in others:
        print("  NOTE: another ScreenPen.exe is already running. It is "
              "single-instance, so this launch broadcast to it and exited. "
              "Kill it and re-run before believing this failure.")
        print("        " + " ".join(others.split())[:200])

proc.terminate()
try:
    proc.wait(timeout=5)
except Exception:
    proc.kill()
time.sleep(0.6)

errlog = ROOT / "dist" / "ScreenPen.error.log"
check("no crash with the documented config", not errlog.exists(),
      errlog.read_text(encoding="utf-8", errors="replace")[:300] if errlog.exists() else "")

# The app rewrites the file only on save, so compare what it *read* by asking
# the settings UI to save it back, proving every documented key is understood.
proc = subprocess.Popen([str(EXE)], cwd=str(EXE.parent))
time.sleep(4.5)

save_hwnd = []
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

# First locate the settings window itself, then enumerate its children. Walking
# from the desktop root with EnumWindows does not reach child buttons.
settings_wins = []


def find_settings(h, l):
    pid = wt.DWORD()
    u.GetWindowThreadProcessId(h, ctypes.byref(pid))
    if pid.value != proc.pid or not u.IsWindowVisible(h):
        return True
    n = u.GetWindowTextLengthW(h)
    b = ctypes.create_unicode_buffer(n + 2)
    u.GetWindowTextW(h, b, n + 2)
    if "ScreenPen" in b.value and n > 0:
        settings_wins.append(h)
    return True


u.EnumWindows(EnumProc(find_settings), 0)


def find_save(h, l):
    cls = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, cls, 256)
    if "BUTTON" in cls.value:
        n = u.GetWindowTextLengthW(h)
        b = ctypes.create_unicode_buffer(n + 2)
        u.GetWindowTextW(h, b, n + 2)
        if b.value.strip() == "保存设置":
            save_hwnd.append(h)
    return True


for w in settings_wins:
    u.EnumChildWindows(w, EnumProc(find_save), 0)

if not save_hwnd:
    check("settings window opened for round-trip", False,
          f"windows found: {len(settings_wins)}, no Save button")
else:
    u.SendMessageW(save_hwnd[0], 0x00F5, 0, 0)      # BM_CLICK
    time.sleep(2.0)
    check("settings window opened for round-trip", True)

    proc.terminate()
    try:
        proc.wait(timeout=5)
    except Exception:
        proc.kill()
    time.sleep(0.5)

    after = json.loads(io.open(SETTINGS, encoding="utf-8").read())
    # Every documented key must survive the app's own read+write cycle with the
    # same value; a key the program ignored would come back as a default.
    dropped = []
    for k, v in documented.items():
        if k not in after:
            dropped.append(k)
        elif isinstance(v, (str, bool, int, float)) and after[k] != v:
            # floats such as 0.5 legitimately re-serialise; compare loosely
            try:
                if abs(float(after[k]) - float(v)) > 1e-6:
                    dropped.append(f"{k}: {v} -> {after[k]}")
            except Exception:
                dropped.append(f"{k}: {v} -> {after[k]}")
    check("every documented key round-trips through the app", not dropped,
          "; ".join(dropped) if dropped else f"{len(after)} keys rewritten")

    check("palette list round-trips",
          after.get("Palette") == documented.get("Palette"),
          f"{len(after.get('Palette', []))} colours")

print()
total = 7
print(f"=== {total - len(fails)}/{total} README schema checks passed ===")
for f in fails:
    print(f"   FAILED: {f}")

# Put the user's own settings back before reporting. Done last so a failure
# above still cannot leave their configuration replaced by the README sample.
if SETTINGS_BACKUP is not None:
    SETTINGS.write_bytes(SETTINGS_BACKUP.read_bytes())
    SETTINGS_BACKUP.unlink()
    print("restored the previous settings file")

sys.exit(1 if fails else 0)
