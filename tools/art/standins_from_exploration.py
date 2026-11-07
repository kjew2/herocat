#!/usr/bin/env python3
"""Cut in-game stand-in art for all 50 companions from the owner-approved AI explorations (issue #321).

The owner approved the explorations for the game on 2026-11-07. They are stand-ins, like the earlier ones cut from the
artist's sheets: the artist's finals replace them before release, because purely AI-generated art can't be registered
for copyright (specs/07 section 15.3); TODO(#331): nothing blocks a release build that still has them. Each companion's source is the owner's pick for the eight Legendary and Mythic
splashes (art/concepts/splash-prompts.md, "Owner preferences") and the card take for the other 42.

Outputs, named by content address (specs/07 section 15.2), in unity/HeroCatClient/Assets/Resources/Art/<Kind>/:

  Icons/icon_<id>.png          128 x 128 face crop
  Portraits/portrait_<id>.png  256 x 256 head-and-shoulders crop
  Splash/art_splash_<id>.png   1080 x 1920 centred 9:16 cover crop of the 2:3 source (Legendary and Mythic only)

Battle chibis (Pixel/) are not touched; tools/art/cut_pilot_assets.py still cuts the four that exist.

Usage: python3 tools/art/standins_from_exploration.py [--preview FILE]
Needs Pillow. Reads art/exploration/ and art/concepts/companions.md.
"""
import argparse
import os
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import card_gen  # noqa: E402  (companion ids, names and rarities from companions.md)

ROOT = card_gen.ROOT
OUT = os.path.join(ROOT, "unity", "HeroCatClient", "Assets", "Resources", "Art")
SPLASH = (1080, 1920)

# The owner's splash picks (2026-11-07): id, source file in art/exploration/splash_batch/.
SPLASH_PICKS = {
    "yuki": ("comp_l_02", "yuki_1.jpg"),
    "lucy": ("comp_l_01", "lucy_t1.jpg"),
    "carmine": ("comp_l_03", "carmine_3.jpg"),
    "valeria": ("comp_l_04", "valeria_t1.jpg"),
    "marisol": ("comp_l_05", "marisol_3.jpg"),
    "shiori": ("comp_l_06", "shiori_1.jpg"),
    "seraphine": ("comp_m_01", "seraphine_t2.jpg"),
    "astraea": ("comp_m_02", "astraea_3.jpg"),
}

# Face centre height as a fraction of the source height; 0.18 unless a hat, hood or crown pushes the face down.
# Checked by eye on a contact sheet of all 50 (--preview).
FACE_Y = {"mina": 0.23, "kestrel": 0.24, "mara": 0.25, "celeste": 0.23, "astraea": 0.26, "lucy": 0.25,
          "seraphine": 0.22, "yuki": 0.21}
FACE_R = 0.16      # icon half-size, as a fraction of the source width
BUST_R = 0.30      # portrait half-size, as a fraction of the source width
BUST_DROP = 0.08   # portrait centre sits this far below the face, as a fraction of the source height


def sources():
    """(name, id, path, is_splash) for all 50 companions."""
    out = []
    for name, (cid, f) in SPLASH_PICKS.items():
        out.append((name, cid, os.path.join(ROOT, "art", "exploration", "splash_batch", f), True))
    for name, b in card_gen.briefs().items():
        out.append((name, b["id"], os.path.join(ROOT, "art", "exploration", "cards", name + "_1.jpg"), False))
    return out


def square(im, cx, cy, r, size):
    W, H = im.size
    r = min(r, cx, W - cx)  # keep the square inside the image
    top = max(0, min(cy - r, H - 2 * r))
    return im.crop((int(cx - r), int(top), int(cx + r), int(top + 2 * r))).resize((size, size), Image.LANCZOS)


def save(img, kind, name):
    d = os.path.join(OUT, kind)
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, name + ".png")
    img.save(p, optimize=True)
    return os.path.relpath(p, ROOT)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--preview", help="also write a contact sheet of every icon and portrait to this file")
    a = ap.parse_args()
    rows = []
    for name, cid, path, is_splash in sources():
        im = Image.open(path).convert("RGB")
        W, H = im.size
        cx, cy = W / 2, FACE_Y.get(name, 0.18) * H
        icon = square(im, cx, cy, FACE_R * W, 128)
        portrait = square(im, cx, cy + BUST_DROP * H, BUST_R * W, 256)
        print(save(icon, "Icons", "icon_" + cid))
        print(save(portrait, "Portraits", "portrait_" + cid))
        if is_splash:
            w = H * 9 / 16  # centred 9:16 cover crop of the 2:3 source
            splash = im.crop((int((W - w) / 2), 0, int((W + w) / 2), H)).resize(SPLASH, Image.LANCZOS)
            print(save(splash, "Splash", "art_splash_" + cid))
        rows.append((name, icon, portrait))
    if a.preview:
        cols, cw, ch = 10, 128 + 256 + 12, 256 + 18
        sheet = Image.new("RGB", (cols * cw, ((len(rows) + cols - 1) // cols) * ch), (18, 18, 18))
        d = ImageDraw.Draw(sheet)
        for i, (name, icon, portrait) in enumerate(rows):
            x, y = (i % cols) * cw, (i // cols) * ch
            sheet.paste(icon, (x, y))
            sheet.paste(portrait, (x + 132, y))
            d.text((x + 2, y + 258), name, fill=(255, 255, 0))
        sheet.save(a.preview)
    print(f"{len(rows)} companions")


if __name__ == "__main__":
    main()
