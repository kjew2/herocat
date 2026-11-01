#!/usr/bin/env python3
"""Release helpers for .github/workflows/build-android.yml (specs/10 sections 5 and 6.3). Stdlib only.

  android_release.py version --file unity/HeroCatClient/version.json --build 345
      Prints version=1.2.0, build=345, full=1.2.0+345 and tag=client-v1.2.0+345, one per line
      (append to $GITHUB_OUTPUT). Fails if version.json is not semver MAJOR.MINOR.PATCH.

  android_release.py size --max-mb 150 build/HeroCat.aab
      Fails if any artifact is larger than the budget (SIZE_AAB_MB_MAX). 1 MB = 1,000,000 bytes, the
      stricter reading, so a file that passes here also passes a MiB-based limit.

Tested by .github/scripts/test_android_release.py, which ./check.sh runs.
"""
import argparse
import json
import os
import re
import sys

SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
BYTES_PER_MB = 1_000_000
SIZE_AAB_MB_MAX = 150  # specs/10 section 5 and constants table


class ReleaseError(Exception):
    pass


def read_version(path):
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        raise ReleaseError(f"{path}: {e}")
    version = data.get("version") if isinstance(data, dict) else None
    if not isinstance(version, str) or not SEMVER.match(version):
        raise ReleaseError(f'{path}: "version" must be MAJOR.MINOR.PATCH, got {version!r}')
    return version


def version_outputs(version, build):
    if not re.fullmatch(r"[1-9]\d*", str(build)):
        raise ReleaseError(f"build number must be a positive integer, got {build!r}")
    full = f"{version}+{build}"
    return {"version": version, "build": str(build), "full": full, "tag": f"client-v{full}"}


def check_sizes(paths, max_mb):
    """Returns (ok, report lines). Missing files and empty lists fail."""
    if max_mb <= 0:
        raise ReleaseError(f"--max-mb must be positive, got {max_mb}")
    if not paths:
        return False, ["no artifacts given"]
    limit = max_mb * BYTES_PER_MB
    ok, lines = True, []
    for p in paths:
        if not os.path.isfile(p):
            ok = False
            lines.append(f"FAIL {p}: not found")
            continue
        size = os.path.getsize(p)
        verdict = "ok  " if size <= limit else "FAIL"
        ok &= size <= limit
        lines.append(f"{verdict} {p}: {size / BYTES_PER_MB:.2f} MB ({size} bytes), budget {max_mb:g} MB")
    return ok, lines


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    v = sub.add_parser("version")
    v.add_argument("--file", required=True)
    v.add_argument("--build", required=True)
    s = sub.add_parser("size")
    s.add_argument("--max-mb", type=float, default=SIZE_AAB_MB_MAX)
    s.add_argument("paths", nargs="*")
    args = ap.parse_args(argv)
    try:
        if args.cmd == "version":
            for k, val in version_outputs(read_version(args.file), args.build).items():
                print(f"{k}={val}")
            return 0
        ok, lines = check_sizes(args.paths, args.max_mb)
        for line in lines:
            print(line)
        if not ok:
            print("artifact size gate failed (specs/10 section 5)", file=sys.stderr)
        return 0 if ok else 1
    except ReleaseError as e:
        print(f"error: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
