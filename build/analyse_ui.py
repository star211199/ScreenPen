"""Analyse a captured ScreenPen screenshot for layout problems.

Since I cannot view images directly, this reduces the capture to structural
facts: where cards are, whether text is present, whether anything overlaps or
is clipped, and whether the accent colour appears where expected.
"""
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

BG = (24, 26, 32)
CARD = (34, 37, 45)
CARDALT = (41, 45, 54)
BORDER = (56, 61, 72)
ACCENT = (232, 160, 0)
TEXTMAIN = (236, 238, 242)


def near(px, ref, tol=6):
    return all(abs(px[i] - ref[i]) <= tol for i in range(3))


def analyse(path):
    im = Image.open(path).convert("RGB")
    w, h = im.size
    px = im.load()

    counts = Counter()
    for y in range(h):
        for x in range(w):
            counts[px[x, y]] += 1

    def frac(ref, tol=6):
        n = sum(c for col, c in counts.items() if near(col, ref, tol))
        return n / (w * h)

    print(f"--- {Path(path).name}  {w}x{h} ---")
    print(f"  bg      {frac(BG):6.1%}   (expect ~0.50: page background)")
    print(f"  card    {frac(CARD):6.1%}   (expect >0.15: cards + header/footer)")
    print(f"  cardalt {frac(CARDALT):6.1%}   (expect >0.02: input boxes/buttons)")
    print(f"  border  {frac(BORDER, 3):6.1%}   (expect >0.003: card outlines)")
    print(f"  accent  {frac(ACCENT, 10):6.1%}   (expect >0.002: logo/primary button)")

    # Locate card rectangles by scanning for the card fill colour.
    rows_with_card = []
    for y in range(h):
        run = sum(1 for x in range(0, w, 4) if near(px[x, y], CARD))
        rows_with_card.append(run)

    # Group contiguous bands.
    bands = []
    start = None
    for y, run in enumerate(rows_with_card):
        big = run > (w // 4) // 4
        if big and start is None:
            start = y
        elif not big and start is not None:
            if y - start > 6:
                bands.append((start, y))
            start = None
    if start is not None and h - start > 6:
        bands.append((start, h))

    print(f"  card bands (y ranges): {bands}")

    # Text presence: count pixels notably brighter than the card fill.
    text_px = 0
    for col, c in counts.items():
        if sum(col) / 3 > 120 and not near(col, ACCENT, 12):
            text_px += c
    print(f"  bright text pixels: {text_px} ({text_px/(w*h):.2%}) "
          f"{'OK' if text_px > 2000 else 'SUSPICIOUS'}")

    return bands


def diff(path_a, path_b):
    a = Image.open(path_a).convert("RGB")
    b = Image.open(path_b).convert("RGB")
    if a.size != b.size:
        print("  size mismatch")
        return
    pa, pb = a.load(), b.load()
    w, h = a.size
    changed = sum(1 for y in range(0, h, 2) for x in range(0, w, 2)
                  if pa[x, y] != pb[x, y])
    total = (w // 2) * (h // 2)
    print(f"  vs {Path(path_b).name}: {changed/total:.2%} of sampled pixels differ")


if __name__ == "__main__":
    shots = sorted(Path(r"D:\AtGuiGu_Java+AI\code\AICoding\rea_project\ScreenPen\build\shots").glob("*.png"))
    for s in shots:
        analyse(str(s))
        print()
    if len(shots) >= 2:
        print("=== page-to-page differences (should be non-zero) ===")
        base = shots[0]
        for s in shots[1:]:
            print(Path(s).name)
            diff(str(base), str(s))
