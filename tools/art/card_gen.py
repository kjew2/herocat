"""Generate clean-cel card art explorations for Epic, Rare and Common companions (issue #321).

These rarities get no splash (specs/00 decision 108), only portraits, icons and summon cards, so each gets one knee-up
card illustration on a simple element-coloured background. Prompts are built from each character's brief in
art/concepts/companions.md, the rarity ladder there, and the house style, owner preferences and avoid list in
art/concepts/splash-prompts.md.

Usage: python3 tools/art/card_gen.py epic rare common [--only hazel,tilly] [--n 1] [--quality medium]
Writes art/exploration/cards/<name>_<k>.jpg and logs every prompt to prompts.txt there.
"""
import argparse, concurrent.futures as cf, os, re, sys
from datetime import date

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import splash_gen  # noqa: E402  (shares the house style, avoid list and API call)

ROOT = splash_gen.ROOT
BRIEFS = os.path.join(ROOT, "art/concepts/companions.md")
OUT = os.path.join(ROOT, "art/exploration/cards")

# How glamorous a design is, per rarity (companions.md "Rarity ladder").
RARITY = {
    "Common": "Design level: cute and grounded townsfolk or apprentice, practical work clothes, two main colours, an everyday tool turned weapon, fully covered.",
    "Rare": "Design level: an adventurer with a theme, a fitted outfit with some flair, two or three colours, a signature weapon, a little skin at most.",
    "Epic": "Design level: a glamorous specialist, fashion-forward and confident, a striking palette and a statement outfit, visible skin such as bare shoulders, midriff or a short hem, within a mature rating, no nudity.",
}
ELEMENT = {
    "FIRE": "warm orange and red light with drifting embers",
    "WATER": "cool teal and blue light with soft water ripples",
    "LEAF": "fresh green light with drifting leaves",
    "LIGHT": "soft gold light with gentle rays",
    "DARK": "deep violet shadows with faint wisps",
    "NONE": "a neutral warm grey studio backdrop with soft light",
}


def briefs():
    s = open(BRIEFS, encoding="utf-8").read()
    out = {}
    for tier in RARITY:
        block = re.search(r"^## " + tier + r"\n(.+?)(?=^## )", s, re.S | re.M).group(1)
        for m in re.finditer(r"^### (\w+)\n`(comp_\w+)`, ([^,]+), (\w+)\.\n(.+?)(?=^### |\Z)", block, re.S | re.M):
            name, cid, role, element, body = m.groups()
            field = lambda k: (re.search(r"- \*\*" + k + r":\*\* (.+)", body) or [None, ""])[1].strip()
            out[name.lower()] = dict(name=name, id=cid, tier=tier, role=role, element=element,
                                     hook=field("Hook"), look=field("Look"), palette=field("Palette"))
    return out


def prompt(b, style, avoid):
    return (f"Vertical 2:3 knee-up anime character illustration for a mobile game collection card. A young woman, "
            f"a {b['role'].lower()}: {b['hook']} Look: {b['look']} A confident ready stance showing her signature weapon "
            f"or tool, the whole figure from head to knees inside the frame with margin around her. Background: "
            f"{ELEMENT.get(b['element'], ELEMENT['NONE'])}, simple and out of focus. Palette: {b['palette']} "
            f"{RARITY[b['tier']]} Face in the upper third, character centred. {style} {avoid}")


def main():
    styles, avoid, _ = splash_gen.load()
    all_b = briefs()
    ap = argparse.ArgumentParser()
    ap.add_argument("tiers", nargs="+", choices=[t.lower() for t in RARITY])
    ap.add_argument("--only", help="comma-separated names within the tiers")
    ap.add_argument("--n", type=int, default=1)
    ap.add_argument("--start", type=int, default=1)
    ap.add_argument("--quality", default="medium", choices=["low", "medium", "high"])
    ap.add_argument("--model", default="gpt-image-2")
    a = ap.parse_args()
    only = set(a.only.lower().split(",")) if a.only else None
    picked = [b for k, b in all_b.items() if b["tier"].lower() in a.tiers and (not only or k in only)]
    os.makedirs(OUT, exist_ok=True)
    splash_gen.OUT = OUT  # generate() writes into splash_gen.OUT
    jobs = {(b["name"].lower(), k): prompt(b, styles["house"], avoid) for b in picked for k in range(a.start, a.start + a.n)}
    failed = 0
    with cf.ThreadPoolExecutor(8) as ex, open(os.path.join(OUT, "prompts.txt"), "a", encoding="utf-8") as log:
        futs = [ex.submit(splash_gen.generate, n, k, p, None, a.quality, a.model) for (n, k), p in jobs.items()]
        for fut in futs:
            name, k, path, err = fut.result()
            print(f"{name}_{k}: " + (os.path.relpath(path, ROOT) if path else "ERROR " + str(err)))
            if not path:
                failed += 1
                continue
            log.write(f"## {os.path.basename(path)}  {date.today()}  model={a.model} quality={a.quality} size=1024x1536\n"
                      f"{jobs[(name, k)]}\n\n")
    print(f"{len(jobs) - failed} of {len(jobs)} made")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
