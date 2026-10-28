#!/usr/bin/env python3
"""Build the guide and store-page captures in assets/media/ from raw recordings.

Usage: scripts/build-media.py [name ...] [--spec FILE] [--raw DIR] [--out DIR]

The spec (default assets/media/sources.json) maps each output file to its raw source and how to cut it:
  "set-rule.gif": {"source": "set-rule.mp4", "start": 0.5, "end": 13.0,
                   "crop": [w, h, x, y], "width": 960, "fps": 15, "mp4": true}
  "panel-overview.png": {"source": "panel-overview.png", "crop": [w, h, x, y], "width": 1600}
Raw files live in local/media-raw/ (ignored by Git). crop, width, start/end, fps and mp4 are optional.
A .gif output gets a two-pass palette; "mp4": true also writes a same-named .mp4 next to it.
A .png output is cropped, optionally resized and quantised with pngquant.
Needs ffmpeg, ImageMagick (magick) and pngquant."""
import argparse, json, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def run(cmd):
    subprocess.run(cmd, check=True)


def video_filter(c):
    parts = [f"fps={c.get('fps', 15)}"]
    if c.get('crop'):
        w, h, x, y = c['crop']; parts.append(f"crop={w}:{h}:{x}:{y}")
    if c.get('width'):
        parts.append(f"scale={c['width']}:-2:flags=lanczos")
    return ','.join(parts)


def trim(c):
    args = []
    if 'start' in c: args += ['-ss', str(c['start'])]
    if 'end' in c: args += ['-to', str(c['end'])]
    return args


def build_gif(src, out, c):
    # Dropping near-identical frames (mpdecimate, variable frame timing) and an ordered dither keep still parts of the
    # picture byte-identical between frames, which is what keeps a GIF small: 7 MB became 3.3 MB on the dry run.
    vf = video_filter(c) + (",mpdecimate,split[a][b];[a]palettegen=stats_mode=diff[p];"
                            "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle")
    run(['ffmpeg', '-v', 'error', '-y', *trim(c), '-i', str(src), '-filter_complex', vf, '-fps_mode', 'vfr', '-loop', '0', str(out)])
    if c.get('mp4'):
        vf = video_filter({**c, 'fps': c.get('mp4_fps', 30)})
        run(['ffmpeg', '-v', 'error', '-y', *trim(c), '-i', str(src), '-vf', vf, '-an', '-c:v', 'libx264', '-crf', '20',
             '-preset', 'slow', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', str(out.with_suffix('.mp4'))])


def build_png(src, out, c):
    cmd = ['magick', str(src)]
    if c.get('crop'):
        w, h, x, y = c['crop']; cmd += ['-crop', f'{w}x{h}+{x}+{y}', '+repage']
    if c.get('width'):
        cmd += ['-filter', 'Lanczos', '-resize', f"{c['width']}x"]
    run(cmd + [str(out)])
    run(['pngquant', '--force', '--skip-if-larger', '--quality', '85-98', '--ext', '.png', str(out)])


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('names', nargs='*')
    ap.add_argument('--spec', type=Path, default=ROOT / 'assets/media/sources.json')
    ap.add_argument('--raw', type=Path, default=ROOT / 'local/media-raw')
    ap.add_argument('--out', type=Path, default=ROOT / 'assets/media')
    a = ap.parse_args()
    for tool in ('ffmpeg', 'magick', 'pngquant'):
        if not shutil.which(tool): sys.exit(f'missing tool: {tool}')
    spec = json.loads(a.spec.read_text())
    a.out.mkdir(parents=True, exist_ok=True)
    for name in a.names or list(spec):
        c = spec[name]; src = a.raw / c['source']; out = a.out / name
        if not src.exists(): print(f'{name}: skipped, no source {src}'); continue
        (build_gif if out.suffix == '.gif' else build_png)(src, out, c)
        extra = [out.with_suffix('.mp4')] if out.suffix == '.gif' and c.get('mp4') else []
        print(name, ', '.join(f'{p.name} {p.stat().st_size / 1e6:.2f} MB' for p in [out, *extra]))


if __name__ == '__main__':
    main()
