#!/usr/bin/env bash
# Run every check in the repo. Agents and humans run this before calling a change done.
# check.sh is the single source of truth for CI (specs/10 section 6.2, specs/00 decision 59): each workflow in
# .github/workflows calls one mode, and a new kind of check goes into a mode here, never inline in a workflow.
#
#   ./check.sh              everything CI runs: --pr, then --content
#   ./check.sh --pr         pr.yml: build -warnaserror, tests, perf, bench, golden, fixtures, purity, test-count guard
#   ./check.sh --content    content.yml: exporter rebuild + diff, model oracles, acceptance sweep, layer shares, golden
#   ./check.sh --nightly    nightly-bots.yml: bot sweep CSV, longest walls, golden --replay
#   ./check.sh --nightly --replay-only   the Windows/macOS nightly legs: build and golden --replay only
#   ./check.sh --fast       inner loop (combines with a mode): skips golden, perf, bench, acceptance and the count guard
#   ./check.sh --no-dotnet  only the Python checks of the chosen modes (machines without the .NET SDK; partial)
#
# Outputs (acceptance table, shares, diffs, TRX, sweep CSV) land in $CHECK_OUT_DIR (default build/check/).
# Exits non-zero on the first hard failure; report-only steps print a warning and the summary says so.
set -euo pipefail
cd "$(dirname "$0")"

FAST=0
DOTNET=1
REPLAY_ONLY=0
MODE_PR=0
MODE_CONTENT=0
MODE_NIGHTLY=0
# M0 rewrite (specs/00 decision 33): the user agreed to run the acceptance sweep report-only until the new bots meet
# the day-stage targets. The golden files are regenerated from the new bots and are a hard gate again (#138).
# TODO(#136): set to 0 (acceptance a hard gate again) once the greedy-f2p sweep passes honestly. Offline catch-up
# (#108) has landed and the sweep still misses AC1/AC3/AC4; it waits on the balance decision #249.
REWRITE_REPORT_ONLY=1
REPORT_ONLY_FAILED=0
for arg in "$@"; do
  case "$arg" in
    --pr) MODE_PR=1 ;;
    --content) MODE_CONTENT=1 ;;
    --nightly) MODE_NIGHTLY=1 ;;
    --replay-only) REPLAY_ONLY=1 ;;
    --fast) FAST=1 ;;
    --no-dotnet) DOTNET=0 ;;
    -h|--help) sed -n 2,15p "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done
if [ "$MODE_PR" -eq 0 ] && [ "$MODE_CONTENT" -eq 0 ] && [ "$MODE_NIGHTLY" -eq 0 ]; then
  MODE_PR=1
  MODE_CONTENT=1
fi
if [ "$REPLAY_ONLY" -eq 1 ] && [ "$MODE_NIGHTLY" -eq 0 ]; then
  echo "--replay-only goes with --nightly" >&2; exit 2
fi

OUT_DIR="${CHECK_OUT_DIR:-build/check}"
mkdir -p "$OUT_DIR"

step() { printf '\n== %s\n' "$1"; }

# Python: python3 everywhere; the Windows nightly leg only has `python` (and runs no Python step in --replay-only).
PY=python3
command -v python3 >/dev/null 2>&1 || PY=python

# --------------------------------------------------------------------------------------------- Python checks

POWER_CURVE_FAILED=0

pr_python() {
  step "Content: exporter tests, and content_src builds to content/ (and the Sim DTOs) with no drift"
  $PY tools/test_export_content.py
  $PY tools/test_content.py
  $PY tools/test_power_curve_model.py
  $PY tools/export_content.py check

  step "Clock purity: no clock reads in HeroCat.Sim, only ServerClock.cs/SessionRunner.cs in HeroCat.Runtime (specs/02 AC 18)"
  $PY tools/check_clock_apis.py --self-test
  $PY tools/check_clock_apis.py

  step "Sim purity: no banned APIs in HeroCat.Sim, no Sim dependency, no Unity in Runtime/Remote (specs/01 AC 1, issue #63)"
  $PY tools/check_sim_purity.py --self-test
  $PY tools/check_sim_purity.py

  step "Sim literals: no balance literal in HeroCat.Sim (specs/09 acceptance 1, issue #131)"
  $PY tools/check_sim_literals.py --self-test
  $PY tools/check_sim_literals.py

  step "Release: build-android.yml helpers (version.json, size gate)"
  $PY .github/scripts/test_android_release.py

  step "CI: tools/ci self-tests (test-count guard, PR comment) and workflow YAML parses (specs/10 section 6.2)"
  $PY tools/ci/test_counts.py --self-test
  $PY tools/ci/pr_comment.py --self-test
  $PY tools/ci/check_workflows.py
}

content_python() {
  step "Content: exporter rebuilds content_src into a scratch dir that diffs clean against content/ (specs/10 section 6.2)"
  rm -rf "$OUT_DIR/content" "$OUT_DIR/exporter.diff"
  mkdir -p "$OUT_DIR/content"
  $PY tools/export_content.py build --out "$OUT_DIR/content" --dto-out "$OUT_DIR/ContentDtos.g.cs" >/dev/null
  if ! { diff -r "$OUT_DIR/content" content && diff "$OUT_DIR/ContentDtos.g.cs" src/HeroCat.Sim/Content/Dto/ContentDtos.g.cs; } \
      > "$OUT_DIR/exporter.diff" 2>&1; then
    head -n 200 "$OUT_DIR/exporter.diff"
    echo "content/ differs from a fresh build of content_src (full diff: $OUT_DIR/exporter.diff); run python3 tools/export_content.py build" >&2
    return 1
  fi
  echo "content/ and the DTO file match a fresh build"

  step "Tools: fish_model.py S-constant checks (05 section 9)"
  $PY tools/fish_model.py --quiet --out "$OUT_DIR/fish"

  step "Summons: tools/gacha_model.py matches content/, Fixtures/Summon/stream.json and vectors.json (04, decision 46)"
  $PY tools/gacha_model.py --check

  # Report-only: several of 03 section 8.6's S-constant bands miss under the current content (layer floors, walls,
  # Awakening cadence, ...). Retuning is the user's balance call; make this a hard gate once the checks pass.
  if [ "$FAST" -eq 0 ]; then
    step "Tools: power_curve_model.py --check, 20-seed sweep and S-constant checks (03 section 8.6; report-only)"
    if ! $PY tools/power_curve_model.py --check --quiet; then
      POWER_CURVE_FAILED=1
      printf '\nWARNING: report-only step failed (power_curve_model.py S-constant bands, balance call pending). Not a full pass.\n'
    fi
  fi
}

# --------------------------------------------------------------------------------------------- .NET checks

BOTS="dotnet run -c Release --no-build --project src/HeroCat.Bots --"
BUILT=0
GOLDEN_DONE=0

need_dotnet() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found. Install the .NET SDK, or run with --no-dotnet for a partial check." >&2
    exit 1
  fi
}

build() {
  if [ "$BUILT" -eq 1 ]; then return 0; fi
  step ".NET: restore, and build with warnings as errors (specs/10 section 6.2)"
  dotnet restore HeroCat.sln
  dotnet build HeroCat.sln -c Release --no-restore -warnaserror
  BUILT=1
}

# Report-only steps: run, print, but do not stop the run while REWRITE_REPORT_ONLY=1.
soft() {
  if [ "$REWRITE_REPORT_ONLY" -eq 1 ]; then
    if ! "$@"; then
      REPORT_ONLY_FAILED=1
      printf '\nWARNING: report-only step failed (until the balance decision #249, TODO(#136)). Not a full pass.\n'
    fi
  else
    "$@"
  fi
}

# Runs a command, tees its stdout+stderr to a file in $OUT_DIR, and keeps the command's exit status.
to_file() {
  local file="$1"; shift
  "$@" 2>&1 | tee "$OUT_DIR/$file"
}

golden_check() {
  if [ "$GOLDEN_DONE" -eq 1 ]; then return 0; fi
  step "Bots: golden files match and the seed-7 command log replays to its day hashes (specs/10 section 2)"
  to_file golden.txt $BOTS golden --check
  GOLDEN_DONE=1
}

pr_dotnet() {
  build

  step "Client: Unity-only Boot/ and Screens/ type-check against API stubs (tools/unity_typecheck; Unity itself cannot run here)"
  dotnet build tools/unity_typecheck/UnityTypecheck.csproj -c Release -nologo -v q

  step "Saves: the v1 fixture matches what the bot CLI generates (until SaveSchema.V1Frozen)"
  $BOTS save-fixture --check

  step "Summons: Fixtures/Summon/stream.json matches what the bot CLI generates (the C# side of issue #14)"
  $BOTS summon-stream --check

  step "Fish Board: Fixtures/Fish/board-vectors.txt matches what the bot CLI generates (05 AC5, issue #103)"
  $BOTS fish-vectors --check

  if [ "$FAST" -eq 1 ]; then
    step ".NET: unit tests (no golden, no perf)"
    dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Golden&TestCategory!=Perf"
    return 0
  fi

  rm -rf "$OUT_DIR/trx"
  step ".NET: all tests but perf (golden tests included, specs/10 section 2)"
  dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory!=Perf" \
    --logger "trx;LogFilePrefix=main" --results-directory "$OUT_DIR/trx/main"

  # Its own step so a perf flake reads as one (specs/10 section 1.1, issue #139).
  step ".NET: perf tests"
  dotnet test HeroCat.sln -c Release --no-build --filter "TestCategory=Perf" \
    --logger "trx;LogFilePrefix=perf" --results-directory "$OUT_DIR/trx/perf"

  step "Tests: executed test count per project >= TEST_COUNT_MIN_RATIO x tests/counts.json (specs/10 section 1, criterion 2)"
  $PY tools/ci/test_counts.py --trx-dir "$OUT_DIR/trx"

  step "Bots: tick benchmark"
  $BOTS bench --ticks 1000000 --stage 300 --max-ms 2000

  golden_check
}

content_dotnet() {
  build
  step "Fish Board: the C# attentive policy matches fish_model.py's day-7 fishMult within 5% (05 AC14, issue #105)"
  $BOTS fish-check --model "$OUT_DIR/fish/fish_attentive.json"
  if [ "$FAST" -eq 1 ]; then return 0; fi

  rm -f "$OUT_DIR/acceptance.report-only"
  if [ "$REWRITE_REPORT_ONLY" -eq 1 ]; then
    echo "the acceptance sweep is report-only until the balance decision #249 (TODO(#136)); it does not block merge yet" \
      > "$OUT_DIR/acceptance.report-only"
  fi
  # GNU timeout guards a hung sweep (specs/10 section 6.2); macOS has none, where the bots' own time budget applies.
  local TIMEOUT=()
  if command -v timeout >/dev/null 2>&1; then TIMEOUT=(timeout 400); fi

  step "Bots: acceptance sweep (greedy-f2p, 20 seeds x 30 days, tier M0; report-only until #249)"
  soft to_file acceptance.txt ${TIMEOUT[@]+"${TIMEOUT[@]}"} $BOTS acceptance --tier M0 --seeds 1-20 --days 30 --time-budget-s 240 --table --verbose

  step "Bots: M0 exit criterion, two weeks of simulated play (specs/00 section 11, specs/10 section 3.8; report-only)"
  soft to_file acceptance-m0-14d.txt ${TIMEOUT[@]+"${TIMEOUT[@]}"} $BOTS acceptance --tier M0 --days 14 --time-budget-s 240

  step "Bots: layer shares, 20 seeds x 30 days (specs/10 section 6.2; informational, AC2 gates them)"
  $BOTS report --layer-shares --seeds 1-20 --days 30 > "$OUT_DIR/shares.txt"
  echo "wrote $OUT_DIR/shares.txt"

  golden_check
}

nightly_dotnet() {
  build
  step "Bots: golden --replay, the seed-7 command log replays to its day hashes on this OS (specs/10 section 6.2)"
  to_file golden-replay.txt $BOTS golden --replay
  step "Fish Board: the board vectors regenerate byte-identically on this OS (05 AC5, issue #103)"
  $BOTS fish-vectors --check
  if [ "$REPLAY_ONLY" -eq 1 ]; then return 0; fi

  # TODO(#214, #215): sweep lazy-f2p, light-spender and whale too once those bots exist (specs/10 section 6.2).
  step "Bots: greedy-f2p sweep, seeds 1-40 x 30 days, CSV for the balance dashboard (specs/10 section 6.2)"
  $BOTS sweep --bot greedy-f2p --seeds 1-40 --days 30 --format csv --out "$OUT_DIR/sweep-greedy-f2p.csv"
  echo "wrote $OUT_DIR/sweep-greedy-f2p.csv"

  step "Bots: longest walls, 20 seeds x 30 days (the diff against the previous night is not automated yet)"
  $BOTS report --longest-walls --seeds 1-20 --days 30 > "$OUT_DIR/longest-walls.txt"
  echo "wrote $OUT_DIR/longest-walls.txt"
}

# --------------------------------------------------------------------------------------------- run

# Plain `if` blocks, not `[ ] && f`: bash ignores set -e inside a function called from an && list.
if [ "$MODE_PR" -eq 1 ]; then pr_python; fi
if [ "$MODE_CONTENT" -eq 1 ]; then content_python; fi

if [ "$DOTNET" -eq 0 ]; then
  printf '\nSkipped .NET checks (--no-dotnet). Not a full pass.\n'
  [ "$POWER_CURVE_FAILED" -eq 1 ] && printf 'Report-only: power_curve_model.py --check FAILED (balance call pending).\n'
  exit 0
fi

need_dotnet
if [ "$MODE_PR" -eq 1 ]; then pr_dotnet; fi
if [ "$MODE_CONTENT" -eq 1 ]; then content_dotnet; fi
if [ "$MODE_NIGHTLY" -eq 1 ]; then nightly_dotnet; fi

if [ "$POWER_CURVE_FAILED" -eq 1 ]; then
  printf '\nHard checks passed; report-only power_curve_model.py --check FAILED (03 section 8.6 bands, balance call pending). Not a full pass.\n'
fi
if [ "$REPORT_ONLY_FAILED" -eq 1 ]; then
  printf '\nHard checks passed; the report-only acceptance sweep FAILED (until the balance decision #249, TODO(#136)). Not a full pass.\n'
elif [ "$POWER_CURVE_FAILED" -eq 1 ]; then
  :
elif [ "$REWRITE_REPORT_ONLY" -eq 1 ] && [ "$FAST" -eq 0 ] && [ "$MODE_CONTENT" -eq 1 ]; then
  printf '\nAll checks passed (the acceptance sweep is report-only until the balance decision #249).\n'
else
  printf '\nAll checks passed.\n'
fi
