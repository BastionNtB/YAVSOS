# YAVSOS icon

Original artwork for YAVSOS, drawn as vectors in October 2026 (maintainer direction, agent
drafting). Not derived from any other mod's artwork.

| File | Use |
| --- | --- |
| `yavsos-icon.svg` | Source of truth. Edit this, then re-export. |
| `icon.png` | 256×256, the file Thunderstore and Hexium packages need as `icon.png`. |
| `yavsos-icon-1024.png` | 1024×1024 for the Nexus page and any large use. |

Design: the Valheim chest silhouette (wide, flaring toward the floor, overhanging lid, centre
ring with hasp, side rings as flat bumps) with a hand-drawn fork rune carved over the ring.
The fork is the first letter of YAVSOS and one path splitting in two, which is what sorting
does.

Re-export:

```sh
rsvg-convert -w 256 -h 256 assets/icon/yavsos-icon.svg -o assets/icon/icon.png
rsvg-convert -w 1024 -h 1024 assets/icon/yavsos-icon.svg -o assets/icon/yavsos-icon-1024.png
```
