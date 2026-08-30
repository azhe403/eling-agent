#!/usr/bin/env bash
# Publish Eling from source and install it into ~/.local/bin ("install from source").
# Smoke test verifies: dashboard health + full MCP memory read/write round-trip.
# Usage: ./publish-global.sh [--configuration Release] [--rid <rid>] [--skip-smoke-test]

set -euo pipefail

CONFIGURATION="Release"
RID=""
SKIP_SMOKE_TEST=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --configuration|-c)
      CONFIGURATION="$2"
      shift 2
      ;;
    --rid|-r)
      RID="$2"
      shift 2
      ;;
    --skip-smoke-test)
      SKIP_SMOKE_TEST=true
      shift 1
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 1
      ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Auto-detect RID if not provided
if [ -z "$RID" ]; then
  UNAME_S="$(uname -s)"
  UNAME_M="$(uname -m)"
  
  case "$UNAME_S" in
    Linux)
      case "$UNAME_M" in
        x86_64) RID="linux-x64" ;;
        aarch64|arm64) RID="linux-arm64" ;;
        armv7l) RID="linux-arm" ;;
        *) RID="linux-x64" ;;
      esac
      ;;
    Darwin)
      case "$UNAME_M" in
        x86_64) RID="osx-x64" ;;
        arm64) RID="osx-arm64" ;;
        *) RID="osx-arm64" ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      case "$UNAME_M" in
        x86_64) RID="win-x64" ;;
        aarch64|arm64) RID="win-arm64" ;;
        *) RID="win-x64" ;;
      esac
      ;;
    *)
      RID="linux-x64"
      ;;
  esac
fi

IS_WINDOWS=false
EXE_EXT=""
if [[ "$RID" == win-* ]]; then
  IS_WINDOWS=true
  EXE_EXT=".exe"
fi

TEMP_DIR="${TMPDIR:-${TEMP:-/tmp}}"
OUT_DIR="$TEMP_DIR/eling-publish-global"
ARTIFACTS_DIR="$TEMP_DIR/eling-publish-artifacts"
BIN_DIR="$HOME/.local/bin"

echo "== Publishing ($CONFIGURATION / $RID) =="
rm -rf "$OUT_DIR" "$ARTIFACTS_DIR"
mkdir -p "$OUT_DIR" "$ARTIFACTS_DIR"

for project in Eling.Host Eling.Dashboard; do
  echo "-- dotnet publish src/backend/$project"
  dotnet publish "$REPO_ROOT/src/backend/$project" \
    -c "$CONFIGURATION" -r "$RID" \
    --self-contained true \
    --artifacts-path "$ARTIFACTS_DIR" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT_DIR" --nologo -v q
done

for expected in "eling$EXE_EXT" "eling-dashboard$EXE_EXT" "eling-dashboard-ui"; do
  if [ ! -e "$OUT_DIR/$expected" ]; then
    echo "Publish output missing: $expected" >&2
    exit 1
  fi
done

echo "== Installing into $BIN_DIR =="
mkdir -p "$BIN_DIR"

# Stop existing running processes if any
if [ "$IS_WINDOWS" = true ]; then
  taskkill //F //IM eling.exe >/dev/null 2>&1 || true
  taskkill //F //IM eling-dashboard.exe >/dev/null 2>&1 || true
else
  pkill -9 -x eling >/dev/null 2>&1 || true
  pkill -9 -x eling-dashboard >/dev/null 2>&1 || true
fi
sleep 1

cp -f "$OUT_DIR/eling$EXE_EXT" "$BIN_DIR/"
chmod +x "$BIN_DIR/eling$EXE_EXT" 2>/dev/null || true

cp -f "$OUT_DIR/eling-dashboard$EXE_EXT" "$BIN_DIR/"
chmod +x "$BIN_DIR/eling-dashboard$EXE_EXT" 2>/dev/null || true

rm -rf "$BIN_DIR/eling-dashboard-ui"
cp -rf "$OUT_DIR/eling-dashboard-ui" "$BIN_DIR/eling-dashboard-ui"

if [ "$SKIP_SMOKE_TEST" = true ]; then
  echo "== Installed (smoke test skipped) =="
  exit 0
fi

echo "== Smoke test: dashboard health =="
RANDOM_SUFFIX=$(head /dev/urandom 2>/dev/null | tr -dc A-Za-z0-9 2>/dev/null | head -c 8 || echo "$$")
PROJECT_DIR="$TEMP_DIR/eling-smoke-$RANDOM_SUFFIX"
mkdir -p "$PROJECT_DIR/.eling"

ELING_BIN="$BIN_DIR/eling$EXE_EXT"

# Create named pipes / temp files for bidirectional STDIO MCP communication
FIFO_IN="$TEMP_DIR/eling_fifo_in_$RANDOM_SUFFIX"
FIFO_OUT="$TEMP_DIR/eling_fifo_out_$RANDOM_SUFFIX"
rm -f "$FIFO_IN" "$FIFO_OUT"
mkfifo "$FIFO_IN" "$FIFO_OUT" 2>/dev/null || true

stop_smoke_process() {
  if [ -n "${SMOKE_PID:-}" ]; then
    kill -9 "$SMOKE_PID" >/dev/null 2>&1 || true
  fi
  if [ "$IS_WINDOWS" = true ]; then
    taskkill //F //IM eling-dashboard.exe >/dev/null 2>&1 || true
  else
    pkill -9 -x eling-dashboard >/dev/null 2>&1 || true
  fi
  rm -rf "$PROJECT_DIR" "$FIFO_IN" "$FIFO_OUT" >/dev/null 2>&1 || true
}

trap stop_smoke_process EXIT INT TERM

# Start eling with background I/O
(
  cd "$PROJECT_DIR"
  if [ -p "$FIFO_IN" ] && [ -p "$FIFO_OUT" ]; then
    "$ELING_BIN" <"$FIFO_IN" >"$FIFO_OUT" 2>/dev/null &
  else
    "$ELING_BIN" 2>/dev/null &
  fi
) &
SMOKE_PID=$!

HEALTH=""
for _ in {1..15}; do
  sleep 1
  if command -v curl >/dev/null 2>&1; then
    HEALTH=$(curl -s -m 2 http://127.0.0.1:4317/health || true)
  elif command -v wget >/dev/null 2>&1; then
    HEALTH=$(wget -qO- -T 2 http://127.0.0.1:4317/health || true)
  fi
  if [ -n "$HEALTH" ]; then
    break
  fi
done

if [ -z "$HEALTH" ]; then
  echo "Smoke test FAILED: dashboard did not answer /health." >&2
  exit 1
fi
echo "  health: $HEALTH"

echo "== Smoke test: MCP memory read/write =="
if [ -p "$FIFO_IN" ] && [ -p "$FIFO_OUT" ]; then
  exec 3>"$FIFO_IN"
  exec 4<"$FIFO_OUT"

  # Initialize
  echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"publish-smoke","version":"1.0"}}}' >&3
  read -r -t 10 INIT_LINE <&4 || INIT_LINE=""
  if [[ "$INIT_LINE" != *'"id":1'* ]]; then
    echo "Smoke test FAILED: MCP initialize got no response. Raw: $INIT_LINE" >&2
    exit 1
  fi

  echo '{"jsonrpc":"2.0","method":"notifications/initialized"}' >&3

  STAMP="$RANDOM_SUFFIX"
  CONTENT="publish-global smoke test $STAMP"

  # Save Memory
  echo "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_save\",\"arguments\":{\"content\":\"$CONTENT\",\"type\":\"note\",\"tags\":[\"smoke\"]}}}" >&3
  read -r -t 10 SAVE_LINE <&4 || SAVE_LINE=""

  SAVED_ID=$(echo "$SAVE_LINE" | grep -o -E '01[0-9a-hjkmnp-tv-z]{24}' | head -n 1 || true)
  if [ -z "$SAVED_ID" ]; then
    echo "Smoke test FAILED: memory_save returned no usable response. Raw: $SAVE_LINE" >&2
    exit 1
  fi

  # Get Memory
  echo "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_get\",\"arguments\":{\"id\":\"$SAVED_ID\",\"scope\":\"project\"}}}" >&3
  read -r -t 10 GET_LINE <&4 || GET_LINE=""

  if [[ "$GET_LINE" != *"$SAVED_ID"* ]]; then
    echo "Smoke test FAILED: memory_get did not return the saved memory (id=$SAVED_ID). Raw: $GET_LINE" >&2
    exit 1
  fi

  exec 3>&-
  exec 4<&-
  echo "  memory read/write: OK (saved & searched back id=$SAVED_ID)"
else
  echo "  memory read/write: Skipped stdio fifo test on this platform"
fi

echo ""
echo "Installed & verified:"
echo "  health:           $HEALTH"
echo "  binary:            $BIN_DIR/eling$EXE_EXT"
echo "  dashboard binary:  $BIN_DIR/eling-dashboard$EXE_EXT"
