#!/usr/bin/env bash
# Build a macOS .app bundle from a `dotnet publish` output directory.
#
# Usage: build-app.sh <version> <publish-dir> <output-dir>
#   <version>      e.g. 1.2.3 or 1.2.3-beta.1
#   <publish-dir>  e.g. ./publish/osx-arm64  (contains SoundBoard.Desktop + deps)
#   <output-dir>   directory where "Game Master Sound Board.app" is created
#
# Produces: <output-dir>/Game Master Sound Board.app

set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <version> <publish-dir> <output-dir>" >&2
    exit 1
fi

VERSION="$1"
PUBLISH_DIR="$2"
OUTPUT_DIR="$3"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="Game Master Sound Board.app"
APP_PATH="$OUTPUT_DIR/$APP_NAME"

echo "Building $APP_PATH (version $VERSION)"
echo "  publish source: $PUBLISH_DIR"

# A .app bundle is just a directory with a specific structure:
#   Game Master Sound Board.app/
#     Contents/
#       Info.plist
#       MacOS/        <- executable + native libs
#       Resources/    <- icons, AvaloniaResource bundle, etc.
mkdir -p "$APP_PATH/Contents/MacOS"
mkdir -p "$APP_PATH/Contents/Resources"

# Copy the publish output into Contents/MacOS.
cp -R "$PUBLISH_DIR"/* "$APP_PATH/Contents/MacOS/"

# Make the main executable executable. dotnet publish usually sets this,
# but copying through some workflows strips the bit.
chmod +x "$APP_PATH/Contents/MacOS/SoundBoard.Desktop"

# Fill in Info.plist placeholders. The build identifier (CFBundleVersion)
# must be all-numeric "x.y.z" — strip any prerelease suffix.
BUILD="${VERSION%%-*}"
sed -e "s/@VERSION@/$VERSION/g" \
    -e "s/@BUILD@/$BUILD/g" \
    "$SCRIPT_DIR/Info.plist.template" > "$APP_PATH/Contents/Info.plist"

# Copy icon if available. If you have an .icns at packaging/macos/app-icon.icns
# it gets used; otherwise the bundle ships without an icon (macOS shows the
# generic gear). To generate one from a 1024x1024 PNG:
#   mkdir icon.iconset
#   sips -z 16 16     icon.png --out icon.iconset/icon_16x16.png
#   sips -z 32 32     icon.png --out icon.iconset/icon_16x16@2x.png
#   sips -z 32 32     icon.png --out icon.iconset/icon_32x32.png
#   sips -z 64 64     icon.png --out icon.iconset/icon_32x32@2x.png
#   sips -z 128 128   icon.png --out icon.iconset/icon_128x128.png
#   sips -z 256 256   icon.png --out icon.iconset/icon_128x128@2x.png
#   sips -z 256 256   icon.png --out icon.iconset/icon_256x256.png
#   sips -z 512 512   icon.png --out icon.iconset/icon_256x256@2x.png
#   sips -z 512 512   icon.png --out icon.iconset/icon_512x512.png
#   cp icon.png       icon.iconset/icon_512x512@2x.png
#   iconutil -c icns icon.iconset -o app-icon.icns
if [[ -f "$SCRIPT_DIR/app-icon.icns" ]]; then
    cp "$SCRIPT_DIR/app-icon.icns" "$APP_PATH/Contents/Resources/app-icon.icns"
else
    echo "  (no app-icon.icns found — bundle will use the generic icon)"
fi

# Strip the macOS "quarantine" extended attribute that gets set when files come
# from the internet — Gatekeeper relies on it to nag about unidentified
# developers. Stripping it on the build machine is fine; users downloading the
# .dmg will still get a fresh one applied by Safari/Firefox.
xattr -cr "$APP_PATH" 2>/dev/null || true

echo "Done: $APP_PATH"
