#!/usr/bin/env python3
"""Clock purity grep (specs/02 section 11.2 rule 1, acceptance 18; issue #78).

- HeroCat.Sim reads no clock and has no engine: no UnityEngine, DateTime.Now/UtcNow/Today,
  DateTimeOffset.Now/UtcNow, Stopwatch or Environment.TickCount.
- HeroCat.Runtime reads system time only in ServerClock.cs and SessionRunner.cs.

Line comments are ignored, so a doc comment may name the APIs. Exits 1 and lists every hit on a violation.

  python3 tools/check_clock_apis.py              check the repo
  python3 tools/check_clock_apis.py --self-test  prove the check catches a bad line (check.sh runs both)
"""
import os
import re
import sys
import tempfile

CLOCK = r"\bDateTime(?:Offset)?\s*\.\s*(?:Now|UtcNow|Today)\b|\bStopwatch\b|\bEnvironment\s*\.\s*TickCount(?:64)?\b"
RULES = [
    # (directory relative to the root, banned pattern, file names allowed to match)
    ("src/HeroCat.Sim", re.compile(CLOCK + r"|\bUnityEngine\b"), set()),
    ("src/HeroCat.Runtime", re.compile(CLOCK), {"ServerClock.cs", "SessionRunner.cs"}),
]


def strip_line_comment(line):
    """Drops a // comment, ignoring // inside a string literal."""
    in_str = False
    i = 0
    while i < len(line) - 1:
        c = line[i]
        if c == '"' and (i == 0 or line[i - 1] != "\\"):
            in_str = not in_str
        elif not in_str and c == "/" and line[i + 1] == "/":
            return line[:i]
        i += 1
    return line


def violations(root):
    hits = []
    for rel, pattern, allowed in RULES:
        base = os.path.join(root, rel)
        for dirpath, _, files in os.walk(base):
            for name in sorted(files):
                if not name.endswith(".cs") or name in allowed:
                    continue
                path = os.path.join(dirpath, name)
                with open(path, encoding="utf-8-sig") as f:
                    for n, line in enumerate(f, 1):
                        m = pattern.search(strip_line_comment(line))
                        if m:
                            hits.append(f"{os.path.relpath(path, root)}:{n}: {m.group(0)}")
    return hits


def self_test():
    with tempfile.TemporaryDirectory() as root:
        def write(rel, text):
            p = os.path.join(root, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w", encoding="utf-8") as f:
                f.write(text)

        write("src/HeroCat.Sim/Ok.cs", "// DateTime.UtcNow in a comment is fine\nclass Ok { string s = \"a//b\"; }\n")
        write("src/HeroCat.Runtime/Services/ServerClock.cs", "var t = DateTimeOffset.UtcNow;\n")
        assert violations(root) == [], violations(root)

        write("src/HeroCat.Sim/Bad.cs", "class Bad { long t = System.DateTime.UtcNow.Ticks; }\n")
        write("src/HeroCat.Sim/Engine.cs", "using UnityEngine;\n")
        write("src/HeroCat.Runtime/Save/Sneaky.cs", "var w = System.Diagnostics.Stopwatch.StartNew();\n")
        write("src/HeroCat.Runtime/Save/Sneaky2.cs", "var t = DateTimeOffset . Now;\n")
        hits = violations(root)
        expected = ["Bad.cs:1", "Engine.cs:1", "Sneaky.cs:1", "Sneaky2.cs:1"]
        for e in expected:
            assert any(e in h for h in hits), (e, hits)
        assert len(hits) == len(expected), hits
    print("check_clock_apis self-test OK")


def main():
    if "--self-test" in sys.argv[1:]:
        self_test()
        return 0
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    hits = violations(root)
    if hits:
        print("Clock API use outside the clock seam (specs/02 section 11.2 rule 1, acceptance 18):", file=sys.stderr)
        for h in hits:
            print("  " + h, file=sys.stderr)
        return 1
    print("clock purity OK (HeroCat.Sim reads no clock; HeroCat.Runtime only in ServerClock.cs / SessionRunner.cs)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
