#!/usr/bin/env bash
# Builds the C# engine for the browser and lays it out under web/wasm/dist/ the way web/engine.js expects:
#   dist/_framework/dotnet.js (+ dotnet.native.wasm, HeroCat.Web.dll, ...) and dist/content-pack.json
# Usage: web/wasm/build.sh [--fast] [--aot] [--clean]
#   (default)  Release publish, interpreter (non-AOT), trimmed. About 3 minutes cold.
#   --fast     incremental: keep obj/ between runs (the default also does; --fast just skips the final size report)
#   --aot      WasmAOT (much slower to build, bigger download, ~5-10x faster); not used by the tests
#   --clean    delete build/obj/HeroCat.Web and build/publish first
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROJ="$ROOT/src/HeroCat.Web/HeroCat.Web.csproj"
PUB="$ROOT/build/publish/HeroCat.Web"
DIST="$HERE/dist"
AOT=false; FAST=false; CLEAN=false
for a in "$@"; do
  case "$a" in
    --fast) FAST=true ;;
    --aot) AOT=true ;;
    --clean) CLEAN=true ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
$CLEAN && rm -rf "$ROOT/build/obj/HeroCat.Web" "$PUB"

echo "== content pack"
node "$HERE/pack-content.js" "$ROOT/build/content-pack.json"

echo "== restore + publish (Release, AOT=$AOT)"
START=$(date +%s)
dotnet publish "$PROJ" -c Release -o "$PUB" -p:RunAOTCompilation=$AOT --nologo -v:minimal
echo "publish took $(( $(date +%s) - START )) s"

SRC="$PUB/wwwroot"
[ -d "$SRC/_framework" ] || SRC="$PUB"
[ -f "$SRC/_framework/dotnet.js" ] || { echo "publish output has no _framework/dotnet.js under $PUB" >&2; exit 1; }

echo "== dist"
rm -rf "$DIST"
mkdir -p "$DIST"
cp -r "$SRC/_framework" "$DIST/_framework"
find "$DIST" \( -name '*.br' -o -name '*.gz' \) -delete
cp "$ROOT/build/content-pack.json" "$DIST/content-pack.json"
printf '%s\n' "{\"built\":\"$(date -u +%FT%TZ)\",\"aot\":$AOT}" > "$DIST/build-info.json"

if ! $FAST; then
  RAW=$(find "$DIST" -type f -exec cat {} + | wc -c | tr -d ' ')   # portable (macOS du has no -b)
  GZ=$(tar -C "$DIST" -cf - . | gzip -9 | wc -c)
  echo "dist: $RAW bytes raw, about $GZ bytes as a gzip -9 tar"
  ls -l "$DIST/_framework" | awk '{print $5, $9}' | sort -rn | head -8
fi
echo "done: $DIST"
