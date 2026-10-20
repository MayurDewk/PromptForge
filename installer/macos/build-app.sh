#!/usr/bin/env bash
# Builds PromptForge.app and PromptForge-macOS-<arch>.zip on a Mac.
#
#   installer/macos/build-app.sh [arm64|x64]        (default: this Mac's processor)
#
# Signing:
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)"  signs with the hardened runtime.
#   Unset: ad-hoc signature (runs on this Mac; other Macs need right-click → Open the first time).
# Notarization (needs SIGN_IDENTITY):
#   NOTARY_PROFILE=<profile saved with `xcrun notarytool store-credentials`>
set -euo pipefail

case "${1:-$(uname -m)}" in
  arm64|aarch64) ARCH=arm64 ;;
  x64|x86_64) ARCH=x64 ;;
  *) echo "Unknown architecture: $1 (use arm64 or x64)" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/PromptForge.Mac/PromptForge.Mac.csproj"
OUT="$ROOT/dist/macos-$ARCH"
APP="$OUT/PromptForge.app"
ZIP="$OUT/PromptForge-macOS-$ARCH.zip"
VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version)"

echo "Building PromptForge $VERSION for macOS $ARCH"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$PROJECT" -c Release -r "osx-$ARCH" --self-contained true -o "$OUT/publish"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$ROOT/installer/macos/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"

# App icon from the 256 px PNG.
ICONSET="$OUT/PromptForge.iconset"
mkdir -p "$ICONSET"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$ROOT/docs/images/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
done
for size in 16 32 128 256; do
  cp "$ICONSET/icon_$((size * 2))x$((size * 2)).png" "$ICONSET/icon_${size}x${size}@2x.png"
done
rm "$ICONSET/icon_64x64.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/PromptForge.icns"
rm -rf "$ICONSET"

# Sign every file in Contents/MacOS first (codesign treats that folder as code, and the .NET publish
# output keeps its managed assemblies next to the native ones), then the bundle itself.
if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  SIGN=(codesign --force --timestamp --options runtime --entitlements "$ROOT/installer/macos/PromptForge.entitlements" --sign "$SIGN_IDENTITY")
else
  echo "SIGN_IDENTITY not set: signing ad-hoc."
  SIGN=(codesign --force --sign -)
fi
find "$APP/Contents/MacOS" -type f -print0 | while IFS= read -r -d '' file; do
  "${SIGN[@]}" "$file"
done
"${SIGN[@]}" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

ditto -c -k --keepParent "$APP" "$ZIP"

if [[ -n "${NOTARY_PROFILE:-}" && -n "${SIGN_IDENTITY:-}" ]]; then
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm "$ZIP"
  ditto -c -k --keepParent "$APP" "$ZIP"
fi

rm -rf "$OUT/publish"
shasum -a 256 "$ZIP"
echo "Done: $APP"
