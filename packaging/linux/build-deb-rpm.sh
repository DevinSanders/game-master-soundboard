#!/usr/bin/env bash
# Build .deb and .rpm packages from a `dotnet publish` output directory using fpm.
#
# Usage: build-deb-rpm.sh <version> <publish-dir> <output-dir> [arch]
#
#   arch — Debian-style CPU arch: "amd64" (default) or "arm64". The RPM build
#          maps this to the rpm convention (x86_64 / aarch64).
#
# Requires:
#   - fpm (Effing Package Management) — `sudo gem install --no-document fpm`
#   - rpmbuild — `sudo apt-get install rpm` on Debian/Ubuntu, or rpm on Fedora.
#
# fpm builds both deb and rpm from the same staged tree, which keeps post-install
# behavior identical between distros. fpm only stages files (it doesn't compile),
# so cross-arch packaging from an x86_64 runner works fine — the arch label is
# metadata that tells apt/dnf which machines the package targets.

set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
    echo "Usage: $0 <version> <publish-dir> <output-dir> [arch]" >&2
    exit 1
fi

VERSION="$1"
PUBLISH_DIR="$2"
OUTPUT_DIR="$3"
DEB_ARCH="${4:-amd64}"

# Map the Debian arch label to the rpm one.
case "$DEB_ARCH" in
    amd64) RPM_ARCH="x86_64" ;;
    arm64) RPM_ARCH="aarch64" ;;
    *) echo "Unsupported arch '$DEB_ARCH' (expected amd64 or arm64)" >&2; exit 1 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Stage the file tree as it should appear on the user's system.
#   /opt/gmsoundboard/                    binaries + native deps
#   /usr/bin/gmsoundboard                 launcher symlink
#   /usr/share/applications/gmsoundboard.desktop
#   /usr/share/icons/hicolor/256x256/apps/gmsoundboard.png

mkdir -p "$STAGE/opt/gmsoundboard"
mkdir -p "$STAGE/usr/bin"
mkdir -p "$STAGE/usr/share/applications"
mkdir -p "$STAGE/usr/share/icons/hicolor/256x256/apps"

cp -R "$PUBLISH_DIR"/* "$STAGE/opt/gmsoundboard/"
chmod +x "$STAGE/opt/gmsoundboard/SoundBoard.Desktop"

# /usr/bin/gmsoundboard wrapper so the .desktop file's Exec=gmsoundboard works.
# Symlink would also work, but a wrapper handles working-directory issues on
# distros where systemd starts the app from /.
cat > "$STAGE/usr/bin/gmsoundboard" <<'EOF'
#!/bin/sh
exec /opt/gmsoundboard/SoundBoard.Desktop "$@"
EOF
chmod +x "$STAGE/usr/bin/gmsoundboard"

cp "$SCRIPT_DIR/gmsoundboard.desktop" "$STAGE/usr/share/applications/gmsoundboard.desktop"

if [[ -f "$SCRIPT_DIR/gmsoundboard.png" ]]; then
    cp "$SCRIPT_DIR/gmsoundboard.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/gmsoundboard.png"
fi

# Strip the prerelease suffix off for the package version field — Debian and RPM
# both have rules about valid version strings, and "1.2.3-beta.1" causes them
# trouble. We store the full semver as the "iteration" so it's still visible.
PKG_VERSION="${VERSION%%-*}"
ITERATION="${VERSION#*-}"
if [[ "$ITERATION" == "$VERSION" ]]; then
    ITERATION="1"
fi

# Runtime dependencies. The published app is self-contained — the .NET
# runtime and the OpenAL Soft native lib are bundled — but two classes of
# system library are dlopen'd at runtime and are NOT in the package:
#   1. Avalonia's X11 backend: libX11, libICE, libSM, fontconfig, and a GL
#      stack (GL falls back to software, but the loader still probes it).
#   2. OpenAL Soft's audio backends: it dlopens libasound (ALSA) and
#      libpulse (PulseAudio) at runtime to find a working output. ALSA is
#      the near-universal floor, so we depend on it; PulseAudio/PipeWire are
#      used when present but aren't hard requirements.
# Declaring these as package deps means apt/dnf pull them in on a minimal
# install instead of the app silently failing to start. (.NET's own libc/
# libstdc++/zlib/libicu needs are present on any graphical desktop, so we
# don't pin those — pinning libicuNN in particular breaks across distro
# releases since the soname is version-stamped.)
DEB_DEPENDS=(libx11-6 libice6 libsm6 libfontconfig1 libgl1 libasound2)
RPM_DEPENDS=(libX11 libICE libSM fontconfig mesa-libGL alsa-lib)

build_depends_args() {
    # Echo "--depends a --depends b …" for the given dep list.
    local dep
    for dep in "$@"; do printf -- '--depends\n%s\n' "$dep"; done
}

# Common fpm args. Both .deb and .rpm use the same input tree; arch + deps
# differ per format.
COMMON_ARGS=(
    --input-type dir
    --name gmsoundboard
    --version "$PKG_VERSION"
    --iteration "$ITERATION"
    --description "Cross-platform soundboard for tabletop RPG sessions"
    --url "https://github.com/DevinSanders/game-master-soundboard"
    --license "GPL-3.0"
    --maintainer "Game Master Sound Board Project"
    --after-install "$SCRIPT_DIR/postinst.sh"
    --after-remove "$SCRIPT_DIR/postrm.sh"
    --category "AudioVideo"
    --chdir "$STAGE"
    --package "$OUTPUT_DIR"
)

echo "Building .deb ($DEB_ARCH)"
mapfile -t DEB_DEP_ARGS < <(build_depends_args "${DEB_DEPENDS[@]}")
fpm --output-type deb --architecture "$DEB_ARCH" \
    "${DEB_DEP_ARGS[@]}" "${COMMON_ARGS[@]}" .

echo "Building .rpm ($RPM_ARCH)"
mapfile -t RPM_DEP_ARGS < <(build_depends_args "${RPM_DEPENDS[@]}")
fpm --output-type rpm --architecture "$RPM_ARCH" \
    "${RPM_DEP_ARGS[@]}" "${COMMON_ARGS[@]}" .

echo "Done. Artifacts in $OUTPUT_DIR:"
ls -la "$OUTPUT_DIR"/*.deb "$OUTPUT_DIR"/*.rpm 2>/dev/null || true
