#!/bin/sh
# Post-install hook for .deb (Debian/Ubuntu) and .rpm (Fedora/openSUSE/RHEL).
# Registers the gmsound:// URL scheme and refreshes the desktop database
# so the launcher picks up the new menu entry.
set -e

# Refresh the desktop database so file managers see the new .desktop entry.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi

# Register the gmsound:// URI scheme with the system MIME handlers.
if command -v xdg-mime >/dev/null 2>&1; then
    xdg-mime default gmsoundboard.desktop x-scheme-handler/gmsound || true
fi

# Refresh icon cache so the launcher icon shows up.
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
fi

exit 0
