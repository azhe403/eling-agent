#!/usr/bin/env bash
# Eling installer (Linux / macOS)
# Usage:  curl -fsSL https://raw.githubusercontent.com/azhe403/eling-agent/main/install.sh | bash
# Repair dashboard UI only:  ./install.sh --dashboard-only

set -euo pipefail

DASHBOARD_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --dashboard-only) DASHBOARD_ONLY=1 ;;
    -h|--help) echo "Usage: $0 [--dashboard-only]"; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 1 ;;
  esac
done

REPO="azhe403/eling-agent"

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$OS" in
  linux) OS="linux" ;;
  darwin) OS="osx" ;;
  *) echo "Unsupported OS: $OS" >&2; exit 1 ;;
esac

ARCH="$(uname -m)"
case "$ARCH" in
  x86_64) ARCH="x64" ;;
  arm64 | aarch64) ARCH="arm64" ;;
  *) echo "Unsupported arch: $ARCH" >&2; exit 1 ;;
esac

RID="${OS}-${ARCH}"
ASSET="eling-backend-${RID}.tar.gz"
URL="https://github.com/$REPO/releases/latest/download/$ASSET"

BIN_DIR="$HOME/.local/bin"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

if [ "$DASHBOARD_ONLY" = "1" ]; then
  UI_DIR="$BIN_DIR/eling-dashboard-ui"
  UI_ASSET="eling-dashboard-ui.tar.gz"
  UI_URL="https://github.com/$REPO/releases/latest/download/$UI_ASSET"
  if [ -f "$UI_DIR/index.html" ]; then
    echo "Dashboard UI looks healthy - re-downloading anyway."
  else
    echo "Dashboard UI missing or corrupt - re-downloading."
  fi
  pkill -x eling-backend 2>/dev/null || true
  sleep 1
  rm -rf "$UI_DIR"
  mkdir -p "$UI_DIR"
  curl -fsSL "$UI_URL" | tar xz -C "$UI_DIR"
  if [ ! -f "$UI_DIR/index.html" ]; then
    echo "Repair FAILED: index.html still missing after reinstall." >&2
    exit 1
  fi
  echo ""
  echo "Dashboard UI repaired"
  echo "  dir: $UI_DIR"
  exit 0
fi

# .local/bin may hold other tools — remove only eling's own files
mkdir -p "$BIN_DIR"
rm -f "$BIN_DIR/eling-backend" "$BIN_DIR/eling-backend.pdb" "$BIN_DIR/eling" "$BIN_DIR/eling.pdb" "$BIN_DIR/eling-dashboard" "$BIN_DIR/eling-dashboard.pdb"

echo "Downloading eling ($RID)..."
curl -fsSL "$URL" | tar xz -C "$STAGE"

# Single binary eling-backend (with eling shim for backwards-compat). UI at eling-dashboard-ui/.
mv "$STAGE/eling-backend" "$BIN_DIR/eling-backend"
chmod +x "$BIN_DIR/eling-backend"
# Backwards-compat symlink: eling → eling-backend
ln -sf "$BIN_DIR/eling-backend" "$BIN_DIR/eling" 2>/dev/null || cp -f "$BIN_DIR/eling-backend" "$BIN_DIR/eling" 2>/dev/null || true

if [ -d "$STAGE/eling-dashboard-ui" ]; then
  rm -rf "$BIN_DIR/eling-dashboard-ui"
  mv "$STAGE/eling-dashboard-ui" "$BIN_DIR/eling-dashboard-ui"
fi

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *)
    echo "NOTE: $BIN_DIR is not on your PATH. Add this to your shell profile:"
    echo "  export PATH=\"\$PATH:$BIN_DIR\""
    ;;
esac

echo ""
echo "eling installed"
echo "  binary:           $BIN_DIR/eling-backend"
echo "Run 'eling-backend' to start the MCP server."
