#!/usr/bin/env bash
# Build (only if needed) and play the web prototype on the C# engine.
#
#   ./play.sh                 build if src/ or Content/ changed, serve web/, open the browser
#   ./play.sh --rebuild       force a rebuild of the C# bundle
#   ./play.sh --port 8080     serve on another port (default 8000, or the next free one)
#   ./play.sh --no-open       do not open a browser (e.g. on a headless machine)
#   ./play.sh --local         only this computer can connect (default: phones on the same Wi-Fi can too)
#   ./play.sh --check         list what is installed and the command to install anything missing; plays nothing
#
# Stop with Ctrl+C. On Windows run it from Git Bash or WSL.
set -euo pipefail
cd "$(dirname "$0")"

PORT=8000
REBUILD=0
OPEN=1
BIND=0.0.0.0
CHECK_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --rebuild) REBUILD=1 ;;
    --port) PORT="${2:?--port needs a number}"; shift ;;
    --no-open) OPEN=0 ;;
    --local) BIND=127.0.0.1 ;;
    --check) CHECK_ONLY=1 ;;
    -h|--help) sed -n 2,12p "$0"; exit 0 ;;
    *) echo "unknown option: $1 (see ./play.sh --help)" >&2; exit 2 ;;
  esac
  shift
done

say() { printf '%s\n' "$*"; }
fail() { printf '\nCannot start: %s\n' "$*" >&2; exit 1; }

# ---- prerequisites: what is needed, how to install it on this OS
case "$(uname -s)" in
  Darwin) OS=mac ;;
  Linux) if command -v apt-get >/dev/null 2>&1; then OS=apt; else OS=linux; fi ;;
  MINGW*|MSYS*|CYGWIN*) OS=win ;;
  *) OS=other ;;
esac
hint() {   # hint <dep>: the install command for this OS
  case "$1:$OS" in
    python:mac) echo "brew install python   (or https://www.python.org/downloads/)" ;;
    python:apt) echo "sudo apt install python3" ;;
    python:win) echo "winget install Python.Python.3.12" ;;
    python:*) echo "https://www.python.org/downloads/" ;;
    node:mac) echo "brew install node   (or https://nodejs.org/)" ;;
    node:apt) echo "sudo apt install nodejs   (needs 18+; or https://nodejs.org/)" ;;
    node:win) echo "winget install OpenJS.NodeJS.LTS" ;;
    node:*) echo "https://nodejs.org/ (version 18 or newer)" ;;
    dotnet:apt) echo "sudo apt install dotnet-sdk-8.0" ;;
    dotnet:win) echo "winget install Microsoft.DotNet.SDK.8" ;;
    dotnet:*) echo "https://dotnet.microsoft.com/download/dotnet/8.0 (the .NET 8 SDK installer)" ;;
    wasm:win) echo "dotnet workload install wasm-tools   (in a terminal run as administrator)" ;;
    wasm:*) echo "sudo dotnet workload install wasm-tools" ;;
    qrencode:mac) echo "brew install qrencode" ;;
    qrencode:apt) echo "sudo apt install qrencode" ;;
    qrencode:*) echo "optional; your package manager's qrencode" ;;
  esac
}
major() { printf '%s' "$1" | sed -E 's/^v//; s/^[^0-9]*([0-9]+).*/\1/'; }
PY_VER=$(python3 --version 2>/dev/null | awk '{print $2}' || true)
NODE_VER=$(node --version 2>/dev/null || true)
DOTNET8=$(dotnet --list-sdks 2>/dev/null | awk '/^8\./{print $1}' | tail -1 || true)
WASM_OK=0; [ -n "$DOTNET8" ] && dotnet workload list 2>/dev/null | grep -q wasm-tools && WASM_OK=1
PY_OK=0;   [ -n "$PY_VER" ] && [ "$(major "$PY_VER")" -ge 3 ] && PY_OK=1
NODE_OK=0; [ -n "$NODE_VER" ] && [ "$(major "$NODE_VER")" -ge 18 ] && NODE_OK=1

if [ "$CHECK_ONLY" -eq 1 ]; then
  missing=0
  row() {   # row <ok 0|1> <name> <found> <hint-key> [optional]
    if [ "$1" -eq 1 ]; then printf '  ok       %-26s %s\n' "$2" "$3"
    elif [ -n "${5:-}" ]; then printf '  optional %-26s install: %s\n' "$2" "$(hint "$4")"
    else printf '  MISSING  %-26s install: %s\n' "$2" "$(hint "$4")"; missing=1; fi
  }
  say "Dependencies for ./play.sh ($OS):"
  row "$PY_OK" "Python 3" "$PY_VER" python
  row "$NODE_OK" "Node.js 18+" "$NODE_VER${NODE_VER:+ }$( [ -n "$NODE_VER" ] && [ "$NODE_OK" -eq 0 ] && echo '(too old)')" node
  row "$( [ -n "$DOTNET8" ] && echo 1 || echo 0)" ".NET 8 SDK" "$DOTNET8" dotnet
  row "$WASM_OK" "wasm-tools workload" "installed" wasm
  row "$(command -v qrencode >/dev/null 2>&1 && echo 1 || echo 0)" "qrencode (phone QR code)" "installed" qrencode optional
  if [ -f web/wasm/dist/_framework/dotnet.js ]; then say "  C# engine bundle: built (web/wasm/dist)"; else say "  C# engine bundle: not built yet (./play.sh builds it)"; fi
  if [ "$missing" -eq 0 ]; then say ""; say "All set. Run ./play.sh"; else say ""; say "Install the MISSING items above (in that order), then run ./play.sh --check again."; fi
  exit "$missing"
fi

[ "$PY_OK" -eq 1 ] || fail "Python 3 is not installed. Install: $(hint python)"
[ "$NODE_OK" -eq 1 ] || fail "Node.js 18+ is not installed${NODE_VER:+ (found $NODE_VER)}. It packs the game content. Install: $(hint node)"
DIST=web/wasm/dist
need_build=$REBUILD
if [ ! -f "$DIST/_framework/dotnet.js" ] || [ ! -f "$DIST/content-pack.json" ]; then
  need_build=1
elif [ -n "$(find src/HeroCat.Sim src/HeroCat.Runtime src/HeroCat.Web Content web/wasm/pack-content.js \
          -newer "$DIST/content-pack.json" -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.js' \) \
          -not -path '*/bin/*' -not -path '*/obj/*' 2>/dev/null | head -1)" ]; then
  need_build=1
fi

if [ "$need_build" -eq 1 ]; then
  [ -n "$DOTNET8" ] || fail "the .NET 8 SDK is not installed. Install: $(hint dotnet)   Then: $(hint wasm)   (./play.sh --check lists everything)"
  [ "$WASM_OK" -eq 1 ] || fail "the WebAssembly build tools are missing. Install: $(hint wasm)"
  say "== Building the C# engine for the browser (first time takes 1-3 minutes)"
  bash web/wasm/build.sh --fast || fail "the build failed; the error is above."
else
  say "== C# engine is up to date (use --rebuild to force a build)"
fi

# ---- pick a free port
free_port() {
  python3 - "$1" "$BIND" <<'PY'
import socket, sys
p, host = int(sys.argv[1]), sys.argv[2]
for port in range(p, p + 50):
    s = socket.socket()
    try:
        s.bind((host, port)); print(port); break
    except OSError:
        continue
    finally:
        s.close()
PY
}
PORT=$(free_port "$PORT")
[ -n "$PORT" ] || fail "no free port found near the one requested."

LAN_IP=$(python3 -c "import socket
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
try:
    s.connect(('10.255.255.255',1)); print(s.getsockname()[0])
except OSError:
    print('')" 2>/dev/null || true)

LOCAL_URL="http://localhost:$PORT"
say ""
say "== HeroCat is running"
say "   On this computer:  $LOCAL_URL"
if [ "$BIND" = "0.0.0.0" ] && [ -n "$LAN_IP" ] && [ "$LAN_IP" != "127.0.0.1" ]; then
  PHONE_URL="http://$LAN_IP:$PORT"
  say "   On your phone:     $PHONE_URL   (same Wi-Fi; allow Python through the firewall if it does not load)"
  if command -v qrencode >/dev/null 2>&1; then qrencode -t ANSIUTF8 "$PHONE_URL"; fi
fi
say "   Stop with Ctrl+C."
say ""

if [ "$OPEN" -eq 1 ]; then
  ( sleep 1
    if command -v open >/dev/null 2>&1; then open "$LOCAL_URL"
    elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$LOCAL_URL" >/dev/null 2>&1
    elif command -v cmd.exe >/dev/null 2>&1; then cmd.exe /c start "$LOCAL_URL" >/dev/null 2>&1
    fi ) &
fi

exec python3 -m http.server "$PORT" --bind "$BIND" --directory web
