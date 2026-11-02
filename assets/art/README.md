# Guide art

Original decorative artwork for the player guide and the store pages (D113, Phase 9 P9-19). Nothing
here shows the game's interface; the captures in `../media/` do that. No Valheim or Iron Gate marks,
game sprites, game fonts or other mods' artwork.

In the guide, a block piece is a line of its own, `` > **Art: `banner.png`** — alt text ``, like the media slots; a
small piece, such as a status mark in a list, is an inline image `![alt](assets/art/mark-ok.png)`. Packaging
(`scripts/package.py`) points both at the public repository's `assets/art` for the stores.

Each piece has an SVG source (the committed source of truth) and a PNG export for the stores. Export
with `rsvg-convert -o <name>.png <name>.svg` (add `-z 2` for a 2x export). Pieces whose geometry is
generated have their generator under `scripts/art/`; edit there, regenerate, then re-export.

| File | What | Source |
| --- | --- | --- |
| `wordmark.svg` | The YAVSOS word mark: six letters cut after the icon's rune, the Y being the rune itself | `scripts/art/wordmark.py` |
| `name-block.svg` | The full name, Marcellus capitals as paths, two lines flush to the word mark's width, initials lit | `scripts/art/nameblock.py` |
| `sign.svg` | The wooden sign: the panel (planks, grain, knots, fibre noise, nails) carrying the word mark and the name block | `scripts/art/panel.py` |
| `banner.svg` | The title banner, 1280x320: slate tile, three chests after the icon's (the rune on the front one), the sign on the right | `scripts/art/banner.py` |
| `divider-left.svg`, `divider-right.svg` | Section dividers, 1280x80, transparent: a band of cut marks fading away from the icon's chest, anchored at a quarter or three quarters; the guide alternates them down the page | `scripts/art/divider.py` |

| `ornament.svg` | Closing ornament, 480x110, transparent: the rune between two cuts that taper away, for the end of the guide | `scripts/art/ornament.py` |
| `mark-ok.svg`, `mark-caution.svg`, `mark-broken.svg`, `mark-off.svg` | The four status marks, 24x24, one image per bullet of the guide's "Status marks" list; drawn from the panel's own disc, glyph grid and colours (the one piece that shows the interface; redraw if `PanelArt` or the colours change) | `scripts/art/marks.py` |

Regenerate in order (each imports the one before): `wordmark.py`, `nameblock.py`, `panel.py`, `banner.py`; `divider.py` needs only `wordmark.py`, and `ornament.py` needs `divider.py`.

Colours follow the icon: cream `#f1e6c8`, groove `#2a160a`, wood `#7f4a25` to `#5a2f14`, slate `#1e2126`.

## Fonts

`fonts/` holds the typefaces the generators read, each with its licence. The generated SVGs contain
paths, not text, so nothing here is needed to view or package them.

| Font | Used for | Licence |
| --- | --- | --- |
| Marcellus (Brian J. Bonislawsky, Astigmatic) | the name block | SIL Open Font License 1.1, `fonts/Marcellus-OFL.txt` |
