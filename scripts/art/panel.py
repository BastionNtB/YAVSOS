#!/usr/bin/env python3
"""Generate the YAVSOS sign, assets/art/sign.svg (D113, Phase 9 P9-19): the wooden panel carrying the
word mark (scripts/art/wordmark.py) and the name block (scripts/art/nameblock.py).

The wood is drawn richer than the chests on purpose: three planks with their own tone and gradient,
procedural grain that bends around three knots, a fibre-noise overlay (feTurbulence), bevelled seams and
edge, a vignette, four iron nails. The name gets a soft shadow for contrast against the grain. All of it
is deterministic (seeded), so regenerating gives the same picture. scripts/art/banner.py composes this
sign into the title banner; `defs()` and `body()` are what it uses.

Usage: scripts/art/panel.py [--out DIR]"""
import argparse
import math
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import nameblock  # noqa: E402
import wordmark  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent.parent
PAD_X, PAD_TOP, PAD_BOTTOM, BETWEEN = 56, 44, 44, 34
NAME_SVG, NAME_H = nameblock.block()
W = wordmark.WIDTH + 2 * PAD_X                                     # 818
H = PAD_TOP + wordmark.HEIGHT + BETWEEN + NAME_H + PAD_BOTTOM      # about 329
PLANKS = 3
PH = H / PLANKS
SEED = 7
GRAIN_LINES = 13
FIBRE_OPACITY = 0.22
SHADOW_OPACITY, SHADOW_BLUR, SHADOW_DX, SHADOW_DY = 0.75, 2.2, 1.5, 2.5
# per-plank tones: top highlight, body, bottom shade
TONES = [("#8c5631", "#7a4823", "#5b3016"), ("#956038", "#84512a", "#62351a"), ("#86512c", "#70401e", "#532b12")]
KNOTS = [(0.78 * W, 0.5 * PH, 16), (0.22 * W, 1.52 * PH, 13), (0.58 * W, 2.42 * PH, 11)]


def _grain_line(y0, plank, amp, wavelength, phase, n=60):
    pts = []
    for i in range(n + 1):
        x = W * i / n
        y = y0 + amp * math.sin(x / wavelength + phase) + 0.4 * amp * math.sin(x / (wavelength * 0.37) + phase * 2.1)
        for kx, ky, kr in KNOTS:
            if int(ky // PH) != plank:
                continue
            d = y0 - ky
            if abs(d) < 0.5:
                d = 0.5
            bulge = (kr * 1.6) / (abs(d) + kr * 0.6) * kr * 0.9
            y += (1 if d > 0 else -1) * bulge * math.exp(-((x - kx) / (kr * 2.2)) ** 2)
        pts.append(f"{x:.1f} {y:.1f}")
    return "M" + " L".join(pts)


def _plank(i, rng):
    top = i * PH
    hi, body, lo = TONES[i]
    grad = (f'<linearGradient id="plank{i}" x1="0" y1="{top:.1f}" x2="0" y2="{top + PH:.1f}" gradientUnits="userSpaceOnUse">'
            f'<stop offset="0" stop-color="{hi}"/><stop offset="0.35" stop-color="{body}"/><stop offset="1" stop-color="{lo}"/></linearGradient>')
    out = [f'<rect x="0" y="{top:.1f}" width="{W}" height="{PH:.1f}" fill="url(#plank{i})"/>']
    for j in range(GRAIN_LINES):
        y0 = top + PH * (0.06 + 0.88 * (j + rng.random() * 0.6) / GRAIN_LINES)
        amp = rng.uniform(1.2, 3.2)
        d = _grain_line(y0, i, amp, rng.uniform(140, 320), rng.uniform(0, 6.3))
        dark = rng.random() < 0.72
        out.append(f'<path d="{d}" fill="none" stroke="{"#4a2810" if dark else "#a56d3f"}" '
                   f'stroke-width="{rng.uniform(0.7, 1.9):.1f}" opacity="{rng.uniform(0.25, 0.6) if dark else rng.uniform(0.18, 0.35):.2f}"/>')
    for kx, ky, kr in KNOTS:
        if int(ky // PH) == i:
            out.append(f'<ellipse cx="{kx:.1f}" cy="{ky:.1f}" rx="{kr * 1.15:.1f}" ry="{kr * 0.75:.1f}" fill="#4a2810" opacity="0.55"/>'
                       f'<ellipse cx="{kx:.1f}" cy="{ky:.1f}" rx="{kr * 0.7:.1f}" ry="{kr * 0.45:.1f}" fill="#3a1e0a" opacity="0.7"/>'
                       f'<ellipse cx="{kx:.1f}" cy="{ky:.1f}" rx="{kr * 1.6:.1f}" ry="{kr * 1.05:.1f}" fill="none" stroke="#4a2810" stroke-width="1.2" opacity="0.5"/>'
                       f'<ellipse cx="{kx - kr * 0.25:.1f}" cy="{ky - kr * 0.2:.1f}" rx="{kr * 0.3:.1f}" ry="{kr * 0.18:.1f}" fill="#8c5631" opacity="0.5"/>')
    return grad, "\n".join(out)


def _planks():
    rng = random.Random(SEED)
    grads, bodies = [], []
    for i in range(PLANKS):
        g, b = _plank(i, rng)
        grads.append(g)
        bodies.append(b)
    return "\n".join(grads), "\n".join(bodies)


_GRADS, _BODIES = _planks()


def defs():
    """Gradients, clip, filters: put once inside <defs> of any document that draws body()."""
    return f'''<clipPath id="signPanel"><rect width="{W}" height="{H:.1f}" rx="10"/></clipPath>
<filter id="signFibre" x="0" y="0" width="1" height="1">
 <feTurbulence type="fractalNoise" baseFrequency="0.010 0.28" numOctaves="3" seed="11" result="n"/>
 <feColorMatrix in="n" type="matrix" values="0 0 0 0 0.62  0 0 0 0 0.40  0 0 0 0 0.22  0 0 0 0.75 0"/>
</filter>
<filter id="signSoft" x="-5%" y="-20%" width="110%" height="140%"><feGaussianBlur stdDeviation="{SHADOW_BLUR}"/></filter>
<radialGradient id="signVignette" cx="50%" cy="45%" r="70%"><stop offset="0.5" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity="0.35"/></radialGradient>
{_GRADS}'''


def body():
    """The sign in its own coordinates, 0..W by 0..H, with its drop shadow."""
    seams = "".join(f'<path d="M0 {i * PH:.1f} H{W}" stroke="#2e170a" stroke-width="3"/>'
                    f'<path d="M0 {i * PH + 2.2:.1f} H{W}" stroke="#a56d3f" stroke-width="1" opacity="0.35"/>' for i in range(1, PLANKS))
    nails = "".join(f'<g transform="translate({x} {y:.1f})"><circle r="6" fill="#43464d" stroke="#1d1f23" stroke-width="1.2"/>'
                    f'<circle r="2.2" cx="-1.5" cy="-1.5" fill="#9a9ea6"/></g>'
                    for x, y in [(22, 22), (W - 22, 22), (22, H - 22), (W - 22, H - 22)])
    shadow_text = NAME_SVG.replace(nameblock.CREAM, wordmark.GROOVE).replace(nameblock.TAN, wordmark.GROOVE)
    return f'''<rect x="6" y="9" width="{W}" height="{H:.1f}" rx="10" fill="#000" opacity="0.4"/>
<g clip-path="url(#signPanel)">
{_BODIES}
<rect width="{W}" height="{H:.1f}" filter="url(#signFibre)" opacity="{FIBRE_OPACITY}" style="mix-blend-mode:multiply"/>
{seams}
<rect width="{W}" height="{H:.1f}" fill="url(#signVignette)"/>
</g>
<rect width="{W}" height="{H:.1f}" rx="10" fill="none" stroke="#3a1e0a" stroke-width="5"/>
<rect x="3" y="3" width="{W - 6}" height="{H - 6:.1f}" rx="8" fill="none" stroke="#a56d3f" stroke-width="1.2" opacity="0.4"/>
{nails}
<g transform="translate({PAD_X} {PAD_TOP})">
{wordmark.word()}
<g transform="translate(0 {wordmark.HEIGHT + BETWEEN})">
<g filter="url(#signSoft)" opacity="{SHADOW_OPACITY}" transform="translate({SHADOW_DX} {SHADOW_DY})">
{shadow_text}
</g>
{NAME_SVG}
</g>
</g>'''


def svg():
    m = 14  # room for the drop shadow
    return f'''<?xml version="1.0" encoding="UTF-8"?>
<!-- YAVSOS sign: the wooden panel with the word mark and the name block. Original artwork, generated by
     scripts/art/panel.py (texture dials and seed there). -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="-{m} -{m} {W + 2 * m} {H + 2 * m:.1f}" width="{W + 2 * m}" height="{H + 2 * m:.1f}">
 <defs>
{defs()}
 </defs>
{body()}
</svg>
'''


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--out", default=str(ROOT / "assets/art"), help="output directory (default assets/art)")
    args = parser.parse_args()
    out = Path(args.out) / "sign.svg"
    out.write_text(svg())
    print(f"{out} ({W}x{H:.0f})")
