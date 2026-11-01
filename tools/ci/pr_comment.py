#!/usr/bin/env python3
"""Post the acceptance table and the day-30 median into the PR (specs/10 section 6.2 content.yml, issue #140).

    python3 tools/ci/pr_comment.py --acceptance build/check/acceptance.txt [--shares build/check/shares.txt] [--dry-run]
    python3 tools/ci/pr_comment.py --self-test

Reads the output of `HeroCat.Bots acceptance --table --verbose` written by `./check.sh --content`, builds a Markdown
comment (the table, the day-30 median stage, every PASS/FAIL/WARN line without the per-seed details, the RESULT line)
and creates or edits one comment on the pull request, found by a hidden marker, so reruns do not pile up comments.

Needs GITHUB_TOKEN (pull-requests: write), GITHUB_REPOSITORY and GITHUB_EVENT_PATH (a pull_request event), which
GitHub Actions sets. --dry-run, or a missing token or PR, prints the body instead. The comment is informational: the
content job's own status is what blocks merge, so an API failure (a fork PR's read-only token) only warns, exit 0.
Python 3.9+, stdlib only.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request

MARKER = "<!-- herocat-acceptance-comment -->"
CHECK_PREFIXES = ("PASS ", "FAIL ", "WARN ", "RESULT ")


def parse(text):
    """(table lines, day-30 median stage or None, check lines) from acceptance --table --verbose output."""
    lines = text.splitlines()
    table, checks, in_table = [], [], False
    for ln in lines:
        if ln.startswith("# herocat acceptance table"):
            in_table = True
            table.append(ln)
            continue
        if in_table:
            if ln.startswith(CHECK_PREFIXES) or not ln.strip() or ln.startswith("#"):
                in_table = False
            else:
                table.append(ln)
                continue
        if ln.startswith(CHECK_PREFIXES):
            checks.append(ln)
    median30 = None
    if len(table) > 1:
        header = table[1].split()
        col = header.index("stageMed") if "stageMed" in header else None
        for row in table[2:]:
            cells = row.split()
            if col is not None and cells and cells[0] == "30" and len(cells) > col:
                median30 = cells[col]
    return table, median30, checks


def body(acceptance_text, shares_text=None, run_url=None, report_only=None):
    table, median30, checks = parse(acceptance_text)
    fails = sum(1 for c in checks if c.startswith("FAIL "))
    out = [MARKER, "### Bot acceptance (`./check.sh --content`)", ""]
    if report_only:
        out += [f"> Report-only: {report_only.strip()}", ""]
    out.append(f"**Day-30 median stage: {median30}**" if median30 else "**Day-30 median stage: not in the table**")
    out.append("")
    out.append(f"{fails} failing check(s)." if fails else "All acceptance checks pass.")
    out.append("")
    if table:
        out += ["```", *table, "```", ""]
    if checks:
        out += ["<details><summary>Checks</summary>", "", "```", *checks, "```", "", "</details>", ""]
    if shares_text and shares_text.strip():
        out += ["<details><summary>Layer shares</summary>", "", "```", *shares_text.rstrip().splitlines(), "```",
                "", "</details>", ""]
    if run_url:
        out.append(f"Artifacts (acceptance table, shares, golden and exporter diffs): {run_url}")
    return "\n".join(out) + "\n"


def api(method, url, token, payload=None):
    req = urllib.request.Request(url, method=method, data=None if payload is None else json.dumps(payload).encode())
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    if payload is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read() or b"null")


def post(text):
    token, repo, event_path = (os.environ.get(k) for k in ("GITHUB_TOKEN", "GITHUB_REPOSITORY", "GITHUB_EVENT_PATH"))
    pr = None
    if event_path and os.path.exists(event_path):
        with open(event_path, encoding="utf-8") as f:
            pr = (json.load(f).get("pull_request") or {}).get("number")
    if not (token and repo and pr):
        print("pr_comment: no token, repository or pull request; printing the comment instead\n")
        print(text)
        return 0
    api_root = os.environ.get("GITHUB_API_URL", "https://api.github.com")
    try:
        existing = None
        page = 1
        while existing is None:
            comments = api("GET", f"{api_root}/repos/{repo}/issues/{pr}/comments?per_page=100&page={page}", token)
            if not comments:
                break
            existing = next((c for c in comments if MARKER in (c.get("body") or "")), None)
            page += 1
        if existing:
            api("PATCH", f"{api_root}/repos/{repo}/issues/comments/{existing['id']}", token, {"body": text})
            print(f"pr_comment: updated comment {existing['id']} on #{pr}")
        else:
            c = api("POST", f"{api_root}/repos/{repo}/issues/{pr}/comments", token, {"body": text})
            print(f"pr_comment: posted comment {c.get('id')} on #{pr}")
    except (urllib.error.URLError, OSError) as e:
        print(f"::warning::pr_comment: could not post the acceptance comment ({e}); the job summary has it")
    return 0


SAMPLE = """# herocat acceptance v3
# bot=greedy-f2p seeds=1-20 days=30 tier=M0 content=2026.11.01/c7430a6b
# herocat acceptance table   bot=greedy-f2p seeds=1-20 days=30 content=2026.11.01/c7430a6b
day  stageMed  goldEarnedMed  diaEarnedMed
  1       191        1.7e+06          1740
 30       360        5.4e+10          3400
FAIL AC1 day 1: median stage 191 outside 192..288 (target 240)
     seed 1: stage 220
PASS AC2 day 1 ceiling: max share 27.0% (HeroLevel, seed 12) <= 40.0%
RESULT 1 failure(s), 0 warning(s)
"""


def self_test():
    table, median30, checks = parse(SAMPLE)
    assert len(table) == 4 and median30 == "360", (table, median30)
    assert checks == [l for l in SAMPLE.splitlines() if l.startswith(CHECK_PREFIXES)], checks
    b = body(SAMPLE, "shares", "https://example/run")
    assert b.startswith(MARKER) and "Day-30 median stage: 360" in b and "1 failing check(s)" in b
    assert "seed 1: stage 220" not in b and "Layer shares" in b
    assert "> Report-only: until #249" in body(SAMPLE, report_only="until #249\n")
    assert "not in the table" in body("RESULT 0 failure(s), 0 warning(s)\n")
    print("pr_comment self-test: OK")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--acceptance")
    ap.add_argument("--shares")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.acceptance or not os.path.exists(args.acceptance):
        print(f"pr_comment: no acceptance output at {args.acceptance}; nothing to post")
        return 0
    with open(args.acceptance, encoding="utf-8") as f:
        acceptance = f.read()
    shares = None
    if args.shares and os.path.exists(args.shares):
        with open(args.shares, encoding="utf-8") as f:
            shares = f.read()
    run_url = None
    if os.environ.get("GITHUB_RUN_ID"):
        run_url = (f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/{os.environ.get('GITHUB_REPOSITORY')}"
                   f"/actions/runs/{os.environ['GITHUB_RUN_ID']}")
    # ./check.sh writes acceptance.report-only next to the output while the sweep does not gate (REWRITE_REPORT_ONLY)
    report_only = None
    flag = os.path.join(os.path.dirname(os.path.abspath(args.acceptance)), "acceptance.report-only")
    if os.path.exists(flag):
        with open(flag, encoding="utf-8") as f:
            report_only = f.read() or "the acceptance sweep does not block merge yet"
    text = body(acceptance, shares, run_url, report_only)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text)
    if args.dry_run:
        print(text)
        return 0
    return post(text)


if __name__ == "__main__":
    sys.exit(main())
