# Plugin & Theme Catalog

All plugin and theme implementations for Game Master Sound Board live
in their own sibling repositories. The main repo intentionally ships
**no plugins** — every audio format, FX algorithm, and theme is an
opt-in install.

Publisher for all first-party plugins is `github.DevinSanders`. Each
release is published as a `.zip` attached to a tagged GitHub Release;
drag the zip onto Settings → Plugin Manager to install.

See [PLUGIN-DEV.md](PLUGIN-DEV.md) for the SDK reference and the
`plugin.json` manifest schema.

---

## Codecs (`IAudioCodecPlugin`)

Each codec adds one or more file types / URL schemes to the host's
`AudioFileReaderCrossPlatform`. The host has built-in support for
`.wav` only; everything else is a plugin.

| Repo | Plugin id | Status | Decoder library |
|---|---|---|---|
| [`gmsb-codec-mp3`](https://github.com/DevinSanders/gmsb-codec-mp3) | `codec.mp3` | working | NLayer (managed) |
| [`gmsb-codec-ogg`](https://github.com/DevinSanders/gmsb-codec-ogg) | `codec.ogg` | working | NVorbis (managed) |
| [`gmsb-codec-flac`](https://github.com/DevinSanders/gmsb-codec-flac) | `codec.flac` | working | BunLabs.NAudio.Flac (managed) |
| [`gmsb-codec-opus`](https://github.com/DevinSanders/gmsb-codec-opus) | `codec.opus` | scaffold | Concentus (managed) |
| [`gmsb-codec-aac-windows`](https://github.com/DevinSanders/gmsb-codec-aac-windows) | `codec.aac.windows` | scaffold | Windows Media Foundation |
| [`gmsb-codec-aac-ffmpeg`](https://github.com/DevinSanders/gmsb-codec-aac-ffmpeg) | `codec.aac.ffmpeg` | scaffold | FFmpeg (system-installed) |
| [`gmsb-codec-webstream`](https://github.com/DevinSanders/gmsb-codec-webstream) | `codec.webstream` | scaffold | YoutubeExplode + HttpClient |

The two AAC plugins coexist — the Windows one only registers its
extensions when the host is running on Windows, so the FFmpeg one
takes over on macOS / Linux automatically.

## Samplers / FX (`IAudioSamplerPlugin`)

Real-time DSP effects, attachable to any tier (Master, Preset,
Playlist, Shortcut-to-Track) via the FX Chain editor. All entries
below are scaffolds — they load + attach correctly but pass audio
through unchanged until the DSP is implemented.

| Repo | Plugin id | Description |
|---|---|---|
| [`gmsb-sampler-reverb`](https://github.com/DevinSanders/gmsb-sampler-reverb) | `sampler.reverb` | Spatial ambience (Cathedral / Cave / Room) + wet/dry knob |
| [`gmsb-sampler-equalizer`](https://github.com/DevinSanders/gmsb-sampler-equalizer) | `sampler.equalizer` | Parametric EQ (3-band or 7-band) |
| [`gmsb-sampler-compressor`](https://github.com/DevinSanders/gmsb-sampler-compressor) | `sampler.compressor` | Dynamic-range compressor |
| [`gmsb-sampler-lowpass`](https://github.com/DevinSanders/gmsb-sampler-lowpass) | `sampler.lowpass` | Low-pass filter (through-walls / muffled) |
| [`gmsb-sampler-pitchshift`](https://github.com/DevinSanders/gmsb-sampler-pitchshift) | `sampler.pitchshift` | Pitch shifter (±24 semitones, speed-independent) |
| [`gmsb-sampler-ducking`](https://github.com/DevinSanders/gmsb-sampler-ducking) | `sampler.ducking` | Sidechain-style ducking |

## Bridges (`IAudioBridgePlugin`)

Stream the master mix into an external real-time audio destination
(Discord voice, Zoom meeting, Mumble, etc.). Each bridge owns its own
settings UI under Settings → Bridges and persists credentials under
its own plugin data folder. Bridges are optional — the host runs
without any of them.

Bridges that need a specific encoder (Opus for Discord; AAC for Zoom)
borrow it from the host's installed codec plugins via
`IPluginContext.CodecRegistry`. Install `gmsb-codec-opus` alongside any
voice bridge to enable Opus encoding without bundling a second copy.

| Repo | Plugin id | Remote | Required companion |
|---|---|---|---|
| [`gmsb-bridge-discord`](https://github.com/DevinSanders/gmsb-bridge-discord) | `bridge.discord` | Discord voice (Discord.Net + DAVE E2EE) | `gmsb-codec-opus` |

## Themes (`IThemePlugin`)

Each theme pack ships multiple selectable palettes — pick any one from
Settings → Appearance → Theme. Theme installs activate live without a
restart.

| Repo | Plugin id | Palettes |
|---|---|---|
**Developer-inspired** packs (multi-palette, ports of editor color schemes):

| Repo | Plugin id | Palettes |
|---|---|---|
| [`gmsb-theme-catppuccin`](https://github.com/DevinSanders/gmsb-theme-catppuccin) | `theme.catppuccin` | Latte, Frappé, Macchiato, Mocha |
| [`gmsb-theme-dracula`](https://github.com/DevinSanders/gmsb-theme-dracula) | `theme.dracula` | Dracula, Dracula Alabaster |
| [`gmsb-theme-solarized`](https://github.com/DevinSanders/gmsb-theme-solarized) | `theme.solarized` | Solarized Light, Solarized Dark |
| [`gmsb-theme-nord-atom`](https://github.com/DevinSanders/gmsb-theme-nord-atom) | `theme.nord-atom` | Nord, Atom One Dark |

**Genre** packs (two distinct palettes per pack — each is its own dropdown entry):

| Repo | Plugin id | Palettes |
|---|---|---|
| [`gmsb-theme-high-fantasy`](https://github.com/DevinSanders/gmsb-theme-high-fantasy) | `theme.high-fantasy` | Tavern Hearth (parchment & ink, light), Deep Dungeon (iron & ember, dark) |
| [`gmsb-theme-sci-fi`](https://github.com/DevinSanders/gmsb-theme-sci-fi) | `theme.sci-fi` | Starship Command (telemetry & clean-room, light), Neon Cyberpunk (the grid, dark) |
| [`gmsb-theme-horror-occult`](https://github.com/DevinSanders/gmsb-theme-horror-occult) | `theme.horror-occult` | The Sanitarium (fog & ash, light), The Eldritch Void (velvet & blood, dark) |
| [`gmsb-theme-industrial-steampunk`](https://github.com/DevinSanders/gmsb-theme-industrial-steampunk) | `theme.industrial-steampunk` | The Inventor's Desk (brass & blueprint, light), Smog & Aether (soot & arcane, dark) |

---

## Building a sibling plugin locally

Sibling repos use a relative `ProjectReference` to
`SoundBoard.PluginApi` (until the SDK is published to NuGet). They
assume the main repo is checked out as a sibling folder:

```
<parent folder>/
├── Game Master Sound Board/    ← this repo
├── gmsb-codec-mp3/             ← any plugin repo, beside the main repo
├── gmsb-sampler-reverb/
└── ...
```

To build + package any plugin:

```powershell
cd gmsb-codec-mp3
dotnet build src/Mp3CodecPlugin.csproj
pwsh scripts/package.ps1
# → dist/codec.mp3-1.0.0.zip
```

Then drop the zip on Settings → Plugin Manager in a running app.
