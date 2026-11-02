#!/usr/bin/env python3
"""Cut stand-in art for the companion art pilot (issue #291), the companions added after it, and the weapon pilot (issue #290) from the artist's references in art/reference/.

These are placeholders until the artist delivers. They are cut from the artist's own prototypes so the pilot can run
end to end in Unity. Outputs are named by content address (specs/07 section 15.2) and written to
unity/HeroCatClient/Assets/Resources/Art/<Kind>/:

  Portraits/portrait_<id>.png  256 x 256 rp crop of the splash art (illustrated)
  Icons/icon_<id>.png          128 x 128 face crop (illustrated)
  Splash/art_splash_<id>.png   1080 x 1400 rp splash without the baked-in text (illustrated; L and M only, 00 decision 108)
  Pixel/spr_<id>.png           battle chibi at its native pixel size (sampled on the art's own pixel grid), transparent,
                               32-colour palette, 1 frame

and per weapon:

  Icons/icon_<id>.png          128 x 128 crop of the weapon sheet (illustrated)
  Pixel/spr_<id>.png           held sprite, 20 px native on its longest side including a 1 px outline, 12-colour palette

Usage: python3 tools/art/cut_pilot_assets.py [--preview DIR]
Needs Pillow and numpy. Reads only art/reference/ (companions/ and weapons/).
"""
import argparse
import os
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REF = os.path.join(ROOT, "art", "reference")
COMPANION_REF = os.path.join(REF, "companions")
WEAPON_REF = os.path.join(REF, "weapons")
OUT = os.path.join(ROOT, "unity", "HeroCatClient", "Assets", "Resources", "Art")


# Per character, in source pixels:
#   splash: (x0, y0, x1, y1) of the illustration without the title text
#   splash_src: optional dedicated splash reference (art/reference/companions/splash/); then `splash` is in its pixels
#   face:   (cx, cy, r) square around the face for the icon
#   bust:   (x0, y0, size) square for the portrait
#   chibi:  (x0, y0, x1, feetY, pedestalTopY) in the crop whose origin is (60, 0.45 * height)
#   grid:   source pixels per art pixel of the chibi (measured; the AI-drawn grid is not exact)
#   wide_gold: also drop the pedestal's darker shading (only where the character has no gold near the feet)
#   clear:  optional boxes (x0, y0, x1, y1) in the same crop coordinates to drop (background the fill cannot reach)
PILOT = {
    "comp_l_01": dict(src="lucy.jpeg",      splash=(600, 0, 1340, 960),  face=(1010, 345, 120), bust=(820, 150, 420),
                      chibi=(20, 10, 360, 332, 285), grid=6.3),
    "comp_l_04": dict(src="valeria.jpeg",   splash=(560, 0, 1342, 1014), face=(1005, 320, 120), bust=(790, 60, 430),
                      chibi=(40, 30, 345, 352, 300), grid=5.6),
    "comp_m_01": dict(src="seraphine.jpeg", splash=(580, 0, 1362, 1014), face=(1000, 345, 120), bust=(790, 90, 420),
                      chibi=(20, 20, 375, 358, 300), grid=5.35),
    # The splash comes from the artist's vertical splash: everything below the title and the role chips (they end at y 265).
    "comp_l_02": dict(src="yuki.jpg",       splash=(24, 270, 1001, 1536), face=(1005, 315, 120), bust=(790, 90, 430),
                      splash_src="splash/yuki_splash.png",
                      chibi=(-20, 20, 355, 378, 335), grid=5.1, wide_gold=True, clear=[(295, 255, 380, 420)]),
}

# Per weapon, in source pixels:
#   icon:  (x0, y0, size) square for the icon, inside the sheet's frame and below the title
#   blade: (x0, y0, x1, y1) box around the one blade the squire holds (for a pair, the front one)
#   tol:   colour distance from the local background that counts as weapon (the glow around it does not)
WEAPONS = {
    "weapon_e_06": dict(src="twin_crystals.png", icon=(148, 308, 895), blade=(190, 470, 1050, 1215), tol=90),
}

HELD_PX = 20            # 07 section 15.2: held sprite 160 rp = 20 px native
OUTLINE = (28, 18, 44)  # dark violet 1 px outline so the blade reads on any chapter background


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


def cut_chibi(src, box, grid, clear=(), wide_gold=False):
    x0, y0, x1, feet, ped = box
    oy = int(src.height * 0.45)
    im = np.asarray(src.crop((60 + x0, oy + y0, 60 + x1, oy + feet + 40))).astype(int)
    keep = ~flood_background(im)
    fy, py = feet - y0, ped - y0
    keep[fy:, :] = False
    r, g, b = im[..., 0], im[..., 1], im[..., 2]
    gold = (r > 150) & (g > 100) & (b < 120) & (r > b + 60)   # the pedestal's top surface around the feet
    if wide_gold:
        gold |= (r > 120) & (g > 80) & (r > b + 50) & (g > b + 20)   # and its darker shading
    band = np.zeros_like(keep)
    band[py:fy, :] = True
    keep &= ~(band & gold)
    for cx0, cy0, cx1, cy1 in clear:
        keep[max(0, cy0 - y0):max(0, cy1 - y0), max(0, cx0 - x0):max(0, cx1 - x0)] = False
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


def cut_held(src, box, tol):
    """A held weapon sprite from an illustrated sheet: mask what differs from the smooth background, keep the largest
    piece, fill its holes, then sample each sprite pixel's brighter pixels (a mean washes a crystal blade out to grey)."""
    x0, y0, x1, y1 = box
    work = src.crop(box).resize(((x1 - x0) // 4, (y1 - y0) // 4), Image.BOX)   # quarter size is plenty for 20 px
    im = np.asarray(work).astype(int)
    bg = work.resize((work.width // 10, work.height // 10), Image.BOX).filter(ImageFilter.MinFilter(3))
    bg = np.asarray(bg.resize(work.size, Image.BILINEAR)).astype(int)
    mask = Image.fromarray(np.where(np.abs(im - bg).sum(-1) > tol, 255, 0).astype(np.uint8))
    mask = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3))
    keep = largest_component(np.asarray(mask) > 0)
    keep = ~largest_component(np.pad(~keep, 1, constant_values=True))[1:-1, 1:-1]   # fill holes
    ys, xs = np.where(keep)
    im, keep = im[ys.min():ys.max() + 1, xs.min():xs.max() + 1], keep[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    h, w = keep.shape
    inner = HELD_PX - 2
    nh, nw = max(1, round(h * inner / max(h, w))), max(1, round(w * inner / max(h, w)))
    yb, xb = np.linspace(0, h, nh + 1).astype(int), np.linspace(0, w, nw + 1).astype(int)
    on = np.zeros((nh, nw), bool)
    rgb = np.zeros((nh, nw, 3), np.uint8)
    for i in range(nh):
        for j in range(nw):
            cell = keep[yb[i]:yb[i + 1], xb[j]:xb[j + 1]]
            px = im[yb[i]:yb[i + 1], xb[j]:xb[j + 1]][cell]
            on[i, j] = cell.mean() >= 0.25
            if len(px):
                rgb[i, j] = px[np.argsort(px.sum(1), kind="stable")[int(0.8 * (len(px) - 1))]]
    rgb = np.asarray(Image.fromarray(rgb).quantize(colors=12, method=Image.Quantize.MEDIANCUT,
                                                   dither=Image.Dither.NONE).convert("RGB"))
    on = np.pad(on, 1)
    out = np.zeros(on.shape + (3,), np.uint8)
    out[1:-1, 1:-1] = rgb
    near = np.zeros_like(on)
    near[1:, :] |= on[:-1, :]
    near[:-1, :] |= on[1:, :]
    near[:, 1:] |= on[:, :-1]
    near[:, :-1] |= on[:, 1:]
    ring = near & ~on
    out[ring] = OUTLINE
    alpha = np.where(on | ring, 255, 0).astype(np.uint8)
    return Image.fromarray(np.dstack([out, alpha]), "RGBA")


def preview(img, path, scale=8):
    big = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
    bg = Image.new("RGBA", big.size, (18, 18, 18, 255))
    bg.alpha_composite(big)
    bg.convert("RGB").save(path)


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
    if args.preview:
        os.makedirs(args.preview, exist_ok=True)
    for cid, c in PILOT.items():
        src = Image.open(os.path.join(COMPANION_REF, c["src"])).convert("RGB")
        splash_src = Image.open(os.path.join(COMPANION_REF, c["splash_src"])).convert("RGB") if "splash_src" in c else src
        x0, y0, x1, y1 = c["splash"]
        splash = splash_src.crop((x0, y0, x1, y1)).resize((1080, 1400), Image.LANCZOS)
        bx, by, bs = c["bust"]
        print(save(splash, "Splash", "art_splash_" + cid))
        print(save(src.crop((bx, by, bx + bs, by + bs)).resize((256, 256), Image.LANCZOS), "Portraits", "portrait_" + cid))
        cx, cy, r = c["face"]
        print(save(square(src, cx, cy, r, 128), "Icons", "icon_" + cid))
        chibi = cut_chibi(src, c["chibi"], c["grid"], c.get("clear", ()), c.get("wide_gold", False))
        print(save(chibi, "Pixel", "spr_" + cid), chibi.size)
        if args.preview:
            preview(chibi, os.path.join(args.preview, cid + "_chibi_x8.png"))
    for wid, w in WEAPONS.items():
        src = Image.open(os.path.join(WEAPON_REF, w["src"])).convert("RGB")
        x0, y0, size = w["icon"]
        print(save(src.crop((x0, y0, x0 + size, y0 + size)).resize((128, 128), Image.LANCZOS), "Icons", "icon_" + wid))
        held = cut_held(src, w["blade"], w["tol"])
        print(save(held, "Pixel", "spr_" + wid), held.size)
        if args.preview:
            preview(held, os.path.join(args.preview, wid + "_held_x8.png"))


if __name__ == "__main__":
    main()
