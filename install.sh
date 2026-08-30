#!/usr/bin/env bash
# Eling installer (Linux / macOS)
# Usage:  curl -fsSL https://raw.githubusercontent.com/azhe403/eling-agent/main/install.sh | bash

set -euo pipefail

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
ASSET="eling-${RID}.tar.gz"
URL="https://github.com/$REPO/releases/latest/download/$ASSET"

BIN_DIR="$HOME/.local/bin"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# .local/bin may hold other tools — remove only eling's own files
mkdir -p "$BIN_DIR"
rm -f "$BIN_DIR/eling" "$BIN_DIR/eling.pdb" "$BIN_DIR/eling-dashboard" "$BIN_DIR/eling-dashboard.pdb"

echo "Downloading eling ($RID)..."
curl -fsSL "$URL" | tar xz -C "$STAGE"

# Paired layout: eling spawns eling-dashboard from its own directory, and the
# dashboard serves its web UI from ./eling-dashboard-ui next to the executables.
mv "$STAGE/eling" "$BIN_DIR/eling"
chmod +x "$BIN_DIR/eling"

if [ -f "$STAGE/eling-dashboard" ]; then
  mv "$STAGE/eling-dashboard" "$BIN_DIR/eling-dashboard"
  chmod +x "$BIN_DIR/eling-dashboard"
fi

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
echo "  binary:           $BIN_DIR/eling"
echo "  dashboard binary: $BIN_DIR/eling-dashboard"
echo "Run 'eling' to start the MCP server."
