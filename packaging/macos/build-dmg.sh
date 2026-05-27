#!/usr/bin/env bash
# Wrap a built .app bundle in a .dmg disk image with a drag-to-Applications shortcut.
#
# Usage: build-dmg.sh <version> <arch> <app-dir-containing-dot-app>
#   <version>  e.g. 1.2.3
#   <arch>     osx-arm64 or osx-x64 (used in the output filename)
#   <app-dir>  directory containing "Game Master Sound Board.app"
#
# Produces: <app-dir>/GameMasterSoundBoard-<version>-<arch>.dmg

set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <version> <arch> <app-dir>" >&2
    exit 1
fi

VERSION="$1"
ARCH="$2"
APP_DIR="$3"
APP_NAME="Game Master Sound Board.app"
DMG_NAME="GameMasterSoundBoard-$VERSION-$ARCH.dmg"

if [[ ! -d "$APP_DIR/$APP_NAME" ]]; then
    echo "Error: $APP_DIR/$APP_NAME not found" >&2
    exit 1
fi

# Stage a clean directory containing only the .app and a symlink to /Applications.
# When the user mounts the .dmg they see "Drag the app over here to install."
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

cp -R "$APP_DIR/$APP_NAME" "$STAGING/"
ln -s /Applications "$STAGING/Applications"

# Build the compressed disk image.
# - UDZO: bzip2-style compression, broadly compatible.
# - volname: name shown in Finder when the dmg is mounted.
hdiutil create \
    -volname "Game Master Sound Board" \
    -srcfolder "$STAGING" \
    -ov \
    -format UDZO \
    "$APP_DIR/$DMG_NAME"

echo "Done: $APP_DIR/$DMG_NAME"
