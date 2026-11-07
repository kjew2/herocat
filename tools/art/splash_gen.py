"""Generate clean-cel splash explorations from art/concepts/splash-prompts.md (issue #321).

Usage: python3 tools/art/splash_gen.py lucy astraea [--n 2] [--start 1] [--quality medium] [--style house|e]
Needs OPENAI_API_KEY and curl. Writes art/exploration/splash_batch/<name>_<k>.png and appends to prompts.txt there.
Generation is text only (splash-prompts.md rules). --style-ref is kept for experiments.
"""
import argparse, base64, concurrent.futures as cf, json, os, re, subprocess, sys, tempfile
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROMPTS = os.path.join(ROOT, "art/concepts/splash-prompts.md")
OUT = os.path.join(ROOT, "art/exploration/splash_batch")
REF_NOTE = ("The reference image is a greyscale design sheet of this character. Copy only her design from it: hair, face, "
            "outfit, armour, accessories and props. Do NOT copy its rendering, shading, lighting, line quality, pose or "
            "composition, and ignore any text in it; render everything strictly in the style described, in full colour per the palette. ")

STYLE_NOTE = ("The second reference image is a style example of a different character: match its rendering only (line weight, "
              "shading, lighting, colour depth, level of detail). Do not copy anything from its character, outfit, pose or scene. ")


def load():
    s = open(PROMPTS, encoding="utf-8").read()
    styles = {"house": re.search(r"## House style\n\n> (.+)", s).group(1),
              "e": re.search(r"## Alternate style: E, blend\n\n.+?\n\n> (.+)", s, re.S).group(1)}
    avoid = re.search(r"## Avoid\n\n> (.+)", s).group(1)
    chars = {}
    for m in re.finditer(r"### (\w+)\n(.+?)(?=\n### |\Z)", s, re.S):
        body = m.group(2)
        prompt = re.search(r"^> (.+)$", body, re.M)
        if not prompt:
            continue  # e.g. Yuki, whose prompt lives in splash-pilot.md
        ref = re.search(r"Reference: `([^`]+)`", body)
        chars[m.group(1).lower()] = (prompt.group(1), os.path.join(ROOT, ref.group(1)) if ref else None)
    return styles, avoid, chars


def generate(name, k, prompt, ref, quality, model, tag="", style_ref=None):
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
        f.write(prompt)
    auth = ["-H", "Authorization: Bearer " + os.environ["OPENAI_API_KEY"]]
    if ref:
        cmd = ["curl", "-sS", "--max-time", "600", "https://api.openai.com/v1/images/edits", *auth,
               "-F", "model=" + model, "-F", "size=1024x1536", "-F", "quality=" + quality, "-F", "moderation=low",
               "-F", "prompt=<" + f.name, "-F", "image[]=@" + ref]
        if style_ref:
            cmd += ["-F", "image[]=@" + style_ref]
    else:
        cmd = ["curl", "-sS", "--max-time", "600", "https://api.openai.com/v1/images/generations", *auth,
               "-H", "Content-Type: application/json", "-d", json.dumps(
                   {"model": model, "size": "1024x1536", "quality": quality, "moderation": "low", "prompt": prompt})]
    try:
        d = json.loads(subprocess.run(cmd, capture_output=True, text=True, check=True).stdout)
    finally:
        os.unlink(f.name)
    if "error" in d:
        return name, k, None, d["error"].get("message")
    raw = base64.b64decode(d["data"][0]["b64_json"])
    try:  # high-quality JPEG keeps exploration images about 7x smaller than PNG in git
        import io
        from PIL import Image
        path = os.path.join(OUT, f"{name}_{tag}{k}.jpg")
        Image.open(io.BytesIO(raw)).convert("RGB").save(path, quality=92, optimize=True)
    except ImportError:
        path = os.path.join(OUT, f"{name}_{tag}{k}.png")
        open(path, "wb").write(raw)
    return name, k, path, None


def main():
    styles, avoid, chars = load()
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="+", choices=sorted(chars))
    ap.add_argument("--n", type=int, default=1)
    ap.add_argument("--start", type=int, default=1, help="first image index, to add more without overwriting")
    ap.add_argument("--quality", default="medium", choices=["low", "medium", "high"])
    ap.add_argument("--model", default="gpt-image-2")
    ap.add_argument("--no-ref", action="store_true", help="ignore the design sheet; text only (files tagged t)")
    ap.add_argument("--style-ref", help="an image to copy the rendering style from, sent after the design sheet (files tagged s)")
    ap.add_argument("--style", default="house", choices=["house", "e"], help="house (clean cel) or the pilot's E blend")
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    tag = ("" if a.style == "house" else a.style) + ("t" if a.no_ref else "") + ("s" if a.style_ref else "")
    if a.style_ref and a.no_ref:
        ap.error("--style-ref needs the design sheet; drop --no-ref")
    jobs = {}
    for name in a.names:
        prompt, ref = chars[name]
        if a.no_ref:
            ref = None
        full = (REF_NOTE if ref else "") + (STYLE_NOTE if a.style_ref else "") + prompt + " " + styles[a.style] + " " + avoid
        for k in range(a.start, a.start + a.n):
            jobs[(name, k)] = (full, ref)
    failed = 0
    with cf.ThreadPoolExecutor(8) as ex, open(os.path.join(OUT, "prompts.txt"), "a", encoding="utf-8") as log:
        futs = [ex.submit(generate, n, k, p, r, a.quality, a.model, tag, a.style_ref) for (n, k), (p, r) in jobs.items()]
        for fut in futs:
            name, k, path, err = fut.result()
            print(f"{name}_{a.style}_{k}: " + (os.path.relpath(path, ROOT) if path else "ERROR " + str(err)))
            if not path:
                failed += 1
                continue
            prompt, ref = jobs[(name, k)]
            log.write(f"## {os.path.basename(path)}  {date.today()}  style={a.style} model={a.model} quality={a.quality} size=1024x1536 "
                      f"ref={os.path.relpath(ref, ROOT) if ref else 'none'}\n{prompt}\n\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
