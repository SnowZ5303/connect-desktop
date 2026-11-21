#!/usr/bin/env bash
# Uso: build/publish-mac.sh VERSION REPO RID
#   RID: osx-arm64 ou osx-x64
# Gera artifacts/ConnectDesktop-mac-<arch>.zip com "Connect Desktop.app".
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "Uso: $0 VERSION REPO RID (osx-arm64 ou osx-x64)" >&2
  exit 1
fi

VERSION="$1"
REPO="$2"
RID="$3"

case "$RID" in
  osx-arm64) ARCH="arm64" ;;
  osx-x64) ARCH="x64" ;;
  *) echo "RID invalido: $RID (use osx-arm64 ou osx-x64)" >&2; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_PROJECT="$ROOT/src/ConnectDesktop.App"
ARTIFACTS="$ROOT/artifacts"
PUBLISH_DIR="$ARTIFACTS/publish-$RID"
BUNDLE="$ARTIFACTS/bundle-$RID/Connect Desktop.app"
ZIP="$ARTIFACTS/ConnectDesktop-mac-$ARCH.zip"

echo "Publicando Connect Desktop $VERSION ($REPO) para $RID..."
dotnet publish "$APP_PROJECT" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:GitHubRepo="$REPO" -o "$PUBLISH_DIR"

echo "Montando bundle..."
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$BUNDLE/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$ROOT/build/mac/Info.plist" > "$BUNDLE/Contents/Info.plist"
chmod +x "$BUNDLE/Contents/MacOS/ConnectDesktop"

echo "Gerando $ZIP..."
rm -f "$ZIP"
ditto -c -k --keepParent "$BUNDLE" "$ZIP"

echo "Pronto: $ZIP"
