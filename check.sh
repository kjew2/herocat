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

step "Content: exporter tests, and content_src builds to content/ (and the Sim DTOs) with no drift"
python3 tools/test_export_content.py
python3 tools/export_content.py check

step "Tools: fish_model.py S-constant checks (05 section 9)"
python3 tools/fish_model.py --quiet

step "Clock purity: no clock reads in HeroCat.Sim, only ServerClock.cs/SessionRunner.cs in HeroCat.Runtime (specs/02 AC 18)"
python3 tools/check_clock_apis.py --self-test
python3 tools/check_clock_apis.py

step "Summons: tools/gacha_model.py matches content/, Fixtures/Summon/stream.json and vectors.json (04, decision 46)"
python3 tools/gacha_model.py --check

step "Release: build-android.yml helpers (version.json, size gate)"
python3 .github/scripts/test_android_release.py

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

step "Client: Unity-only Boot/ and Screens/ type-check against API stubs (tools/unity_typecheck; Unity itself cannot run here)"
dotnet build tools/unity_typecheck/UnityTypecheck.csproj -c Release -nologo -v q

BOTS="dotnet run -c Release --no-build --project src/HeroCat.Bots --"

step "Saves: the v1 fixture matches what the bot CLI generates (until SaveSchema.V1Frozen)"
$BOTS save-fixture --check

step "Summons: Fixtures/Summon/stream.json matches what the bot CLI generates (the C# side of issue #14)"
$BOTS summon-stream --check

if [ "$FAST" -eq 1 ]; then
  step ".NET: unit tests (no golden, no perf)"
  dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Golden&TestCategory!=Perf"
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
    dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Golden&TestCategory!=Perf"
    step ".NET: golden tests (report-only)"
    soft dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory=Golden"
  else
    step ".NET: all tests but perf"
    dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Perf"
  fi

  # Its own step so a perf flake reads as one (specs/10 section 1.1, issue #139).
  step ".NET: perf tests"
  dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory=Perf"

  step "Bots: tick benchmark"
  $BOTS bench --ticks 1000000 --stage 300 --max-ms 2000

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
