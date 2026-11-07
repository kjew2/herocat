"""Generate Yuki splash style pilot candidates with the OpenAI image API (issue #321).

Prompts come from art/concepts/splash-pilot.md, so the brief stays the source of truth.
Usage: python3 tools/art/splash_pilot_gen.py A B C D E [--n 1] [--quality medium] [--ref PATH]
Needs OPENAI_API_KEY and curl. Writes art/exploration/splash_pilot/yuki_<v>_<k>.png and appends to prompts.txt.
"""
import argparse, base64, concurrent.futures as cf, json, os, re, subprocess, sys, tempfile
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BRIEF = os.path.join(ROOT, "art/concepts/splash-pilot.md")
OUT = os.path.join(ROOT, "art/exploration/splash_pilot")
STYLE_KEYS = {"A": "A, Clean cel", "B": "B, Graphic pop", "C": "C, Gothic drama", "D": "D, Painterly", "E": "E, Blend"}
REF_NOTE = ("The reference image is a greyscale design sheet of this character (face and hair, outfit, plush bunny, shoes). "
            "Copy only her design from it: hair cut and colour panels, face, outfit pieces, accessories and the bunny. "
            "Do NOT copy its rendering, shading, lighting, softness, line quality or composition; render everything "
            "strictly in the style described below, in full colour per the palette. ")

def prompts():
    s = open(BRIEF, encoding="utf-8").read()
    base = re.search(r"\*\*Base \(every variant\):\*\*\n\n> (.+)", s).group(1)
    avoid = re.search(r"\*\*Avoid \(every variant\):\*\* (.+)", s).group(1)
    styles = {k: re.search(r"- \*\*" + re.escape(v) + r":\*\* (.+)", s).group(1) for k, v in STYLE_KEYS.items()}
    return base, avoid, styles

def generate(variant, k, prompt, ref, quality, model):
    path = os.path.join(OUT, f"yuki_{variant.lower()}_{k}.png")
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as f:
        f.write(prompt)
    url = "https://api.openai.com/v1/images/" + ("edits" if ref else "generations")
    cmd = ["curl", "-sS", "--max-time", "600", url, "-H", "Authorization: Bearer " + os.environ["OPENAI_API_KEY"],
           "-F", "model=" + model, "-F", "size=1024x1536", "-F", "quality=" + quality, "-F", "moderation=low",
           "-F", "prompt=<" + f.name]
    if ref:
        cmd += ["-F", "image[]=@" + ref]
    else:  # generations takes JSON
        cmd = cmd[:6] + ["-H", "Content-Type: application/json", "-d", json.dumps(
            {"model": model, "size": "1024x1536", "quality": quality, "moderation": "low", "prompt": prompt})]
    d = json.loads(subprocess.run(cmd, capture_output=True, text=True, check=True).stdout)
    os.unlink(f.name)
    if "error" in d:
        return variant, k, None, d["error"].get("message")
    open(path, "wb").write(base64.b64decode(d["data"][0]["b64_json"]))
    return variant, k, path, None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("variants", nargs="+", choices=list(STYLE_KEYS))
    ap.add_argument("--n", type=int, default=1)
    ap.add_argument("--start", type=int, default=1, help="first image index, to add more without overwriting")
    ap.add_argument("--quality", default="medium", choices=["low", "medium", "high"])
    ap.add_argument("--model", default="gpt-image-2")
    ap.add_argument("--ref", default=os.path.join(OUT, "yuki_design_sheet.png"), help="'' for text only")
    a = ap.parse_args()
    base, avoid, styles = prompts()
    os.makedirs(OUT, exist_ok=True)
    jobs = []
    for v in a.variants:
        p = (REF_NOTE if a.ref else "") + base + " Style: " + styles[v] + " Avoid: " + avoid
        jobs += [(v, k, p) for k in range(a.start, a.start + a.n)]
    with cf.ThreadPoolExecutor(8) as ex, open(os.path.join(OUT, "prompts.txt"), "a", encoding="utf-8") as log:
        for v, k, path, err in ex.map(lambda j: generate(j[0], j[1], j[2], a.ref or None, a.quality, a.model), jobs):
            prompt = next(p for vv, kk, p in jobs if vv == v and kk == k)
            print(f"{v}{k}: " + (os.path.relpath(path, ROOT) if path else "ERROR " + str(err)))
            if path:
                log.write(f"## yuki_{v.lower()}_{k}.png  {date.today()}  model={a.model} quality={a.quality} size=1024x1536 "
                          f"ref={os.path.relpath(a.ref, ROOT) if a.ref else 'none'}\n{prompt}\n\n")
    return 0

if __name__ == "__main__":
    sys.exit(main())
