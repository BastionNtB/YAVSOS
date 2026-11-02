#!/usr/bin/env python3
"""Generate the YAVSOS word mark, assets/art/wordmark.svg (D113, Phase 9 P9-19).

Six letters cut the way the mod icon's rune is. The Y is the rune itself (assets/icon/yavsos-icon.svg).
Straight strokes are tapered, bowed quads with slanted end cuts, built with the rune's own measurements:
17-20 icon units wide, tips cut nearly square to the page, one side straight and one bowed. The O is a
brush O: two curved strokes, thin at the tips, overlapping at the top and bottom. The two S's are
different cuts on purpose. Each stroke is its own path (two strokes that wind opposite ways cancel
where they overlap if joined into one path).

The SVG is the committed source; this script is where the letters are edited. Export the PNG with
rsvg-convert (see assets/art/README.md). Usage: scripts/art/wordmark.py [--out DIR]"""
import argparse
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
RUNE = ("M136.5 212.4 Q139.3 178.7 135.4 145.2 L118.9 155.0 Q120.7 179.2 121.2 203.4 Z M137.8 152.9 Q117.4 131.7 98.0 109.7 "
        "L93.4 125.9 Q103.3 143.9 118.2 157.5 Z M131.9 164.5 Q152.9 142.9 168.7 117.0 L151.9 115.2 Q138.5 131.0 124.1 145.9 Z")
CREAM, GROOVE = "#f1e6c8", "#2a160a"
fmt = lambda p: f"{p[0]:.1f} {p[1]:.1f}"


def stroke(p0, p1, w0, w1, bow=0.0, belly=0.0, cut0=0.0, cut1=0.0, ext=0.0):
    """One chisel stroke from p0 to p1: width w0 at the start, w1 at the end; bow bends the whole stroke
    sideways; belly thickens (+) or thins (-) its middle; cut0/cut1 slant the end cuts (units along the
    stroke); ext runs both ends past their points so strokes meeting at a corner overlap."""
    (x0, y0), (x1, y1) = p0, p1
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    ux, uy = dx / length, dy / length
    nx, ny = -uy, ux
    x0, y0, x1, y1 = x0 - ux * ext, y0 - uy * ext, x1 + ux * ext, y1 + uy * ext
    a0 = (x0 + nx * w0 / 2 + ux * cut0, y0 + ny * w0 / 2 + uy * cut0)
    b0 = (x0 - nx * w0 / 2 - ux * cut0, y0 - ny * w0 / 2 - uy * cut0)
    a1 = (x1 + nx * w1 / 2 + ux * cut1, y1 + ny * w1 / 2 + uy * cut1)
    b1 = (x1 - nx * w1 / 2 - ux * cut1, y1 - ny * w1 / 2 - uy * cut1)
    mx, my = (x0 + x1) / 2, (y0 + y1) / 2
    half = (w0 + w1) / 4
    ca = (mx + nx * (half + bow + belly), my + ny * (half + bow + belly))
    cb = (mx - nx * (half - bow + belly), my - ny * (half - bow + belly))
    return f"M{fmt(a0)} Q{fmt(ca)} {fmt(a1)} L{fmt(b1)} Q{fmt(cb)} {fmt(b0)} Z"


def arc(center, rx, ry, th0, th1, w0, wmid, w1, rot=0.0, n=28):
    """One brush stroke along an ellipse arc from angle th0 to th1 (degrees, 0 = right, 90 = down),
    width w0 at the start, about wmid in the middle, w1 at the end; rot tilts the ellipse."""
    cx, cy = center
    cr, sr = math.cos(math.radians(rot)), math.sin(math.radians(rot))
    outer, inner = [], []
    for i in range(n + 1):
        t = i / n
        th = math.radians(th0 + (th1 - th0) * t)
        w = (1 - t) ** 2 * w0 + 2 * (1 - t) * t * wmid + t ** 2 * w1
        for lst, off in ((outer, w / 2), (inner, -w / 2)):
            px, py = (rx + off) * math.cos(th), (ry + off) * math.sin(th)
            lst.append((cx + px * cr - py * sr, cy + px * sr + py * cr))
    pts = outer + inner[::-1]
    return "M" + " L".join(fmt(p) for p in pts) + " Z"


# Each letter: strokes in a 120-tall box (baseline 120), and its advance width.
LETTERS = {
    "A": ([stroke((50, 6), (8, 122), 16, 24, bow=4, belly=-2, cut0=4, cut1=8),
           stroke((46, 0), (100, 118), 18, 22, bow=-4, cut0=-5, cut1=9),
           stroke((22, 86), (82, 80), 12, 16, bow=2, cut0=3, cut1=-4)], 100),
    "V": ([stroke((2, 2), (54, 122), 24, 15, bow=5, belly=-2, cut0=7, cut1=5),
           stroke((102, -2), (58, 116), 22, 13, bow=-5, cut0=-7, cut1=-3)], 100),
    # first S: a top hook falling to the left, a steep spine, a bottom hook falling to the left
    "S1": ([stroke((90, 4), (24, 24), 14, 22, bow=-5, cut0=6, ext=2),
            stroke((24, 24), (82, 98), 22, 20, bow=4, belly=-2, ext=3),
            stroke((82, 98), (12, 120), 20, 14, bow=-5, cut1=7, ext=2)], 96),
    # brush O: two curved strokes, thin tips overlapping at the top and bottom, the ellipse tilted a little
    "O": ([arc((49, 62), 40, 54, 278, 84, 10, 34, 11, rot=-7),
           arc((53, 60), 38, 52, -96, 98, 9, 30, 12, rot=-7)], 100),
    # second S: the zigzag, three falling cuts
    "S2": ([stroke((84, 2), (22, 46), 16, 22, bow=3, cut0=-6),
            stroke((20, 44), (90, 80), 22, 20, bow=3, belly=-1),
            stroke((90, 78), (18, 122), 20, 14, bow=3, cut1=7)], 96),
}
Y_ADVANCE = 94
GAP = 24
ORDER = ["A", "V", "S1", "O", "S2"]
WIDTH = Y_ADVANCE + GAP + sum(LETTERS[k][1] + GAP for k in ORDER) - GAP  # 706
HEIGHT = 120


def letter(key, x):
    strokes, adv = LETTERS[key]
    groove = "".join(f'<path d="{d}" fill="{GROOVE}" stroke="{GROOVE}" stroke-width="6" stroke-linejoin="round"/>' for d in strokes)
    cream = "".join(f'<path d="{d}" fill="{CREAM}"/>' for d in strokes)
    return f' <g transform="translate({x} 0)"><g transform="translate(2 3)">{groove}</g>{cream}</g>'


def rune_y(x):
    return (f' <g transform="translate({x} -4) scale(1.24) translate(-93 -110)">'
            f'<path d="{RUNE}" fill="{GROOVE}" stroke="{GROOVE}" stroke-width="6" stroke-linejoin="round" transform="translate(2 3)"/>'
            f'<path d="{RUNE}" fill="{CREAM}"/></g>')


def word():
    out = [rune_y(0)]
    cx = Y_ADVANCE + GAP
    for key in ORDER:
        out.append(letter(key, cx))
        cx += LETTERS[key][1] + GAP
    return "\n".join(out)


def svg():
    # letter box 0..WIDTH x 0..HEIGHT, with room for the cuts past it and the groove shadow
    vx, vy, vw, vh = -8, -16, WIDTH + 24, HEIGHT + 40
    return f'''<?xml version="1.0" encoding="UTF-8"?>
<!-- YAVSOS word mark. Original artwork for the YAVSOS project: six letters cut after the icon's rune,
     the Y being the rune itself. Generated by scripts/art/wordmark.py; edit the letters there. -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="{vx} {vy} {vw} {vh}" width="{vw}" height="{vh}">
{word()}
</svg>
'''


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", default=str(ROOT / "assets/art"), help="output directory (default assets/art)")
    args = parser.parse_args()
    out = Path(args.out) / "wordmark.svg"
    out.write_text(svg())
    print(f"{out} ({WIDTH}x{HEIGHT} letter box)")
