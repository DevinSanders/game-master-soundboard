#!/usr/bin/env bash
# Build a Linux AppImage from a `dotnet publish` output directory.
#
# Usage: build-appimage.sh <version> <publish-dir> <output-dir>
#
# Requires: appimagetool in PATH (workflow installs it from the GitHub release).

set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <version> <publish-dir> <output-dir>" >&2
    exit 1
fi

VERSION="$1"
PUBLISH_DIR="$2"
OUTPUT_DIR="$3"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPDIR="$(mktemp -d)/GameMasterSoundBoard.AppDir"
trap 'rm -rf "$(dirname "$APPDIR")"' EXIT

echo "Staging AppDir at $APPDIR"

# Standard AppDir layout:
#   AppDir/
#     AppRun                       <- entry point script (must be executable)
#     gmsoundboard.desktop         <- top-level .desktop file
#     gmsoundboard.png             <- top-level icon
#     usr/
#       bin/                       <- the binary + native deps
#       share/applications/        <- another copy of the .desktop file
#       share/icons/hicolor/...    <- icons by size
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# Copy publish output. The main binary is "SoundBoard.Desktop"; expose it as
# "gmsoundboard" so the .desktop file's Exec=gmsoundboard resolves.
cp -R "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/SoundBoard.Desktop"
ln -sf SoundBoard.Desktop "$APPDIR/usr/bin/gmsoundboard"

# Install the .desktop file (top-level AND in share/applications).
cp "$SCRIPT_DIR/gmsoundboard.desktop" "$APPDIR/gmsoundboard.desktop"
cp "$SCRIPT_DIR/gmsoundboard.desktop" "$APPDIR/usr/share/applications/gmsoundboard.desktop"

# Icon. AppImage strictly requires a top-level PNG matching the .desktop Icon= field.
# If we have a 256×256 PNG at packaging/linux/gmsoundboard.png it's used; otherwise
# a 1×1 placeholder is generated so appimagetool doesn't refuse to build.
if [[ -f "$SCRIPT_DIR/gmsoundboard.png" ]]; then
    cp "$SCRIPT_DIR/gmsoundboard.png" "$APPDIR/gmsoundboard.png"
    cp "$SCRIPT_DIR/gmsoundboard.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/gmsoundboard.png"
else
    echo "  (no gmsoundboard.png — writing a 1×1 placeholder; replace with a real icon)"
    # 1x1 transparent PNG, base64-encoded.
    PLACEHOLDER='iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVQYV2NgAAIAAAUAAarVyFEAAAAASUVORK5CYII='
    echo "$PLACEHOLDER" | base64 -d > "$APPDIR/gmsoundboard.png"
    cp "$APPDIR/gmsoundboard.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/gmsoundboard.png"
fi

# AppRun is the AppImage's entry point. Mark executable.
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}/usr/bin:${LD_LIBRARY_PATH-}"
exec "${HERE}/usr/bin/gmsoundboard" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# Build the AppImage.
# --appimage-extract-and-run makes appimagetool extract itself instead of
# mounting via FUSE. Ubuntu 24.04+ runners (the GitHub Actions default since
# 2025) no longer ship libfuse2, so without this flag the tool fails at
# launch with "dlopen(): error loading libfuse.so.2". Extract-mode is slightly
# slower per invocation but has no runtime dependency on FUSE.
OUTPUT_FILE="$OUTPUT_DIR/GameMasterSoundBoard-$VERSION-x86_64.AppImage"
ARCH=x86_64 appimagetool --appimage-extract-and-run --no-appstream "$APPDIR" "$OUTPUT_FILE"

echo "Done: $OUTPUT_FILE"
