#!/usr/bin/env bash
# Run every check in the repo. Agents and humans run this before calling a change done.
#
#   ./check.sh            everything (what CI runs, see specs/10 section 6.2, specs/00 decision 59)
#   ./check.sh --fast     inner loop: skips golden tests, the bench and the acceptance sweep
#   ./check.sh --no-dotnet  only the content check (for machines without the .NET SDK)
#
# Exits non-zero on the first failure.
set -euo pipefail
cd "$(dirname "$0")"

FAST=0
DOTNET=1
# M0 rewrite (specs/00 decision 33): the old sim is being replaced, so the day-stage acceptance sweep and the
# golden files cannot pass until the new loop lands. The user agreed to run them report-only until then.
# TODO(#136, #138): set to 0 (hard gates again) when the new acceptance gate and goldens land.
REWRITE_REPORT_ONLY=1
REPORT_ONLY_FAILED=0
for arg in "$@"; do
  case "$arg" in
    --fast) FAST=1 ;;
    --no-dotnet) DOTNET=0 ;;
    -h|--help) sed -n 2,7p "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }

step "Content: content_src builds to content/ with no drift"
python3 tools/content_build.py --check

if [ "$DOTNET" -eq 0 ]; then
  printf '\nSkipped .NET checks (--no-dotnet). Not a full pass.\n'
  exit 0
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Install the .NET SDK, or run with --no-dotnet for a partial check." >&2
  exit 1
fi

step ".NET: build"
dotnet build HeroCat.sln -c Release

BOTS="dotnet run -c Release --no-build --project src/HeroCat.Bots --"

if [ "$FAST" -eq 1 ]; then
  step ".NET: unit tests (no golden)"
  dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Golden"
else
  # Report-only steps: run, print, but do not stop the run while REWRITE_REPORT_ONLY=1.
  soft() {
    if [ "$REWRITE_REPORT_ONLY" -eq 1 ]; then
      if ! "$@"; then
        REPORT_ONLY_FAILED=1
        printf '\nWARNING: report-only step failed (M0 rewrite, TODO(#136)). Not a full pass.\n'
      fi
    else
      "$@"
    fi
  }

  if [ "$REWRITE_REPORT_ONLY" -eq 1 ]; then
    step ".NET: unit tests (golden tests report-only during the M0 rewrite)"
    dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Golden"
    step ".NET: golden tests (report-only)"
    soft dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory=Golden"
  else
    step ".NET: all tests"
    dotnet test HeroCat.sln -c Release --no-build
  fi

  step "Bots: tick benchmark"
  $BOTS bench --ticks 1000000 --max-ms 2000

  step "Bots: acceptance sweep (20 seeds x 30 days)"
  soft $BOTS acceptance --seeds 1-20 --days 30 --time-budget-s 180

  step "Bots: golden files match"
  soft $BOTS golden --check
fi

if [ "$REPORT_ONLY_FAILED" -eq 1 ]; then
  printf '\nHard checks passed; report-only checks FAILED (acceptance/golden, M0 rewrite). Not a full pass.\n'
elif [ "$REWRITE_REPORT_ONLY" -eq 1 ] && [ "$FAST" -eq 0 ]; then
  printf '\nAll checks passed (acceptance and golden are report-only during the M0 rewrite).\n'
else
  printf '\nAll checks passed.\n'
fi
