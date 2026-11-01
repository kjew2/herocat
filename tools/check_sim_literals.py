#!/usr/bin/env python3
"""No balance literal in the Sim (specs/09 principle 2 and acceptance 1, 00 contract 3; issue #131).

Scans src/HeroCat.Sim/**/*.cs for numeric literals. Allowed:

- the values 0, 1 and 2 in any spelling (0.0, 1L, 2u ...; -1 is the literal 1 after a unary minus);
- an array index: a literal that is the whole content of [ ] after an expression (cells[3]), not an array size
  after `new T` (new int[8]);
- lines that name the ATTACK_PROGRESS_UNIT fixed-point scale 03 declares (ATTACK_PROGRESS_UNIT / AttackProgressUnit);
- a line ending in `// literal-ok: <reason>`, and every line between `// literal-ok-begin: <reason>` and
  `// literal-ok-end` (algorithm tables such as SHA-256's round constants). The reason must not be empty.

Comments, string literals and char literals are ignored. Exits 1 and prints file:line for every hit.

  python3 tools/check_sim_literals.py              check the repo
  python3 tools/check_sim_literals.py --self-test  prove the check catches a planted literal (check.sh runs both)
"""
import os
import re
import sys
import tempfile

SIM_DIR = "src/HeroCat.Sim"
ALLOWED_VALUES = {0.0, 1.0, 2.0}
SCALE_NAMES = ("ATTACK_PROGRESS_UNIT", "AttackProgressUnit")
OK = re.compile(r"//\s*literal-ok:\s*\S")
OK_BEGIN = re.compile(r"//\s*literal-ok-begin:\s*\S")
OK_END = re.compile(r"//\s*literal-ok-end\b")

# A C# numeric literal: hex, binary, or decimal with optional fraction/exponent, then an optional suffix.
NUMBER = re.compile(
    r"(?<![\w.])("
    r"0[xX][0-9A-Fa-f_]+|0[bB][01_]+|"
    r"(?:\d[\d_]*(?:\.\d[\d_]*)?|\.\d[\d_]*)(?:[eE][+-]?\d+)?"
    r")([uUlLfFdDmM]{0,2})(?![\w])")
NEW_ARRAY = re.compile(r"new\s+[\w.<>,\s]+$")


def strip_code(text):
    """Returns the source with comments, strings and chars blanked (newlines kept), so line numbers still match."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", text[i:j]))
            i = j
        elif c == '"' or (c in "@$" and nxt in '"@$'):
            # regular, verbatim (@"") and interpolated ($"", $@"", @$"") strings
            k = i
            verbatim = False
            while k < n and text[k] in "@$":
                verbatim = verbatim or text[k] == "@"
                k += 1
            if k >= n or text[k] != '"':
                out.append(c)
                i += 1
                continue
            k += 1
            while k < n:
                if verbatim and text[k] == '"' and k + 1 < n and text[k + 1] == '"':
                    k += 2
                    continue
                if not verbatim and text[k] == "\\":
                    k += 2
                    continue
                if text[k] == '"' or (not verbatim and text[k] == "\n"):
                    k += 1
                    break
                k += 1
            out.append(re.sub(r"[^\n]", " ", text[i:k]))
            i = k
        elif c == "'":
            k = i + 1
            while k < n and text[k] != "'" and text[k] != "\n":
                k += 2 if text[k] == "\\" else 1
            k = min(n, k + 1)
            out.append(re.sub(r"[^\n]", " ", text[i:k]))
            i = k
        else:
            out.append(c)
            i += 1
    return "".join(out)


def value_of(token):
    t = token.replace("_", "")
    try:
        if t[:2] in ("0x", "0X"):
            return float(int(t[2:], 16))
        if t[:2] in ("0b", "0B"):
            return float(int(t[2:], 2))
        return float(t)
    except ValueError:
        return None


def is_index(code_line, start, end):
    """True for a literal that is the whole content of [ ] after an expression, not an array size."""
    before = code_line[:start].rstrip()
    after = code_line[end:].lstrip()
    if not before.endswith("[") or not after.startswith("]"):
        return False
    head = before[:-1].rstrip()
    return bool(head) and not NEW_ARRAY.search(head) and (head[-1].isalnum() or head[-1] in "_])")


def violations_in(path, rel):
    with open(path, encoding="utf-8-sig") as f:
        raw = f.read()
    raw_lines = raw.split("\n")
    code_lines = strip_code(raw).split("\n")
    hits = []
    in_block = False
    for n, (line, code) in enumerate(zip(raw_lines, code_lines), 1):
        if OK_BEGIN.search(line):
            in_block = True
            continue
        if OK_END.search(line):
            in_block = False
            continue
        if in_block or OK.search(line) or any(s in code for s in SCALE_NAMES):
            continue
        for m in NUMBER.finditer(code):
            v = value_of(m.group(1))
            if v in ALLOWED_VALUES or is_index(code, m.start(), m.end()):
                continue
            hits.append(f"{rel}:{n}: {m.group(0)}")
    return hits


def violations(root):
    hits = []
    base = os.path.join(root, SIM_DIR)
    for dirpath, dirs, files in os.walk(base):
        dirs[:] = sorted(d for d in dirs if d not in ("bin", "obj"))
        for name in sorted(files):
            if name.endswith(".cs"):
                path = os.path.join(dirpath, name)
                hits.extend(violations_in(path, os.path.relpath(path, root)))
    return hits


def self_test():
    with tempfile.TemporaryDirectory() as root:
        def write(rel, text):
            p = os.path.join(root, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w", encoding="utf-8") as f:
                f.write(text)

        write(SIM_DIR + "/Ok.cs", "\n".join([
            "// 1.07 in a comment is fine",
            "/* so is 3.5 here */",
            "class Ok {",
            "  string s = \"a 1.07 string\"; string v = @\"x \"\"9\"\" y\"; char c = '7';",
            "  int a = 0, b = 1, d = -1, e = 2; double f = 1.0, g = 0.0; long h = 1L; ulong u = 2UL;",
            "  int i = cells[3]; int j = grid[i][4]; int k = M()[5];",
            "  int unit = k.AttackProgressUnit * 100;",
            "  const int H = 3600;   // literal-ok: unit conversion",
            "  // literal-ok-begin: a table",
            "  static readonly uint[] K = { 0x428a2f98, 0x71374491 };",
            "  // literal-ok-end",
            "  int x1 = Sha256(F64(I32));",
            "}",
        ]) + "\n")
        assert violations(root) == [], violations(root)

        write(SIM_DIR + "/Bad.cs", "\n".join([
            "class Bad {",
            "  double x = 1.07;",
            "  int[] a = new int[8];",
            "  long m = 0xFF;",
            "  double y = .5 * 3e2;",
            "  int z = 10;   // literal-ok:",
            "}",
        ]) + "\n")
        hits = violations(root)
        expected = ["Bad.cs:2: 1.07", "Bad.cs:3: 8", "Bad.cs:4: 0xFF", "Bad.cs:5: .5", "Bad.cs:5: 3e2", "Bad.cs:6: 10"]
        for e in expected:
            assert any(h.endswith(e) for h in hits), (e, hits)
        assert len(hits) == len(expected), hits
    print("check_sim_literals self-test OK")


def main():
    if "--self-test" in sys.argv[1:]:
        self_test()
        return 0
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    hits = violations(root)
    if hits:
        print("Numeric literals in HeroCat.Sim (specs/09 acceptance 1). Read the value from ContentDb, or annotate a")
        print("non-balance literal with `// literal-ok: <reason>`:")
        for h in hits:
            print("  " + h)
        return 1
    print("check_sim_literals: no balance literals in " + SIM_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main())
