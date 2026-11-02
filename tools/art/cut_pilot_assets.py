#!/usr/bin/env python3
"""Cut stand-in art for the companion art pilot (issue #291) from the artist's reference sheets in art/reference/.

These are placeholders until the artist delivers. They are cut from the artist's own prototypes so the pilot can run
end to end in Unity. Outputs are named by content address (specs/07 section 15.2) and written to
unity/HeroCatClient/Assets/Resources/Art/<Kind>/:

  Portraits/portrait_<id>.png  256 x 256 rp crop of the splash art (illustrated)
  Icons/icon_<id>.png          128 x 128 face crop (illustrated)
  Splash/art_splash_<id>.png   1080 x 1400 rp splash without the baked-in text (illustrated; L and M only, 00 decision 108)
  Pixel/spr_<id>.png           battle chibi at its native pixel size (sampled on the art's own pixel grid), transparent,
                               32-colour palette, 1 frame

Usage: python3 tools/art/cut_pilot_assets.py [--preview DIR]
Needs Pillow and numpy. Reads only art/reference/.
"""
import argparse
import os
from collections import deque

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REF = os.path.join(ROOT, "art", "reference")
OUT = os.path.join(ROOT, "unity", "HeroCatClient", "Assets", "Resources", "Art")


# Per character, in source pixels:
#   splash: (x0, y0, x1, y1) of the illustration without the title text
#   face:   (cx, cy, r) square around the face for the icon
#   bust:   (x0, y0, size) square for the portrait
#   chibi:  (x0, y0, x1, feetY, pedestalTopY) in the crop whose origin is (60, 0.45 * height)
#   grid:   source pixels per art pixel of the chibi (measured; the AI-drawn grid is not exact)
PILOT = {
    "comp_l_01": dict(src="lucy.jpeg",      splash=(600, 0, 1340, 960),  face=(1010, 345, 120), bust=(820, 150, 420),
                      chibi=(20, 10, 360, 332, 285), grid=6.3),
    "comp_l_04": dict(src="valeria.jpeg",   splash=(560, 0, 1342, 1014), face=(1005, 320, 120), bust=(790, 60, 430),
                      chibi=(40, 30, 345, 352, 300), grid=5.6),
    "comp_m_01": dict(src="seraphine.jpeg", splash=(580, 0, 1362, 1014), face=(1000, 345, 120), bust=(790, 90, 420),
                      chibi=(20, 20, 375, 358, 300), grid=5.35),
}


def flood_background(im, tol=22):
    """Background = everything reachable from the top, left and right edges through small colour steps.
    The reference backgrounds are blurred, the chibis have hard dark outlines, so the fill stops at the outline."""
    h, w, _ = im.shape
    bg = np.zeros((h, w), bool)
    q = deque()
    for x in range(w):
        bg[0, x] = True
        q.append((0, x))
    for y in range(h):
        for x in (0, w - 1):
            bg[y, x] = True
            q.append((y, x))
    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and not bg[ny, nx] and np.abs(im[ny, nx] - im[y, x]).sum() < tol:
                bg[ny, nx] = True
                q.append((ny, nx))
    return bg


def largest_component(mask):
    h, w = mask.shape
    seen = np.zeros_like(mask)
    best = []
    for sy in range(h):
        for sx in range(w):
            if mask[sy, sx] and not seen[sy, sx]:
                comp, q = [], deque([(sy, sx)])
                seen[sy, sx] = True
                while q:
                    y, x = q.popleft()
                    comp.append((y, x))
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ny, nx = y + dy, x + dx
                        if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                            seen[ny, nx] = True
                            q.append((ny, nx))
                if len(comp) > len(best):
                    best = comp
    out = np.zeros_like(mask)
    for y, x in best:
        out[y, x] = True
    return out


def cut_chibi(src, box, grid):
    x0, y0, x1, feet, ped = box
    oy = int(src.height * 0.45)
    im = np.asarray(src.crop((60 + x0, oy + y0, 60 + x1, oy + feet + 40))).astype(int)
    keep = ~flood_background(im)
    fy, py = feet - y0, ped - y0
    keep[fy:, :] = False
    r, g, b = im[..., 0], im[..., 1], im[..., 2]
    gold = (r > 150) & (g > 100) & (b < 120) & (r > b + 60)   # the pedestal's top surface around the feet
    band = np.zeros_like(keep)
    band[py:fy, :] = True
    keep &= ~(band & gold)
    keep = largest_component(keep)
    # Sample the centre of each art pixel (box-averaging smears the outlines), then a small palette.
    ys, xs = np.where(keep)
    ya, yb, xa, xb = ys.min(), ys.max(), xs.min(), xs.max()
    nh, nw = int(round((yb - ya + 1) / grid)), int(round((xb - xa + 1) / grid))
    cy = (ya + (np.arange(nh) + 0.5) * grid).astype(int).clip(0, im.shape[0] - 1)
    cx = (xa + (np.arange(nw) + 0.5) * grid).astype(int).clip(0, im.shape[1] - 1)
    rgb = Image.fromarray(im[np.ix_(cy, cx)].astype(np.uint8))
    rgb = rgb.quantize(colors=32, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).convert("RGB")
    alpha = np.where(keep[np.ix_(cy, cx)], 255, 0).astype(np.uint8)
    return Image.fromarray(np.dstack([np.asarray(rgb), alpha]), "RGBA")


def square(src, cx, cy, r, size):
    return src.crop((cx - r, cy - r, cx + r, cy + r)).resize((size, size), Image.LANCZOS)


def save(img, kind, name):
    d = os.path.join(OUT, kind)
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, name + ".png")
    img.save(p, optimize=True)
    return os.path.relpath(p, ROOT)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--preview", help="also write <id>_chibi_x8.png previews to this directory")
    args = ap.parse_args()
    for cid, c in PILOT.items():
        src = Image.open(os.path.join(REF, c["src"])).convert("RGB")
        x0, y0, x1, y1 = c["splash"]
        splash = src.crop((x0, y0, x1, y1)).resize((1080, 1400), Image.LANCZOS)
        bx, by, bs = c["bust"]
        print(save(splash, "Splash", "art_splash_" + cid))
        print(save(src.crop((bx, by, bx + bs, by + bs)).resize((256, 256), Image.LANCZOS), "Portraits", "portrait_" + cid))
        cx, cy, r = c["face"]
        print(save(square(src, cx, cy, r, 128), "Icons", "icon_" + cid))
        chibi = cut_chibi(src, c["chibi"], c["grid"])
        print(save(chibi, "Pixel", "spr_" + cid), chibi.size)
        if args.preview:
            os.makedirs(args.preview, exist_ok=True)
            big = chibi.resize((chibi.width * 8, chibi.height * 8), Image.NEAREST)
            bg = Image.new("RGBA", big.size, (18, 18, 18, 255))
            bg.alpha_composite(big)
            bg.convert("RGB").save(os.path.join(args.preview, cid + "_chibi_x8.png"))


if __name__ == "__main__":
    main()
