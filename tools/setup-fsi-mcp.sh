#!/usr/bin/env bash
# Vendors, builds and starts the FSI MCP server so `dotnet fsi` is available as an
# MCP tool with no manual setup. Called from Directory.Build.targets on every build.
#
# It exists as a script rather than inline MSBuild because it has to be idempotent in
# three different ways: clone once, build once, and start only when nothing is already
# listening. Every step is safe to re-run.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$ROOT/.tools/fsi-mcp-server"
PROJECT="$DIR/server/fsi-mcp-server.fsproj"
PORT=5020
REPO="https://github.com/jovaneyck/fsi-mcp-server.git"
LOG="$ROOT/.tools/fsi-mcp-server.log"

listening() { curl -fsS -m 2 "http://localhost:$PORT/health" >/dev/null 2>&1; }

# Already serving: nothing to do. This is the common case on every rebuild.
if listening; then exit 0; fi

if [ ! -d "$DIR/.git" ]; then
  echo "  fsi-mcp: cloning $REPO"
  rm -rf "$DIR"
  git clone --depth 1 --quiet "$REPO" "$DIR" || { echo "  fsi-mcp: clone failed (offline?), skipping"; exit 0; }
fi

# Upstream targets net9.0; this repo's toolchain is net10.0, and a machine with only
# the .NET 10 SDK has no .NET 9 runtime to run it on. Retargeting keeps the vendored
# copy runnable from a single SDK. Idempotent.
if grep -q "<TargetFramework>net9.0</TargetFramework>" "$PROJECT" 2>/dev/null; then
  sed -i.bak 's|<TargetFramework>net9.0</TargetFramework>|<TargetFramework>net10.0</TargetFramework>|' "$PROJECT"
  rm -f "$PROJECT.bak"
fi

if ! dotnet build "$PROJECT" -v q --nologo >>"$LOG" 2>&1; then
  echo "  fsi-mcp: build failed; see $LOG"
  exit 0
fi

echo "  fsi-mcp: starting on http://localhost:$PORT"
( nohup dotnet run --project "$PROJECT" --no-build >>"$LOG" 2>&1 & ) >/dev/null 2>&1

for _ in $(seq 1 30); do
  if listening; then exit 0; fi
  sleep 1
done

echo "  fsi-mcp: did not become healthy in 30s; see $LOG"
exit 0
