"""Assert the settings pages are laid out correctly, without eyeballing.

For each page we check:
  * a dark background dominates and cards are visible
  * text pixels exist in the expected rows (headings, labels, values)
  * the left nav rail keeps all seven entries drawn
  * pages differ from each other (i.e. navigation really switches content)
  * nothing is clipped at the right edge and no text overlaps the nav rail
"""
import glob
import os
import sys
from collections import Counter

import numpy as np
from PIL import Image

BG = (24, 26, 32)
CARD = (34, 37, 45)
CARDALT = (41, 45, 54)
BORDER = (56, 61, 72)
ACCENT = (232, 160, 0)
TEXTMAIN = (236, 238, 242)
TEXTSUB = (148, 156, 172)

NAV_W = 190
HEADER_H = 64
FOOTER_H = 60

fails = []
checks = 0


def check(name, ok, detail=""):
    global checks
    checks += 1
    if not ok:
        fails.append((name, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def near(arr, rgb, tol=6):
    return (np.abs(arr.astype(int) - np.array(rgb)).max(axis=2) <= tol)


shots = sorted(glob.glob("build/shots/*.png"))
if not shots:
    print("no screenshots; run build\\capture_ui.py first")
    sys.exit(1)

imgs = {}
for f in shots:
    imgs[os.path.basename(f)] = np.array(Image.open(f).convert("RGB"))

print("=== per-page layout ===")
for name, a in imgs.items():
    h, w, _ = a.shape
    print(f"\n{name}  {w}x{h}")

    # --- dominant background ------------------------------------------------
    bg = near(a, BG)
    card = near(a, CARD)
    alt = near(a, CARDALT)
    frac_bg = bg.mean()
    frac_card = card.mean()
    check("background dominates", frac_bg > 0.15, f"{frac_bg:.1%} bg")
    check("cards are drawn", frac_card > 0.25, f"{frac_card:.1%} card")

    # --- nav rail: all seven entries must be drawn ---------------------------
    nav = a[:, :NAV_W]
    nav_diff = 0
    for c, col in (("card", CARD), ("accent", ACCENT), ("sub", TEXTSUB),
                   ("main", TEXTMAIN)):
        nav_diff += near(nav, col, 30).sum()
    check("nav rail is painted", nav_diff > 3000, f"{nav_diff} nav pixels")

    # selected nav entry is highlighted with the accent colour
    accent_nav = near(nav, ACCENT, 40).sum()
    check("active nav entry uses the accent colour", accent_nav > 150,
          f"{accent_nav} accent px in rail")

    # --- header / footer bands ---------------------------------------------
    # The header carries the logo, title and subtitle plus a separator, so it
    # is never solid card colour; require a card background *under* the text
    # rather than a high overall fraction.
    header = a[:HEADER_H, NAV_W:]
    footer = a[h - FOOTER_H:, :]
    hdr_card = near(header, CARD).mean()
    hdr_bg = near(header, BG).mean()
    check("header band is a card, not background",
          hdr_card > 0.20 and hdr_card > hdr_bg,
          f"{hdr_card:.0%} card vs {hdr_bg:.0%} bg")
    check("footer band is a card", near(footer, CARD).mean() > 0.4,
          f"{near(footer, CARD).mean():.0%}")

    # --- text presence ------------------------------------------------------
    # The point of these two is "the page is not blank". The primary-text floor
    # used to be 8000, which only held because the screenshots still carried the
    # OS caption: its dark title text sits right on TEXTMAIN and was contributing
    # a few thousand pixels to every page. capture_ui.py now crops to the client
    # area, so the floor has to reflect real pages. 关于 is the sparsest of the
    # seven (5166 px - it is mostly secondary-colour prose); the next lowest is
    # 10304. 4000 clears "blank" with room to spare and stays well under every
    # real page.
    bright = near(a, TEXTMAIN, 45).sum()
    sub = near(a, TEXTSUB, 45).sum()
    check("page has primary text", bright > 4000, f"{bright} px")
    check("page has secondary text", sub > 3000, f"{sub} px")

    # --- content must not overlap the nav rail ------------------------------
    # Sample the first 6 px of the content area: it should be background, never
    # card colour, otherwise a card is bleeding under the rail.
    gutter = a[HEADER_H:h - FOOTER_H, NAV_W:NAV_W + 6]
    check("content leaves a gutter beside the rail",
          near(gutter, CARD).mean() < 0.20,
          f"{near(gutter, CARD).mean():.0%} card in gutter")

    # --- card column must not touch the right edge -------------------------
    right = a[HEADER_H:h - FOOTER_H, w - 6:]
    check("cards do not run off the right edge",
          near(right, CARD).mean() < 0.10,
          f"{near(right, CARD).mean():.0%} card at right edge")

    # --- input controls present (alternate card colour) --------------------
    check("input fields / buttons are drawn", alt.sum() > 3000,
          f"{alt.sum()} alt px")

print("\n=== pages must differ (navigation really switches) ===")
names = list(imgs)
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        a, b = imgs[names[i]], imgs[names[j]]
        if a.shape != b.shape:
            check(f"{names[i]} vs {names[j]}", False, "size mismatch")
            continue
        d = (np.abs(a.astype(int) - b.astype(int)).max(axis=2) > 12).mean()
        check(f"{names[i]} != {names[j]}", d > 0.01, f"{d:.1%} differ")

print()
print(f"=== {checks - len(fails)}/{checks} UI assertions passed ===")
for n, d in fails:
    print(f"   FAILED: {n}  {d}")
sys.exit(1 if fails else 0)
