#!/usr/bin/env bash
# Uso: build/publish-linux.sh VERSION REPO
# Gera artifacts/ConnectDesktop-linux-x64.tar.gz com a pasta connect-desktop/
# (binario single-file + connect-desktop.desktop).
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "Uso: $0 VERSION REPO" >&2
  exit 1
fi

VERSION="$1"
REPO="$2"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_PROJECT="$ROOT/src/ConnectDesktop.App"
ARTIFACTS="$ROOT/artifacts"
PUBLISH_DIR="$ARTIFACTS/publish-linux-x64"
PACKAGE_DIR="$ARTIFACTS/linux-x64/connect-desktop"
TARBALL="$ARTIFACTS/ConnectDesktop-linux-x64.tar.gz"

echo "Publicando Connect Desktop $VERSION ($REPO) para linux-x64..."
dotnet publish "$APP_PROJECT" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:Version="$VERSION" -p:GitHubRepo="$REPO" -o "$PUBLISH_DIR"

echo "Montando pacote..."
rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR"
cp -R "$PUBLISH_DIR"/. "$PACKAGE_DIR/"
chmod +x "$PACKAGE_DIR/ConnectDesktop"

cat > "$PACKAGE_DIR/connect-desktop.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Connect Desktop
Comment=Compartilhe mouse e teclado entre computadores
Exec=ConnectDesktop
Terminal=false
Categories=Utility;
EOF

echo "Gerando $TARBALL..."
rm -f "$TARBALL"
tar -C "$ARTIFACTS/linux-x64" -czf "$TARBALL" connect-desktop

echo "Pronto: $TARBALL"
