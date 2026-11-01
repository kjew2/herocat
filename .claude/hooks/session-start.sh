#!/bin/bash
# Cloud sessions start without the .NET SDK, which ./check.sh needs. Install it (and the wasm-tools
# workload the full check runs the web steps with) so agents can run ./check.sh without --no-dotnet.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^8\.'; then
  # dot.net's install script is blocked by the proxy; the distro package is not.
  SUDO=""; [ "$(id -u)" -eq 0 ] || SUDO="sudo"
  $SUDO apt-get update -qq
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0
fi

# Full ./check.sh builds the WebAssembly bundle only when this workload is present. Not fatal: check.sh
# then skips the web steps locally (CI still requires them).
if ! dotnet workload list 2>/dev/null | grep -q wasm-tools; then
  dotnet workload install wasm-tools >&2 || echo "warning: wasm-tools workload install failed; ./check.sh will skip the web steps" >&2
fi

# Warm the NuGet cache so the first ./check.sh does not pay for restore.
dotnet restore HeroCat.sln >&2 || echo "warning: dotnet restore failed" >&2

# CI sets these too; keeps dotnet quiet in every later command of the session.
{
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
  echo 'export DOTNET_NOLOGO=1'
  echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
} >> "${CLAUDE_ENV_FILE:-/dev/null}"
