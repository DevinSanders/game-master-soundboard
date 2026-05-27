# Game Master Sound Board

A cross-platform soundboard built for tabletop RPG sessions. Cue ambient loops, fire one-shot stings, switch between full multi-track scenes, and pipe the mix to Discord — all from one window.

<!-- Add a screenshot here once the UI is camera-ready -->
<!-- ![Screenshot](docs/screenshot.png) -->

## Features

- **Library** of tagged tracks with per-track volume, fades, loop points, start delay, and waveform preview.
- **Presets** — single-card scenes that play several tracks together with independent overrides, editable live while they're playing.
- **Playlists** — sequential auto-advance through tracks and presets.
- **Soundboard shortcuts** — paginated grid of one-tap buttons for the table.
- **Master mixer** with global volume, per-track visualizers, and a master-output visualizer in the Now Playing bar.
- **Discord output** — pipe the mix into a Discord voice channel without blocking local playback.
- **`gmsound://` URI scheme** — launch tracks, presets, and playlists directly from Obsidian, browser links, or anywhere markdown renders. There's a built-in URI builder for generating links.
- **Plugin system** — install codec, DSP, voice-bridge, theme, and UI-extension plugins from the Plugin Manager (drag a `.zip` onto the drop zone) or by extracting a plugin folder under `…/Plugins/<id>/`. Each plugin ships a `plugin.json` manifest and loads into its own isolated `AssemblyLoadContext`.
- **Cross-platform** — Windows (NAudio / WASAPI / WaveOut), macOS and Linux (OpenAL Soft via Silk.NET.OpenAL).

## Install

Every release on the [Releases page](../../releases) ships installers and package-manager files for Windows, macOS, and Linux. The short version:

| Platform | Easy install |
|---|---|
| Windows | `GameMasterSoundBoard-Setup-<version>.exe`, or `choco install gmsoundboard` |
| macOS (Apple Silicon / Intel) | `*-osx-arm64.dmg` / `*-osx-x64.dmg`, or `brew install --cask gmsoundboard` (tap coming soon) |
| Linux Debian/Ubuntu/Raspberry Pi | `gmsoundboard_<version>_amd64.deb` / `_arm64.deb` |
| Linux Fedora/RHEL | `gmsoundboard-<version>.x86_64.rpm` / `.aarch64.rpm` |
| Linux portable | `GameMasterSoundBoard-<version>-x86_64.AppImage` (x64 only) |

Universal `.zip` / `.tar.gz` archives are also attached for manual install on any OS.

**See [INSTALL.md](INSTALL.md) for the full guide**, including Gatekeeper / SmartScreen workarounds (the app isn't yet code-signed) and uninstall instructions.

Pre-release and RC builds are published to the same Releases page and clearly marked as pre-release.

## Build from source

Prerequisites: **.NET 10 SDK**.

```powershell
git clone https://github.com/DevinSanders/game-master-soundboard.git
cd "game-master-soundboard"

dotnet build SoundBoard.slnx

# Full app (single-instance, gmsound:// activation)
dotnet run --project SoundBoard.Desktop

# UI project directly (no single-instance lock, Avalonia DevTools enabled)
dotnet run --project SoundBoard.UI -- --debug
```

`SoundBoard.slnx` is the new SLNX format — use the `dotnet` CLI, not legacy `.sln` tools.

## Where your data lives

| Item | Path |
|---|---|
| Default library (SQLite) | `%LocalAppData%\GameMasterSoundBoard\Libraries\default.db` |
| Other libraries | `%LocalAppData%\GameMasterSoundBoard\Libraries\*.db` |
| Settings | `%LocalAppData%\GameMasterSoundBoard\settings.json` |
| Logs (with `--debug`) | `%LocalAppData%\GameMasterSoundBoard\logs\` |
| Plugins | `%LocalAppData%\GameMasterSoundBoard\Plugins\` |

On macOS the base path is `~/Library/Application Support/GameMasterSoundBoard/`; on Linux it's `~/.local/share/GameMasterSoundBoard/`.

You can swap libraries from inside the app (Settings → Libraries) and import/export the whole thing — tracks, presets, playlists, shortcuts, and settings — as a single bundle.

## Plugins

Install a plugin by dragging its `.zip` onto **Settings → Plugin Manager**, or manually extract its folder (containing `plugin.json` + the entry DLL) under `…/Plugins/<id>/` for code plugins or `…/Themes/<id>/` for themes. Each plugin implements one or more marker interfaces from `SoundBoard.PluginApi`:

- `IAudioCodecPlugin` — add support for a new audio file format or URL scheme. Codec plugins can also borrow from each other via the `IPluginContext.CodecRegistry` — e.g. `gmsb-codec-webstream` carries no decoders of its own and dispatches HTTP bytes to whichever format-specific codec is installed.
- `IAudioSamplerPlugin` — DSP effects (FX Chain) attachable at the Master, Bus, Preset, Playlist, or Track-shortcut tier. Sidechain support via `IPluginContext.Sidechain` enables cross-bus detection (e.g. ducker on Music keyed to SFX).
- `IAudioBridgePlugin` — stream the master mix to Discord, Zoom, Mumble, etc. Bridges borrow audio encoders (Opus, AAC) from installed codec plugins instead of bundling their own.
- `IThemePlugin` — re-skin the app via Avalonia resource overrides. Theme installs are live (no restart).
- `IUIExtensionPlugin` — add controls at well-known UI insertion points (Mixer, Track Editor, Settings, Overlay).

Each plugin loads into its own collectible `AssemblyLoadContext` for isolation, so enabling and disabling plugins doesn't conflict. See [docs/PLUGINS.md](docs/PLUGINS.md) for the first-party plugin catalog and [docs/PLUGIN-DEV.md](docs/PLUGIN-DEV.md) for the SDK reference.

## URI scheme

Construct a `gmsound://` link from anywhere markdown renders (Obsidian, GitHub, a browser, your DM notes) and the OS will route it to the running app:

```
gmsound://play/track/123?volume=0.8&fadeIn=2
gmsound://play/preset/tavern-night
gmsound://playlist/dungeon-crawl/next
```

The app has a URI builder (Tools → URI Builder) that produces these for you, including Markdown and HTML snippets.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, conventions, and the easy-to-miss footguns specific to this codebase.

Bug reports especially welcome — please attach a `--debug` log when filing one (the bug-report template walks through it).

## License

[GPL v3](LICENSE) with the additional exception in [LICENSE-EXCEPTION](LICENSE-EXCEPTION).

The bundled [RPG Awesome](https://nagoshiashumari.github.io/Rpg-Awesome/) icon font is licensed separately under [SIL OFL 1.1](https://scripts.sil.org/OFL); see the in-app About page for full attribution.
