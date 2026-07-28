#!/usr/bin/env python3
"""
Trim the exported-chrome band off menu/tutorial artwork, and flag duplicate exports.

Why this exists
---------------
The design exports for the menu pages come out slightly taller than the artboard,
carrying a strip of mockup chrome along the bottom. Stretched into a UI Image that
strip renders as a grey bar across the bottom of the page. This finds that band by
brightness and crops it off.

It also hashes every file, because an export slip that saves one page's art over
another's is invisible in the Unity inspector -- the sprite name still looks right.

Usage
-----
    python Tools/trim_menu_art.py                 # report only, changes nothing
    python Tools/trim_menu_art.py --apply         # crop, backing originals up first
    python Tools/trim_menu_art.py --apply "Assets/Materials/Menu/Winning.png"

Needs Pillow:  python -m pip install Pillow
"""
import argparse
import glob
import hashlib
import os
import shutil
import sys
from collections import defaultdict
from datetime import datetime

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required:  python -m pip install Pillow")

DEFAULT_GLOB = 'Assets/Materials/Menu/*.png'
# How much brighter than the page body a row must be to count as chrome.
BRIGHTNESS_MARGIN = 12
# Rows in the top portion are sampled to establish what "the page body" looks like.
BODY_SAMPLE = 0.8


def row_stats(im):
    """Mean brightness and alpha for every row, sampled across the width."""
    w, h = im.size
    px = im.load()
    step = max(1, w // 400)
    xs = range(0, w, step)
    out = []
    for y in range(h):
        bright = alpha = 0
        n = 0
        for x in xs:
            r, g, b, a = px[x, y]
            bright += (r + g + b) / 3.0
            alpha += a
            n += 1
        out.append((bright / n, alpha / n))
    return out


def measure_band(im):
    """Height in pixels of the bright/transparent strip along the bottom edge."""
    w, h = im.size
    rows = row_stats(im)
    body = sorted(v for v, _ in rows[: int(h * BODY_SAMPLE)])
    if not body:
        return 0, 0.0
    typical = body[len(body) // 2]
    limit = typical + BRIGHTNESS_MARGIN

    band = 0
    for y in range(h - 1, -1, -1):
        bright, alpha = rows[y]
        if bright > limit or alpha < 250:
            band += 1
        else:
            break
    # A "band" covering most of the image means the page is simply bright; don't crop.
    if band > h * 0.25:
        return 0, typical
    return band, typical


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('files', nargs='*', help=f'PNGs to check (default: {DEFAULT_GLOB})')
    ap.add_argument('--apply', action='store_true', help='actually crop; otherwise report only')
    args = ap.parse_args()

    paths = args.files or sorted(glob.glob(DEFAULT_GLOB))
    if not paths:
        sys.exit(f'no files matched {DEFAULT_GLOB}')

    backup = os.path.join('Tools', '_art_backup', datetime.now().strftime('%Y%m%d-%H%M%S'))
    digests = defaultdict(list)
    planned = []

    print(f'{"file":26s} {"size":12s} {"band":>6s}  result')
    print('-' * 72)

    for path in paths:
        with open(path, 'rb') as fh:
            digests[hashlib.md5(fh.read()).hexdigest()].append(os.path.basename(path))

        im = Image.open(path).convert('RGBA')
        w, h = im.size
        band, _ = measure_band(im)
        name = os.path.basename(path)
        size = f'{w}x{h}'

        if band == 0:
            print(f'{name:26s} {size:12s} {"-":>6s}  clean')
            continue

        newh = h - band
        note = f'-> {w}x{newh}'
        # Landing exactly on 16:9 is a strong sign the band really was export chrome.
        if abs(w / newh - 16 / 9) < 0.02:
            note += '  (lands on 16:9)'
        print(f'{name:26s} {size:12s} {band:>6d}  {note}')
        planned.append((path, im, band))

    dupes = {d: n for d, n in digests.items() if len(n) > 1}
    if dupes:
        print('\nDUPLICATE FILES -- same bytes, so these pages show identical art:')
        for names in dupes.values():
            print('  ' + '  ==  '.join(names))
        print('  Re-export the wrong one; cropping cannot fix it.')

    if not planned:
        print('\nNothing to crop.')
        return

    if not args.apply:
        print(f'\nReport only. Re-run with --apply to crop {len(planned)} file(s).')
        return

    os.makedirs(backup, exist_ok=True)
    for path, im, band in planned:
        shutil.copy2(path, os.path.join(backup, os.path.basename(path)))
        w, h = im.size
        im.crop((0, 0, w, h - band)).save(path, 'PNG', optimize=True)
    print(f'\nCropped {len(planned)} file(s). Originals: {backup}')


if __name__ == '__main__':
    main()
