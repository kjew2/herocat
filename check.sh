#!/usr/bin/env bash
# Run every check in the repo. Agents and humans run this before calling a change done.
#
#   ./check.sh            everything (what CI runs, see specs/09 section 13)
#   ./check.sh --fast     inner loop: skips golden tests, the bench and the acceptance sweep
#   ./check.sh --no-dotnet  only the content check (for machines without the .NET SDK; the web build needs .NET too)
#   ./check.sh --no-wasm    skip the WebAssembly build and browser test (full mode runs them when the
#                           wasm-tools workload is installed; see web/wasm/BRIDGE.md)
#
# Exits non-zero on the first failure.
set -euo pipefail
cd "$(dirname "$0")"

FAST=0
DOTNET=1
WASM=1
for arg in "$@"; do
  case "$arg" in
    --fast) FAST=1 ;;
    --no-dotnet) DOTNET=0 ;;
    --no-wasm) WASM=0 ;;
    -h|--help) sed -n 2,10p "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }

step "Content: content_src builds to Content/ with no drift"
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
  step ".NET: all tests"
  dotnet test HeroCat.sln -c Release --no-build

  step "Bots: tick benchmark"
  $BOTS bench --ticks 1000000 --max-ms 2000

  step "Bots: acceptance sweep (20 seeds x 30 days)"
  $BOTS acceptance --seeds 1-20 --days 30 --time-budget-s 180

  step "Bots: golden files match"
  $BOTS golden --check

  if [ "$WASM" -eq 1 ] && dotnet workload list 2>/dev/null | grep -q wasm-tools; then
    step "Web: C# core builds to WebAssembly"
    bash web/wasm/build.sh --fast

    step "Web: page plays on the C# engine in headless Chromium"
    node web/wasm/test/run-wasm.js
  else
    printf '\nSkipped WebAssembly checks (--no-wasm or wasm-tools workload not installed). Not a full pass.\n'
  fi
fi

printf '\nAll checks passed.\n'
