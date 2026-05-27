# SoundBoard.PluginApi

The plugin & theme SDK for [Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard) — a cross-platform soundboard for tabletop RPG sessions.

This is the **only** assembly a plugin references. It's intentionally tiny: it depends on `NAudio.Core` alone (for `ISampleProvider` / `WaveStream` in the audio contracts) — no Avalonia, no EF Core, no host internals. That keeps the surface a plugin compiles against stable and small.

## The five extension interfaces

Every plugin exports exactly one `IPlugin` implementation and opts into behavior by also implementing one or more marker interfaces:

| Interface | Adds |
|---|---|
| `IAudioCodecPlugin` | Support for a new audio file format or URL scheme. Can borrow/lend encoders & decoders via `IPluginContext.CodecRegistry`. |
| `IAudioSamplerPlugin` | A real-time DSP effect ("FX Chain") attachable at the Master, Bus, Preset, Playlist, or Track-shortcut tier. Cross-bus sidechain detection via `IPluginContext.Sidechain`. |
| `IAudioBridgePlugin` | Streams the master mix to an external destination (Discord, Zoom, Mumble, …) and can pipe remote audio back in. |
| `IThemePlugin` | One or more selectable colour palettes. A palette is a **flat** `ResourceDictionary` of named brushes — no light/dark variants; the host derives chrome from the palette's luminance. |
| `IUIExtensionPlugin` | Controls inserted at a named UI placement (`Mixer`, `TrackEditor`, `Settings`, `Overlay`). |

## Quick start

```csharp
using SoundBoard.PluginApi;

public sealed class MyThemePlugin : IThemePlugin
{
    public string Id => "com.example.my-theme";
    public string Name => "My Theme";
    public string Version => "1.0.0";
    public string Author => "Me";
    public string Description => "A single warm palette.";

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public IEnumerable<ThemePalette> GetPalettes() => new[]
    {
        new ThemePalette("warm", "Warm",
            new[] { "avares://MyThemePlugin/Themes/Warm.axaml" }),
    };
}
```

Each plugin ships a `plugin.json` manifest (`publisher`, `id`, `name`, `version`, `author`, `description`, `entryDll`, `isTheme`) at its package root. Users install by dragging the plugin's `.zip` onto **Settings → Plugin Manager**, or by extracting its folder under `…/Plugins/<id>/` (or `…/Themes/<id>/` for themes).

## Documentation

- **[Plugin developer guide](https://github.com/DevinSanders/game-master-soundboard/blob/main/docs/PLUGIN-DEV.md)** — full walkthrough: csproj setup, manifest, the Stream-handoff codec contract, sidechain, bridges, and packaging.
- **[Plugin catalog](https://github.com/DevinSanders/game-master-soundboard/blob/main/docs/PLUGINS.md)** — the first-party codec / sampler / bridge / theme plugins, each in its own sibling repo.

## Licensing

The host application is GPL-3.0-only, but a [written exception](https://github.com/DevinSanders/game-master-soundboard/blob/main/LICENSE-EXCEPTION) lets any plugin that interacts with the host **only** through the interfaces in this SDK be licensed however its author wants. Build your plugin under MIT, Apache, a commercial license — whatever you like.
