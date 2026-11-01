#!/usr/bin/env python3
"""Content fixtures (specs/09 acceptance 6, 7, 14, 15, 20; issue #124).

    python3 tools/content_fixtures.py list
    python3 tools/content_fixtures.py run <name>          # print the exporter's stderr for one fixture
    python3 tools/content_fixtures.py emit --out <dir>    # JSON for the C# parity test (ContentDbFixtureTests)

Layout: `tests/content_fixtures/good_minimal/` is a small valid `content_src/` (two chapters, one collectible per
rarity, two banner levels, `ship_locales = en,ja` with every locale filled). Every other directory is a fixture that
describes its defect as edits on that baseline instead of a full copy, so adding a table or column means editing
good_minimal only, not sixty fixtures:

    fixture.json   {"edits": [[table, old, new], ...],      # str.replace(old, new, 1) on <table>.csv; old must occur
                    "append": [[table, csv_line], ...],     # extra rows
                    "remove_tables": [table, ...],
                    "previous": {"edits": [...], "drop_column": [table, column]},   # E09x: a previous build
                    "args": ["--strict-warnings", "--ui-keys", "{ui_keys}"],          # {ui_keys} = ./ui_keys.txt
                    "import": {"locale": "ja", "fill": {key: text}},   # E076: run import-translations, not build
                    "csharp": true}                         # ContentDb.Build must report the same first line
    <table>.csv    optional: adds or replaces that table wholesale
    expected.txt   the first stderr line(s) of `build`; three_defects holds three lines in table order

`import` makes a translator round-trip fixture (09 section 13.2, issue #213): `translator-csv --locale <locale>` of the
baseline, with `fill` written into the locale column, is what the translator returns; then the edits change the source
under it and the fixture runs `import-translations` on the result instead of `build`.

`csharp: true` marks the loader-scope fixtures (09 section 8.6): `emit` builds them with the exporter's fixture-only
`--emit-invalid` (the defect survives into the JSON) and writes `<out>/<name>/expected.txt`; the C# test loads each
through ContentLoader and asserts ContentDb.Build's first error line equals it. Stdlib only.
"""
import argparse
import contextlib
import csv
import io
import json
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_content as ex  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIXTURES = os.path.join(REPO, "tests", "content_fixtures")
BASE = os.path.join(FIXTURES, "good_minimal")


def names():
    return sorted(d for d in os.listdir(FIXTURES)
                  if os.path.isdir(os.path.join(FIXTURES, d)) and d != "good_minimal")


def spec(name):
    p = os.path.join(FIXTURES, name, "fixture.json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def expected(name):
    with open(os.path.join(FIXTURES, name, "expected.txt"), encoding="utf-8") as f:
        return [ln.rstrip("\n") for ln in f if ln.strip()]


def apply_edits(src, s, fixture_dir=None):
    for table, old, new in s.get("edits", []):
        p = os.path.join(src, table + ".csv")
        with open(p, encoding="utf-8") as f:
            text = f.read()
        if old not in text:
            raise ValueError(f"edit target not found in {table}.csv: {old!r}")
        with open(p, "w", encoding="utf-8") as f:
            f.write(text.replace(old, new, 1))
    for table, line in s.get("append", []):
        with open(os.path.join(src, table + ".csv"), "a", encoding="utf-8") as f:
            f.write(line + "\n")
    for table in s.get("remove_tables", []):
        os.remove(os.path.join(src, table + ".csv"))
    if fixture_dir:
        for f in os.listdir(fixture_dir):
            if f.endswith(".csv"):
                shutil.copy(os.path.join(fixture_dir, f), os.path.join(src, f))


def run_main(argv):
    err, out = io.StringIO(), io.StringIO()
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
        rc = ex.main(argv)
    return rc, [ln for ln in err.getvalue().splitlines() if ln.strip()]


def materialize(name, tmp):
    """Writes the fixture's content_src to tmp/src (and its previous build to tmp/prev); returns the extra args."""
    s = spec(name)
    fdir = os.path.join(FIXTURES, name)
    src = os.path.join(tmp, "src")
    shutil.copytree(BASE, src)
    args = []
    if "previous" in s:
        psrc = os.path.join(tmp, "prev_src")
        prev = os.path.join(tmp, "prev")
        shutil.copytree(BASE, psrc)
        apply_edits(psrc, s["previous"])
        rc, lines = run_main(["build", "--src", psrc, "--out", prev, "--dto-out", os.path.join(tmp, "prev.cs")])
        if rc != 0:
            raise RuntimeError(f"{name}: previous build failed: {lines}")
        if "drop_column" in s["previous"]:
            table, col = s["previous"]["drop_column"]
            p = os.path.join(prev, table + ".json")
            with open(p, encoding="utf-8") as f:
                doc = json.load(f)
            for r in doc["rows"]:
                r.pop(col, None)
            with open(p, "w", encoding="utf-8") as f:
                f.write(ex.dumps(doc))
        args += ["--previous", prev]
    imp = s.get("import")
    if imp:   # E076-style: export the baseline for a translator, fill it, then let the edits move the source under it
        text, errs = ex.translator_csv(src, imp["locale"])
        if errs:
            raise RuntimeError(f"{name}: translator-csv failed: {list(errs)}")
        rows = list(csv.reader(io.StringIO(text)))
        for r in rows[1:]:
            r[2] = imp.get("fill", {}).get(r[0], r[2])
        path = os.path.join(tmp, "translator.csv")
        with open(path, "w", encoding="utf-8", newline="") as f:
            csv.writer(f, lineterminator="\n").writerows(rows)
        args += ["--csv", path, "--locale", imp["locale"]]
    apply_edits(src, s, fdir)
    for a in s.get("args", []):
        args.append(a.replace("{ui_keys}", os.path.join(fdir, "ui_keys.txt")))
    return src, args


def run(name, out=None, emit_invalid=False):
    """Builds one fixture; returns (exit code, stderr lines)."""
    with tempfile.TemporaryDirectory() as tmp:
        src, args = materialize(name, tmp)
        if spec(name).get("import"):
            return run_main(["import-translations", "--src", src] + args)
        argv = ["build", "--src", src, "--out", out or os.path.join(tmp, "out"),
                "--dto-out", os.path.join(tmp, "dto.cs")] + args
        if emit_invalid:
            argv.append("--emit-invalid")
        return run_main(argv)


def emit(out_dir):
    """The loader-scope fixtures as built JSON (defect intact) plus expected.txt, for ContentDbFixtureTests."""
    os.makedirs(out_dir, exist_ok=True)
    done = []
    for name in ["good_minimal"] + names():
        if name != "good_minimal" and not spec(name).get("csharp"):
            continue
        target = os.path.join(out_dir, name)
        if os.path.exists(target):
            shutil.rmtree(target)
        if name == "good_minimal":
            with tempfile.TemporaryDirectory() as tmp:
                rc, lines = run_main(["build", "--src", BASE, "--out", target, "--dto-out", os.path.join(tmp, "d.cs")])
            want = []
        else:
            rc, lines = run(name, out=target, emit_invalid=True)
            want = expected(name)
            if lines[:len(want)] != want:
                raise RuntimeError(f"{name}: exporter output {lines[:len(want)]} != expected.txt {want}")
        if not os.path.exists(os.path.join(target, "manifest.json")):
            raise RuntimeError(f"{name}: nothing emitted (a parse-level defect cannot reach ContentDb.Build): {lines}")
        with open(os.path.join(target, "expected.txt"), "w", encoding="utf-8") as f:
            f.write("".join(w + "\n" for w in want))
        done.append(name)
    return done


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("verb", choices=["list", "run", "emit"])
    ap.add_argument("name", nargs="?")
    ap.add_argument("--out")
    args = ap.parse_args(argv)
    if args.verb == "list":
        for n in names():
            print(n + ("  [csharp]" if spec(n).get("csharp") else ""))
        return 0
    if args.verb == "run":
        rc, lines = run(args.name)
        print("\n".join(lines))
        return rc
    if not args.out:
        print("emit needs --out <dir>", file=sys.stderr)
        return 2
    done = emit(args.out)
    print(f"emitted {len(done)} fixtures to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
