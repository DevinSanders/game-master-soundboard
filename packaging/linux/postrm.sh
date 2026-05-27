#!/bin/sh
# Post-removal hook for .deb / .rpm. Refreshes the desktop database and icon
# cache so stale entries don't linger. We don't try to unregister the URI
# scheme — the .desktop file is gone, xdg-mime resolves to nothing on its own.
set -e

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
fi

exit 0
