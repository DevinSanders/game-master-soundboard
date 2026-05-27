# Third-party licenses bundled in releases

This folder contains the verbatim license texts for every library whose
license requires us to ship the text alongside our binaries. The release
workflow copies this folder into the publish output of every platform
artifact (Windows installer, macOS .app/.dmg, Linux .deb/.rpm/.AppImage,
universal zip/tar.gz) as `THIRD-PARTY-LICENSES/`.

## What's required and why

| Library | License | Why bundled |
|---|---|---|
| OpenAL Soft | LGPL-2.0-or-later | LGPL §6/§4 requires that we either accompany the binary distribution with the library's complete source code OR provide a written offer to supply it for at least three years. We do both — the upstream COPYING is downloaded and bundled at release time, and `SOURCE-OFFER.txt` is the written offer. |

The `Silk.NET.OpenAL` managed bindings (MIT) are statically attributed in
the in-app About window; they have no source-bundling requirement.

## Files

- `SOURCE-OFFER.txt` — static, hand-written. Names the LGPL components and
  provides a three-year offer for source code.
- `OpenAL-Soft-COPYING.txt` — downloaded at release time from a pinned
  commit of https://github.com/kcat/openal-soft. NOT checked in; the
  release workflow fails if the download fails.

## Adding a new bundled-license-requiring dep

1. Add a row to the table above with the rationale.
2. Update the release workflow's "Stage third-party licenses" step to
   download the new license file into this folder before publish.
3. Update `SOURCE-OFFER.txt` to name the new component.

## Plugins ship their own

Plugin-side LGPL deps (e.g. `libsodium` in `gmsb-bridge-discord`) are the
plugin's responsibility — each sibling repo's release workflow stages its
own `THIRD-PARTY-LICENSES/` into its plugin zip.
