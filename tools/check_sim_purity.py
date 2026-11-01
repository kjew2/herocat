#!/usr/bin/env python3
"""Sim purity and assembly dependency rules (specs/01 section 1.2 and AC 1, 00 contract 1; issue #63).

- src/HeroCat.Sim/**/*.cs uses none of 01 AC 1's banned identifiers: UnityEngine, System.IO, System.Net,
  Newtonsoft, System.Reflection, DateTime.Now, DateTime.UtcNow, DateTimeOffset.Now, DateTimeOffset.UtcNow,
  Stopwatch, Math.Pow, Math.Exp, Math.Log. Comments and string literals are ignored.
- HeroCat.Sim.csproj has no PackageReference, Reference or ProjectReference; HeroCat.Sim.asmdef has
  noEngineReferences true and no references.
- HeroCat.Runtime and HeroCat.Remote reference no Unity assembly: no UnityEngine/UnityEditor in their .cs files or
  .csproj, and noEngineReferences true in their .asmdef.

The Runtime clock-API rule of 02 AC 18 is tools/check_clock_apis.py. Exits 1 and lists every hit.

  python3 tools/check_sim_purity.py              check the repo
  python3 tools/check_sim_purity.py --self-test  prove the check catches planted violations (check.sh runs both)
"""
import json
import os
import re
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from check_sim_literals import strip_code  # noqa: E402  (comment and string stripping shared with the literal check)

BANNED = ["UnityEngine", "System.IO", "System.Net", "Newtonsoft", "System.Reflection", "DateTime.Now",
          "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "Stopwatch", "Math.Pow", "Math.Exp",
          "Math.Log"]
SIM_BANNED = re.compile(r"(?<!\w)(" + "|".join(re.escape(b) for b in BANNED) + r")\w*")   # Math.Log10 too
UNITY = re.compile(r"\bUnity(?:Engine|Editor)\b")
PROJECT_REF = re.compile(r"<\s*(PackageReference|Reference|ProjectReference)\b")


def cs_files(base):
    for dirpath, dirs, files in os.walk(base):
        dirs[:] = sorted(d for d in dirs if d not in ("bin", "obj"))
        for name in sorted(files):
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def grep_code(path, pattern, root):
    with open(path, encoding="utf-8-sig") as f:
        code = strip_code(f.read())
    hits = []
    for n, line in enumerate(code.split("\n"), 1):
        for m in pattern.finditer(line):
            hits.append(f"{os.path.relpath(path, root)}:{n}: {m.group(0)}")
    return hits


def asmdef(root, rel):
    path = os.path.join(root, rel)
    if not os.path.exists(path):
        return None, [f"{rel}: missing"]
    with open(path, encoding="utf-8-sig") as f:
        try:
            return json.load(f), []
        except ValueError as e:
            return None, [f"{rel}: not JSON ({e})"]


def violations(root):
    hits = []
    sim = os.path.join(root, "src/HeroCat.Sim")
    for p in cs_files(sim):
        hits += grep_code(p, SIM_BANNED, root)

    csproj = os.path.join(sim, "HeroCat.Sim.csproj")
    with open(csproj, encoding="utf-8-sig") as f:
        for n, line in enumerate(f, 1):
            m = PROJECT_REF.search(line)
            if m:
                hits.append(f"src/HeroCat.Sim/HeroCat.Sim.csproj:{n}: {m.group(1)} (the Sim takes no dependency)")
    a, errs = asmdef(root, "src/HeroCat.Sim/HeroCat.Sim.asmdef")
    hits += errs
    if a is not None:
        if a.get("noEngineReferences") is not True:
            hits.append("src/HeroCat.Sim/HeroCat.Sim.asmdef: noEngineReferences must be true")
        if a.get("references") or a.get("precompiledReferences"):
            hits.append("src/HeroCat.Sim/HeroCat.Sim.asmdef: references must be empty")

    for name in ("HeroCat.Runtime", "HeroCat.Remote"):
        base = os.path.join(root, "src", name)
        for p in cs_files(base):
            hits += grep_code(p, UNITY, root)
        proj = os.path.join(base, name + ".csproj")
        if os.path.exists(proj):
            with open(proj, encoding="utf-8-sig") as f:
                for n, line in enumerate(f, 1):
                    if UNITY.search(line):
                        hits.append(f"src/{name}/{name}.csproj:{n}: Unity reference")
        a, errs = asmdef(root, f"src/{name}/{name}.asmdef")
        hits += errs
        if a is not None and a.get("noEngineReferences") is not True:
            hits.append(f"src/{name}/{name}.asmdef: noEngineReferences must be true")
    return hits


def self_test():
    with tempfile.TemporaryDirectory() as root:
        def write(rel, text):
            p = os.path.join(root, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w", encoding="utf-8") as f:
                f.write(text)

        clean_asmdef = json.dumps({"name": "x", "references": [], "precompiledReferences": [], "noEngineReferences": True})
        write("src/HeroCat.Sim/HeroCat.Sim.csproj", "<Project><PropertyGroup/></Project>\n")
        write("src/HeroCat.Sim/HeroCat.Sim.asmdef", clean_asmdef)
        write("src/HeroCat.Sim/Ok.cs", "// Math.Pow and System.IO in a comment are fine\nclass Ok { string s = \"DateTime.UtcNow\"; double d = Math.Floor(1.0); }\n")
        for name in ("HeroCat.Runtime", "HeroCat.Remote"):
            write(f"src/{name}/{name}.csproj", "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /></ItemGroup></Project>\n")
            write(f"src/{name}/{name}.asmdef", json.dumps({"name": name, "references": ["HeroCat.Sim"], "noEngineReferences": True}))
            write(f"src/{name}/Ok.cs", "using System.IO;\nclass Ok {}\n")
        assert violations(root) == [], violations(root)

        write("src/HeroCat.Sim/Bad.cs", "\n".join([
            "using System.IO;",
            "class Bad { long t = System.DateTime.UtcNow.Ticks; double p = Math.Pow(2, 3);",
            "  object r = typeof(Bad).GetTypeInfo(); }",
            "using System.Reflection;",
            "using UnityEngine;",
        ]) + "\n")
        write("src/HeroCat.Sim/HeroCat.Sim.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" /></ItemGroup></Project>\n")
        write("src/HeroCat.Runtime/Engine.cs", "using UnityEngine;\n")
        write("src/HeroCat.Remote/HeroCat.Remote.asmdef", json.dumps({"name": "r", "noEngineReferences": False}))
        hits = violations(root)
        expected = ["Bad.cs:1: System.IO", "Bad.cs:2: DateTime.UtcNow", "Bad.cs:2: Math.Pow", "Bad.cs:4: System.Reflection",
                    "Bad.cs:5: UnityEngine", "HeroCat.Sim.csproj:1: PackageReference", "Engine.cs:1: UnityEngine",
                    "HeroCat.Remote.asmdef: noEngineReferences"]
        for e in expected:
            assert any(e in h for h in hits), (e, hits)
        assert len(hits) == len(expected), hits
    print("check_sim_purity self-test OK")


def main():
    if "--self-test" in sys.argv[1:]:
        self_test()
        return 0
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    hits = violations(root)
    if hits:
        print("Sim purity / assembly dependency violations (specs/01 section 1.2, AC 1; 00 contract 1):")
        for h in hits:
            print("  " + h)
        return 1
    print("check_sim_purity: HeroCat.Sim is pure; Runtime and Remote reference no Unity assembly")
    return 0


if __name__ == "__main__":
    sys.exit(main())
