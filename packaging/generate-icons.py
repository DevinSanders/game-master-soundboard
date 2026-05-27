#!/usr/bin/env python3
"""
Generate platform icon assets from SoundBoard.UI/Assets/app-icon.ico.

Outputs:
  packaging/linux/gmsoundboard.png  — 256×256 PNG for AppImage / .deb / .rpm
  packaging/macos/app-icon.icns     — multi-resolution Apple Icon

Run from the repo root:
    py packaging/generate-icons.py

Re-run any time the source icon changes; the outputs are committed to the repo
because CI image runners can't easily produce them and they shouldn't change
release-to-release.
"""

from __future__ import annotations

import sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "SoundBoard.UI" / "Assets" / "app-icon.ico"
LINUX_PNG = ROOT / "packaging" / "linux" / "gmsoundboard.png"
MACOS_ICNS = ROOT / "packaging" / "macos" / "app-icon.icns"


def square_pad(im: Image.Image) -> Image.Image:
    """Center the image on a transparent square canvas of side = max(W, H)."""
    side = max(im.width, im.height)
    if im.width == im.height:
        return im
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(im, ((side - im.width) // 2, (side - im.height) // 2), im if im.mode == "RGBA" else None)
    return canvas


def main() -> int:
    if not SOURCE.exists():
        print(f"ERROR: source icon not found at {SOURCE}", file=sys.stderr)
        return 1

    # Pillow's ICO reader returns the largest embedded image when you call .convert()
    # or iterate over .size. We grab it explicitly to make sure we're not stuck on
    # a tiny embedded variant.
    with Image.open(SOURCE) as src:
        # Force the highest-resolution sub-image. .ico stores multiple sizes; .size
        # at open() is the first/default, not necessarily the largest.
        if hasattr(src, "sizes"):
            largest = max(src.sizes())
            src.size = largest
        im = src.convert("RGBA").copy()

    print(f"Source: {SOURCE.name} ({im.width}×{im.height})")

    # Pad to square — the source is 235×256, not square, and both target formats
    # work best with square inputs.
    im_square = square_pad(im)
    print(f"Padded to square: {im_square.width}×{im_square.height}")

    # ── Linux PNG ─────────────────────────────────────────────────────────
    LINUX_PNG.parent.mkdir(parents=True, exist_ok=True)
    linux_im = im_square.resize((256, 256), Image.LANCZOS)
    linux_im.save(LINUX_PNG, format="PNG", optimize=True)
    print(f"Wrote {LINUX_PNG.relative_to(ROOT)} ({LINUX_PNG.stat().st_size:,} bytes)")

    # ── macOS ICNS ────────────────────────────────────────────────────────
    # Pillow's ICNS writer embeds multiple sizes from a single input. It picks
    # which ones to include based on what Apple's format defines (16, 32, 64,
    # 128, 256, 512, 1024). We upscale the input to 1024 so all sizes have a
    # high-quality source to resample from.
    MACOS_ICNS.parent.mkdir(parents=True, exist_ok=True)
    macos_source = im_square.resize((1024, 1024), Image.LANCZOS)
    macos_source.save(
        MACOS_ICNS,
        format="ICNS",
        sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)],
    )
    print(f"Wrote {MACOS_ICNS.relative_to(ROOT)} ({MACOS_ICNS.stat().st_size:,} bytes)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
