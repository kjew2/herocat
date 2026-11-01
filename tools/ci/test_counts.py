#!/usr/bin/env python3
"""Test-count guard (specs/10 section 1 and acceptance criterion 2, issue #140).

    python3 tools/ci/test_counts.py --trx-dir DIR [--baseline tests/counts.json] [--min-ratio 0.9]
    python3 tools/ci/test_counts.py --trx-dir DIR --update
    python3 tools/ci/test_counts.py --self-test

Reads every .trx file under DIR (written by `dotnet test --logger trx`), counts the executed tests per test project
(the assembly a test came from; NotExecuted results such as [Ignore] do not count) and compares the counts with the
committed baseline `tests/counts.json`. It fails when:

- a project in the baseline ran fewer than min-ratio x its baseline count (someone deleted tests to go green),
- a project in the baseline did not run at all, or
- a project ran that the baseline does not list (a new test project must be added to the baseline).

When a project ran more tests than its baseline it prints a NOTE: rerun with --update and commit tests/counts.json in
the PR that adds the tests, so the baseline keeps up. Lowering a count is done the same way and shows in the diff.

--min-ratio defaults to $TEST_COUNT_MIN_RATIO, else 0.90 (specs/10 constant TEST_COUNT_MIN_RATIO). The counts are only
meaningful for the full `./check.sh --pr` test run (golden and perf tests included); `--fast` skips this guard.
Exit codes: 0 ok, 1 guard failed, 2 usage / IO error. Python 3.9+, stdlib only.
"""
import argparse
import contextlib
import io
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_BASELINE = os.path.join(ROOT, "tests", "counts.json")
DEFAULT_MIN_RATIO = 0.90  # TEST_COUNT_MIN_RATIO, specs/10 constants
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def project_of(storage):
    """Assembly path in a TRX UnitTest's storage attribute -> project name (HeroCat.Sim.Tests)."""
    name = storage.replace("\\", "/").rsplit("/", 1)[-1]
    return name[:-4] if name.lower().endswith(".dll") else name


def count_trx(path):
    """{project: executed tests} for one TRX file."""
    root = ET.parse(path).getroot()
    storage = {}
    for ut in root.iter(NS + "UnitTest"):
        # TestMethod's codeBase keeps the assembly's case; vstest lower-cases the storage attribute.
        tm = ut.find(NS + "TestMethod")
        path = tm.get("codeBase") if tm is not None and tm.get("codeBase") else ut.get("storage", "unknown")
        storage[ut.get("id")] = project_of(path)
    counts = {}
    for r in root.iter(NS + "UnitTestResult"):
        if r.get("outcome") == "NotExecuted":
            continue
        p = storage.get(r.get("testId"), "unknown")
        counts[p] = counts.get(p, 0) + 1
    return counts


def count_dir(trx_dir):
    total = {}
    files = []
    for dirpath, _, names in os.walk(trx_dir):
        files += [os.path.join(dirpath, n) for n in names if n.endswith(".trx")]
    for f in sorted(files):
        for p, n in count_trx(f).items():
            total[p] = total.get(p, 0) + n
    return total, len(files)


def compare(actual, baseline, min_ratio):
    """(failures, notes) as lists of lines."""
    failures, notes = [], []
    for p in sorted(baseline):
        want = baseline[p]
        got = actual.get(p)
        if got is None:
            failures.append(f"FAIL {p}: no tests ran (baseline {want})")
        elif got < min_ratio * want:
            failures.append(f"FAIL {p}: {got} tests ran, below {min_ratio:.2f} x baseline {want} "
                            f"(needs >= {min_ratio * want:.1f}); if the drop is intended, run --update and commit tests/counts.json")
        else:
            notes.append(f"OK   {p}: {got} tests (baseline {want})")
            if got > want:
                notes.append(f"NOTE {p}: {got} > baseline {want}; run python3 tools/ci/test_counts.py --update "
                             f"--trx-dir build/check/trx and commit tests/counts.json with the new tests")
    for p in sorted(set(actual) - set(baseline)):
        failures.append(f"FAIL {p}: {actual[p]} tests ran but the project is not in tests/counts.json (run --update)")
    return failures, notes


def write_baseline(path, actual):
    data = {
        "description": "Executed tests per test project in the full ./check.sh --pr run (specs/10 section 1, "
                       "acceptance criterion 2). tools/ci/test_counts.py fails a PR when a project drops below "
                       "TEST_COUNT_MIN_RATIO x its count here. Update with: python3 tools/ci/test_counts.py "
                       "--update --trx-dir build/check/trx (after ./check.sh --pr), in the PR that adds tests.",
        "projects": {p: actual[p] for p in sorted(actual)},
    }
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def run(trx_dir, baseline_path, min_ratio, update):
    if not os.path.isdir(trx_dir):
        print(f"test_counts: no TRX directory {trx_dir}", file=sys.stderr)
        return 2
    actual, nfiles = count_dir(trx_dir)
    if nfiles == 0 or not actual:
        print(f"test_counts: no tests found in TRX files under {trx_dir}", file=sys.stderr)
        return 2
    if update:
        write_baseline(baseline_path, actual)
        print(f"wrote {os.path.relpath(baseline_path, ROOT)}: " + ", ".join(f"{p} {n}" for p, n in sorted(actual.items())))
        return 0
    if not os.path.exists(baseline_path):
        print(f"test_counts: missing {baseline_path}; create it with --update", file=sys.stderr)
        return 2
    with open(baseline_path, encoding="utf-8") as f:
        baseline = json.load(f)["projects"]
    failures, notes = compare(actual, baseline, min_ratio)
    for line in notes + failures:
        print(line)
    print(f"test_counts: {sum(actual.values())} tests in {len(actual)} project(s) from {nfiles} TRX file(s), "
          f"min ratio {min_ratio:.2f}: {'FAIL' if failures else 'OK'}")
    return 1 if failures else 0


# ------------------------------------------------------------------------------------------------ self-test

def _trx(results):
    """A minimal TRX with (project, outcome) results."""
    defs, res = [], []
    for i, (proj, outcome) in enumerate(results):
        dll = f"/x/build/bin/{proj}/Release/net8.0/{proj}.dll"
        defs.append(f'<UnitTest name="t{i}" storage="{dll.lower()}" id="id{i}"><TestMethod codeBase="{dll}" name="t{i}"/>'
                    '</UnitTest>')
        res.append(f'<UnitTestResult testId="id{i}" testName="t{i}" outcome="{outcome}"/>')
    return (f'<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="{NS[1:-1]}"><Results>{"".join(res)}</Results>'
            f'<TestDefinitions>{"".join(defs)}</TestDefinitions></TestRun>')


def self_test():
    with tempfile.TemporaryDirectory() as d, contextlib.redirect_stdout(io.StringIO()), \
            contextlib.redirect_stderr(io.StringIO()):
        trx = os.path.join(d, "trx")
        os.makedirs(trx)
        with open(os.path.join(trx, "a.trx"), "w") as f:
            f.write(_trx([("A.Tests", "Passed")] * 9 + [("A.Tests", "NotExecuted"), ("B.Tests", "Failed")]))
        with open(os.path.join(trx, "b.trx"), "w") as f:  # a second run (perf) adds to the same project
            f.write(_trx([("B.Tests", "Passed")]))
        actual, n = count_dir(trx)
        assert n == 2 and actual == {"A.Tests": 9, "B.Tests": 2}, actual
        base = os.path.join(d, "counts.json")
        assert run(trx, base, 0.9, update=True) == 0
        assert run(trx, base, 0.9, update=False) == 0
        cases = [
            ({"A.Tests": 10, "B.Tests": 2}, 0),   # 9 >= 0.9 x 10
            ({"A.Tests": 11, "B.Tests": 2}, 1),   # 9 < 9.9: tests were deleted
            ({"A.Tests": 9}, 1),                  # B.Tests not in the baseline
            ({"A.Tests": 9, "B.Tests": 2, "C.Tests": 1}, 1),  # C.Tests did not run
            ({"A.Tests": 5, "B.Tests": 1}, 0),    # more than the baseline: a note only
        ]
        for projects, want in cases:
            with open(base, "w") as f:
                json.dump({"projects": projects}, f)
            got = run(trx, base, 0.9, update=False)
            assert got == want, (projects, got, want)
        assert run(os.path.join(d, "missing"), base, 0.9, update=False) == 2
    print("test_counts self-test: OK")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--trx-dir")
    ap.add_argument("--baseline", default=DEFAULT_BASELINE)
    ap.add_argument("--min-ratio", type=float, default=float(os.environ.get("TEST_COUNT_MIN_RATIO", DEFAULT_MIN_RATIO)))
    ap.add_argument("--update", action="store_true", help="rewrite the baseline from the TRX files")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.trx_dir:
        ap.error("--trx-dir is required")
    return run(args.trx_dir, args.baseline, args.min_ratio, args.update)


if __name__ == "__main__":
    sys.exit(main())
