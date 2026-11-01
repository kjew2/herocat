#!/usr/bin/env python3
"""Workflow lint for the check.sh wrappers (specs/10 section 6.2, specs/00 decision 59, issue #140).

    python3 tools/ci/check_workflows.py

- Every .github/workflows/*.yml and .github/actions/*/action.yml parses as YAML (with PyYAML when it is installed;
  without it this part prints SKIP, the rest still runs).
- pr.yml, content.yml and nightly-bots.yml each call their check.sh mode (`./check.sh --pr`, `--content`,
  `--nightly`), and every `./check.sh` call in a workflow uses only options check.sh accepts: the workflows stay
  thin wrappers, so a green ./check.sh means green CI.
- actionlint runs over the workflows when it is on PATH (it is not required).

Exit 0 ok, 1 a check failed. Python 3.9+, stdlib (+ optional PyYAML, actionlint).
"""
import glob
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WRAPPERS = {"pr.yml": "--pr", "content.yml": "--content", "nightly-bots.yml": "--nightly"}


def check_sh_options():
    """The options check.sh's argument loop accepts."""
    with open(os.path.join(ROOT, "check.sh"), encoding="utf-8") as f:
        text = f.read()
    loop = text[text.index('for arg in "$@"'):text.index("done", text.index('for arg in "$@"'))]
    return set(re.findall(r"^\s*(--[a-z-]+)(?:\|[-a-z|]+)?\)", loop, re.M))


def main():
    failures = []
    files = sorted(glob.glob(os.path.join(ROOT, ".github", "workflows", "*.yml")) +
                   glob.glob(os.path.join(ROOT, ".github", "actions", "*", "action.yml")))
    try:
        import yaml  # noqa: F401
    except ImportError:
        yaml = None
        print("SKIP YAML parse: PyYAML not installed (pip install pyyaml to run it)")
    opts = check_sh_options()
    for path in files:
        rel = os.path.relpath(path, ROOT)
        with open(path, encoding="utf-8") as f:
            text = f.read()
        if yaml is not None:
            try:
                doc = yaml.safe_load(text)
                if not isinstance(doc, dict) or not (("jobs" in doc and (True in doc or "on" in doc)) or "runs" in doc):
                    failures.append(f"{rel}: not a workflow (jobs + on) or an action (runs)")
            except yaml.YAMLError as e:
                failures.append(f"{rel}: YAML error: {e}")
        for call in re.findall(r"\./check\.sh([^\n]*)", text):
            for tok in re.findall(r"--[a-z-]+", call):
                if tok not in opts:
                    failures.append(f"{rel}: ./check.sh {tok} is not an option check.sh accepts ({' '.join(sorted(opts))})")
        name = os.path.basename(path)
        if name in WRAPPERS and not re.search(r"\./check\.sh[^\n]*" + re.escape(WRAPPERS[name]) + r"\b", text):
            failures.append(f"{rel}: does not call ./check.sh {WRAPPERS[name]}")
    for name in WRAPPERS:
        if not os.path.exists(os.path.join(ROOT, ".github", "workflows", name)):
            failures.append(f".github/workflows/{name} is missing")
    if shutil.which("actionlint"):
        r = subprocess.run(["actionlint"], cwd=ROOT)
        if r.returncode != 0:
            failures.append("actionlint reported problems (above)")
    else:
        print("SKIP actionlint: not on PATH")
    for line in failures:
        print("FAIL " + line)
    print(f"check_workflows: {len(files)} file(s), {'FAIL' if failures else 'OK'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
