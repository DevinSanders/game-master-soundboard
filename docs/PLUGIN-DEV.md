# Plugin & Theme Developer Guide

Game Master Sound Board can be extended with **plugins** (audio codecs,
DSP effects, UI panels) and **themes** (Avalonia resource overrides).
Both ship as .NET class library DLLs and are loaded into isolated
`AssemblyLoadContext`s at app startup.

This guide walks through writing a plugin from scratch. Working
implementations and stubs for every plugin category live in their own
sibling repositories — see [`docs/PLUGINS.md`](PLUGINS.md) for the
catalog. Three good starting points:

- `gmsb-codec-mp3` — minimal `IAudioCodecPlugin` (NLayer-backed MP3).
- `gmsb-sampler-reverb` — `IAudioSamplerPlugin` scaffold with the
  factory-instance-editor contract.
- `gmsb-theme-catppuccin` — `IThemePlugin` with four flat palettes
  (Latte, Frappé, Macchiato, Mocha), demonstrating the multi-palette
  pattern (one pack, several selectable looks).

---

## 1. The SDK

All extension contracts live in the **`SoundBoard.PluginApi`** assembly,
shipped as its own NuGet package. It pulls in only `NAudio.Core` (for
`ISampleProvider` / `WaveStream` in the audio plugin contracts) — no
Avalonia, no EF Core, no Discord. Your plugin will not be forced to ship
those.

```xml
<ItemGroup>
  <PackageReference Include="SoundBoard.PluginApi" Version="1.0.0" />
</ItemGroup>
```

Until the package is published, reference it directly from this repo:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\SoundBoard.PluginApi\SoundBoard.PluginApi.csproj" />
</ItemGroup>
```

## 1.5. The `plugin.json` manifest

Every plugin folder must contain a `plugin.json` at its root. The
manifest is the host's source of truth for identity, presentation, and
which DLL to load — folder names and zip names carry no meaning to the
scanner. A folder without a manifest is skipped by the runtime scanner
with a warning; a zip without one is rejected by the installer with a
clear error.

```json
{
  "publisher": "com.acme",
  "id": "sunset-theme",
  "name": "Sunset Theme Pack",
  "version": "1.0.0",
  "author": "Acme",
  "description": "Warm + cool palette pack.",
  "entryDll": "Acme.SunsetTheme.dll",
  "isTheme": true
}
```

| Field | Required | Notes |
|---|---|---|
| `publisher` | yes | Author / organisation owning this plugin's release lineage. Use a reverse-DNS domain (`com.acme`, `org.example`) or a stable handle (`github.username`). The installer uses `(publisher, id)` as the upgrade key — see [Upgrade lineage](#upgrade-lineage) below. |
| `id` | yes | Stable identifier within your publisher namespace. Must match your `IPlugin.Id`. Combined with `publisher` to form the on-disk folder name (`<safe-publisher>__<safe-id>`) and as the key in `settings.json` (`EnabledPluginIds`, `SelectedThemeId`). |
| `name` | yes | Display name shown in Settings. Defaults to `id` if omitted. |
| `version` | no | Free-form. Displayed in the settings row and in the "replaces v1.1" install summary. |
| `author` | no | Free-form. |
| `description` | no | Free-form. |
| `entryDll` | yes | File name of the DLL that contains your `IPlugin` type. Must be a bare file name — no `/`, `\`, or `..`. |
| `isTheme` | no | `true` for `IThemePlugin`, `false` (default) for everything else. Drives install-time routing (`Themes\` vs `Plugins\`) without executing your DLL. The runtime scanner double-checks the actual interface and logs a warning on mismatch. |

### Upgrade lineage

The installer uses the `(publisher, id)` tuple from the manifest to
decide what to do with each dropped zip:

- **Same `(publisher, id)` as an existing install** → the existing
  folder is wiped and replaced with the new version. This is the
  upgrade path. The install summary tells the user "replaces v1.1".
- **Same `id` but different `publisher`** → the incoming plugin
  installs side-by-side in its own folder. Both load; the user can
  enable / try both. The install summary notes "installed alongside
  com.other-author/your-id" so the user can tell them apart.
- **No matching `id` at all** → fresh install.

The folder-name format guarantees the side-by-side case can't
collide on disk: `com_acme__sunset-theme` and
`com_other-author__sunset-theme` are obviously distinct paths under
`Themes\`.

**Why `publisher` matters for you as an author.** As long as every
release you ship uses the same `publisher`, users who installed your
v1.0 get a clean v1.1 upgrade when they drop your new zip. If you
change `publisher` mid-lineage (rebranded, repo moved), your users
will see your new version installed alongside the old one — they'd
need to manually uninstall the old folder. Pick a stable
`publisher` once and stick with it.

**Reverse-DNS isn't enforced.** It's just a convention that makes
collisions across the world astronomically unlikely. Pick whatever
makes sense for you: `com.your-domain`, `github.your-handle`,
`org.your-org`. Just don't pick something generic like `plugins`
that another author might also pick by accident.

The manifest must end up **at the root of your plugin folder**, next to
the entry DLL — not inside a `Resources/` or `Properties/` subfolder.
In a csproj, the standard idiom is:

```xml
<ItemGroup>
  <None Update="plugin.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

…so the manifest lands in `bin/Release/net10.0/` alongside your DLL.
When you zip that folder for distribution, `plugin.json` is at the zip
root and the host's installer picks it up automatically.

## 2. The five extension interfaces

| Interface | What it does | Where the host wires it |
|---|---|---|
| `IThemePlugin` | Returns one or more named `ThemePalette`s; each palette carries `avares://` URIs to a flat `ResourceDictionary` of brushes (or `Styles`) baked into your DLL. | Applied by `ThemeService` when the user picks the palette — the brushes win the `DynamicResource` lookup regardless of variant, and the host derives the Avalonia chrome variant from the theme's background luminance. |
| `IAudioCodecPlugin` | Adds support for new audio file extensions. Optionally exposes encoder/decoder factories that other plugins can borrow. | Registered into the cross-platform reader; takes precedence over built-ins only for novel extensions. |
| `IAudioSamplerPlugin` | Inserts a real-time DSP node into the host's FX chain at one of the attachment points declared by `SupportedAttachments` (Master, Bus, Preset, Playlist, or Track-shortcut). Sidechain detection across buses available via `IPluginContext.Sidechain`. | Wraps the chain tail at the selected attachment tier; runs on the audio thread. |
| `IUIExtensionPlugin` | Adds a UI panel at one of the named placements (`Mixer`, `TrackEditor`, `Settings`, `Overlay`). | Hosted at the requested placement. |
| `IAudioBridgePlugin` | Streams the master mix into an external real-time audio destination (Discord, Zoom, Mumble, …). Optionally pipes received audio back into the local mix. | Per-bridge worker thread pulls broadcast PCM from `MasterMixer`; bridge encodes / uploads on its own schedule. UI rendered in Settings → Bridges. |

Every plugin DLL must export **exactly one** type implementing `IPlugin`,
and that type must have a **public parameterless constructor**.

## 3. Writing a theme

Themes are the simplest extension. The contract is one method,
`GetPalettes()`, returning one or more `ThemePalette`s. Each palette
becomes a separate dropdown entry in the host's Appearance settings,
prefixed with your plugin's `Name`. A "theme pack" with five palettes is
one plugin DLL with five `ThemePalette` entries — not five separate DLLs.

### csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- AssemblyName MUST match the segment used in avares:// URIs. -->
    <AssemblyName>Acme.SunsetTheme</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SoundBoard.PluginApi" Version="1.0.0" />
    <PackageReference Include="Avalonia" Version="12.0.3" />
  </ItemGroup>
  <ItemGroup>
    <!-- Each AvaloniaResource gets baked into the DLL at:
           avares://<AssemblyName>/<this Include path>
         The host's AvaloniaXamlLoader pulls them out by that URI.
         Add one item per palette .axaml. -->
    <AvaloniaResource Include="Themes/Sunset.axaml" />
    <AvaloniaResource Include="Themes/Twilight.axaml" />
  </ItemGroup>
</Project>
```

### Themes/Sunset.axaml

A theme is a **flat set of colours — no light/dark variants.** The host
has no user-facing Dark/Light toggle: each theme is one selectable look.
Author a plain `ResourceDictionary` that overrides whichever of the host's
named semantic brushes you care about, as `SolidColorBrush` entries:

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="ContentBackground" Color="#2A1810" />
  <SolidColorBrush x:Key="PanelBackground1"  Color="#1F120B" />
  <SolidColorBrush x:Key="PrimaryAccent"     Color="#FF7A1F" />
  <SolidColorBrush x:Key="TextPrimary"       Color="#FFE6CC" />
  <!-- ...override however many of the host's named brushes you want.
       See §"Semantic key vocabulary" for the full list. -->
</ResourceDictionary>
```

Do **not** wrap these in `ResourceDictionary.ThemeDictionaries` or split
them into `Dark`/`Light` blocks. The host applies your brushes so they win
Avalonia's `DynamicResource` lookup regardless of the active variant, and
it derives the Avalonia chrome variant — used only for un-themed Fluent
controls like scrollbars, combo-box popups and focus rings — from your
theme's background luminance. You declare the colours; the host figures
out light vs dark on its own.

The host references all theme-sensitive colors via `DynamicResource`, so
once your overrides land the change propagates without touching any
control templates.

### SunsetThemePlugin.cs

```csharp
using SoundBoard.PluginApi;

public sealed class SunsetThemePlugin : IThemePlugin
{
    public string Id          => "com.acme.sunset-theme";
    public string Name        => "Sunset Theme Pack";
    public string Version     => "1.0.0";
    public string Author      => "Acme";
    public string Description => "Warm + cool palette pack.";

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public IEnumerable<ThemePalette> GetPalettes() => new[]
    {
        new ThemePalette(
            Id: "sunset",
            Name: "Sunset",
            ResourceUris: new[] { "avares://Acme.SunsetTheme/Themes/Sunset.axaml" }),

        new ThemePalette(
            Id: "twilight",
            Name: "Twilight",
            ResourceUris: new[] { "avares://Acme.SunsetTheme/Themes/Twilight.axaml" }),
    };
}
```

The user sees two entries in the dropdown: **"Sunset Theme Pack: Sunset"**
and **"Sunset Theme Pack: Twilight"**. The plugin id (`com.acme.sunset-theme`)
plus the palette id (`sunset` or `twilight`) together identify the selection
in `settings.json`.

## 3.5. Inter-plugin codec dispatch

Codecs can decode from either a file path (the host's normal `CreateStream(string)` flow) **or** a pre-opened `Stream` handed in by another plugin. The Stream overload lets transport plugins like `gmsb-codec-webstream` open a network connection and hand the raw bytes to whichever format-specific codec the user has installed — instead of bundling NLayer / NVorbis / FlacReader / etc. themselves.

### Adding Stream input to your codec

Three additions on top of the basic `IAudioCodecPlugin`:

```csharp
public sealed class MyCodecPlugin : IAudioCodecPlugin
{
    // ...existing IPlugin properties, SupportedPatterns, CreateStream(string)...

    // 1. Declare the MIME types this codec accepts. Used by
    //    IAudioCodecRegistry.GetByContentType("...") for dispatch.
    public IEnumerable<string> SupportedContentTypes => new[]
    {
        "audio/mpeg",      // canonical
        "audio/mp3",       // non-standard but seen in the wild
    };

    // 2. Opt in to the Stream overload.
    public bool SupportsStreamInput => true;

    // 3. Implement the overload. Ownership of `source` transfers — your
    //    returned WaveStream's Dispose() MUST dispose `source`.
    //    `formatHint` is advisory (a MIME or extension); your decoder
    //    must still validate the bytes itself.
    public WaveStream CreateStream(Stream source, string formatHint)
        => new MyWaveStream(source);
}
```

Inside `MyWaveStream`, propagate `CanSeek` from the input — a live HTTP / ICY stream isn't seekable, and the host needs to know:

```csharp
public override bool CanSeek => _owningStream?.CanSeek ?? true;

public override long Position
{
    get => _decoder.BytePosition;
    set
    {
        if (!CanSeek) return;          // refuse seeks on live streams
        _decoder.SeekTo(value);
    }
}

protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _decoder.Dispose();
        try { _owningStream?.Dispose(); } catch { }   // we took ownership
    }
    base.Dispose(disposing);
}
```

### Consuming the registry from a transport plugin

If you're writing a transport plugin (URL opener, archive reader, etc.), use the registry on the plugin context to find the right decoder:

```csharp
public void Initialize(IPluginContext context)
{
    _codecRegistry = context.CodecRegistry;
}

public WaveStream CreateStream(string source)
{
    var probe = HttpProbe.Run(_http, source);

    // MIME first (more reliable), URL extension fallback.
    var codec = _codecRegistry?.GetByContentType(probe.ContentType ?? "")
             ?? _codecRegistry?.GetByExtension(Path.GetExtension(new Uri(source).AbsolutePath))
             ?? throw new NotSupportedException("No installed codec handles this stream.");

    if (!codec.SupportsStreamInput)
        throw new NotSupportedException($"Codec '{codec.Id}' doesn't support Stream input.");

    Stream transport = probe.LooksSeekable
        ? new SeekableHttpStream(_http, source, probe.ContentLength!.Value)
        : LiveTransportStream.Open(_http, source);

    // Codec takes ownership of `transport`. If construction throws,
    // make sure to dispose the transport — otherwise you'll leak the
    // HTTP connection.
    try { return codec.CreateStream(transport, probe.ContentType ?? ""); }
    catch { transport.Dispose(); throw; }
}
```

### Safety boundaries (read this — it's the contract)

The registry is the seam between plugins; a few rules keep it from becoming a vulnerability surface:

- **Stream ownership transfers** on `CreateStream(Stream, ...)`. The codec's returned `WaveStream.Dispose()` MUST dispose the input Stream. Callers hand off and stop tracking — no double-dispose, no leaks.
- **Format hint is advisory.** Codecs MUST validate input bytes themselves (NLayer / NVorbis / FlacReader / Concentus all throw on malformed input — preserve that behaviour). Don't trust the hint as a security check; a malicious caller could lie.
- **Registry is a one-shot snapshot** built once after every plugin finishes loading at app startup. No mutation API — plugins can read but not inject. Lookups are thread-safe by construction.
- **No recursive codec lookups.** A codec MUST NOT call the registry from within its own `CreateStream`. Dispatch is the calling plugin's job. Keeps the plugin dependency graph a tree, not a cycle.
- **Audio-thread safety is the transport's responsibility.** A network-backed Stream's `Read()` must not block indefinitely — buffer on a background thread and serve from an in-memory buffer.
- **Registry-timing note.** Plugins receive the registry via `IPluginContext.CodecRegistry`, but the snapshot is built AFTER every plugin finishes `Initialize` (so codecs that load later are still findable). Reading the registry inside `Initialize` may return an empty list; query at `CreateStream` time instead.

### Optional: exposing encoder / decoder factories

Codecs can opt into being borrowed by bridge plugins (Discord / Zoom / Mumble / …) and any other plugin that needs to encode or decode the format outside of the file-playback pipeline. This is how `gmsb-codec-opus` lets `gmsb-bridge-discord` reuse its Concentus-based Opus encoder rather than bundling a second copy.

Three optional members on `IAudioCodecPlugin`:

```csharp
public sealed class MyOpusCodecPlugin : IAudioCodecPlugin
{
    // ...existing IPlugin / CreateStream members...

    // Flip this when CreateEncoder is implemented. Bridges check it as a
    // fast filter before asking for an encoder.
    public bool SupportsEncoding => true;

    // Frame-oriented streaming encoder. Returns null when the requested
    // sample rate / channel count / bitrate isn't supported by your
    // underlying library; bridges fall back to a user-readable error.
    public IAudioFrameEncoder? CreateEncoder(int sampleRate, int channels, int bitrate)
        => MyEncoder.TryCreate(sampleRate, channels, bitrate);

    // Optional symmetric decoder for plugins receiving compressed
    // packets (Mumble bridge piping remote speakers back into the mix).
    public IAudioFrameDecoder? CreateDecoder() => new MyDecoder();
}
```

Implementing `IAudioFrameEncoder`:

```csharp
internal sealed class MyEncoder : IAudioFrameEncoder
{
    // Codec-defined fixed-size frames. Opus = 960 samples/channel @ 48 kHz.
    public int FrameSamples => 960;
    public int Channels { get; }
    public int SampleRate { get; }

    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        // pcm length MUST equal FrameSamples * Channels (interleaved
        // float in [-1, 1]). Return actual packet byte count written.
        return _underlying.EncodeFrame(pcm, packet);
    }

    public void Dispose() { /* pure-managed; or release native handles here */ }
}
```

Borrowing your encoder from another plugin:

```csharp
var opus = context.CodecRegistry?.GetByExtension(".opus");
if (opus == null || !opus.SupportsEncoding)
    throw new InvalidOperationException("Install gmsb-codec-opus to enable Opus encoding.");

using var encoder = opus.CreateEncoder(48000, 2, 64_000);
// ...feed PCM frames, get packets...
```

Same safety rules as the Stream-input path: encoders are caller-owned (single thread, single instance per outbound stream, dispose on disconnect). Encoders may hold significant codec state (Opus's predictor / lookahead) — never reuse one across reconnects.

## 4. Writing a DSP sampler plugin

DSP plugins wrap the audio chain at one of five attachment tiers declared
by the plugin's `SupportedAttachments` (`Master`, `Bus`, `Preset`, `Playlist`,
or `Shortcut`). The contract: implement
`CreateEffect(ISampleProvider source) => new YourEffect(source)`.

`Read()` runs on the audio thread. **No allocations, no UI access, no
blocking I/O.** The mixer is 48 kHz IEEE-float stereo — your effect must
preserve that.

```csharp
using NAudio.Wave;
using SoundBoard.PluginApi;

public sealed class GainPlugin : IAudioSamplerPlugin
{
    public string Id => "com.acme.gain";
    public string Name => "Gain";
    public string Version => "1.0.0";
    public string Author => "Acme";
    public string Description => "Constant attenuation.";
    public UIPlacement Placement => UIPlacement.None;

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public ISampleProvider CreateEffect(ISampleProvider source) => new GainEffect(source);
    public object? CreateControl() => null;
}

internal sealed class GainEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    public GainEffect(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        var n = _source.Read(buffer, offset, count);
        for (int i = 0; i < n; i++) buffer[offset + i] *= 0.5f;
        return n;
    }
}
```

## 4.5. Writing an audio bridge plugin

Bridge plugins stream the host's master mix into an external real-time audio destination — Discord voice, Zoom, Google Meet, Mumble, Teamspeak, anything that accepts a continuous PCM (or compressed-frame) feed. The reference implementation is [`gmsb-bridge-discord`](https://github.com/DevinSanders/gmsb-bridge-discord).

### Why a separate plugin

Voice-platform integrations have huge, platform-specific dependency footprints. The Discord bridge alone ships ~25 MB of Discord.Net + libdave native binaries across 5 RIDs. Users who never run a bot shouldn't pay for that. Each bridge is a separate plugin so the user installs only the destinations they actually use, and the host runs without any of them.

### The contract

```csharp
public interface IAudioBridgePlugin : IPlugin
{
    BridgeStatus Status { get; }
    string? StatusDetail { get; }
    Exception? LastError { get; }
    bool WantsOutboundAudio { get; }
    event EventHandler? StatusChanged;

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    void SendOutboundPcm(ReadOnlySpan<float> pcm);
    object CreateSettingsControl(IBridgeHost host, IPluginContext context);
}
```

The host's `AudioBridgeHost` runs one dedicated worker thread per loaded bridge. Each worker pulls from `MasterMixer`'s bounded broadcast queue and calls `SendOutboundPcm`. **You never touch the audio thread directly** — the worker thread is your seam.

### Status surface

Drive the `Status` property through these transitions and fire `StatusChanged` from the same thread that mutated it. The host marshals to the UI thread before re-reading.

| Status | When to set it |
|---|---|
| `Disconnected` | Idle. Initial state, and the clean state after `DisconnectAsync` completes. |
| `Connecting` | `ConnectAsync` has started but steady state isn't reached yet. UIs show a spinner. |
| `Connected` | Audio is flowing in at least one direction. Set `WantsOutboundAudio = true` here. |
| `Failed` | Last connect attempt failed, or a connected bridge was dropped by the remote. Populate `LastError`. |

### Outbound audio (host → bridge)

The host calls `SendOutboundPcm` from the bridge's worker thread, never the audio thread. The PCM is **48 kHz stereo IEEE-float, interleaved (LRLRLR…)**. Each chunk is a snapshot the host owns — copy whatever you need before returning. Re-frame and encode on your side; the worker will keep feeding chunks at ~100 Hz.

A typical loop: accumulate chunks until you have a full codec frame (Opus needs 960 samples per channel, 20 ms), then encode that frame and write it to your network sink. Drop chunks if your sink can't keep up — backpressure on the worker thread is fine, backpressure on the audio thread isn't (and would only happen if you held a lock for ~10 ms, which you shouldn't).

### Borrowing a codec encoder

Bridges that need compressed audio (Opus for Discord, AAC for many meeting platforms) should **borrow** the encoder from an installed codec plugin rather than bundling a second copy. See [Section 3.5 "Optional: exposing encoder / decoder factories"](#optional-exposing-encoder--decoder-factories) for the codec side.

> **Important nuance.** Borrowing the encoder saves you from shipping a duplicate codec for OUTBOUND audio, but it doesn't relieve you of native dependencies your underlying platform SDK requires. Discord.Net, for instance, still needs native `libopus` and `libsodium` loaded for inbound audio decoding and parts of the voice handshake — even when the OUTBOUND path is fed pre-encoded packets via `CreateOpusStream()`. Bundle those natives in your bridge plugin's csproj (`OpusSharp.Natives`, `libsodium`, etc.) — the host's per-plugin native probe will find them under `runtimes/<rid>/native/`. The borrowed-encoder pattern still earns its keep for future platforms that don't have a built-in native opus dep (e.g. a Mumble bridge talking the raw Mumble protocol directly), and it keeps the host's audio thread on a single managed codec for outbound work.

```csharp
public async Task ConnectAsync(CancellationToken ct)
{
    // Borrow Opus from codec.opus. Surface a clear error if the codec
    // plugin isn't installed — don't crash later in the network handshake.
    var opus = _context.CodecRegistry?.GetByExtension(".opus");
    if (opus == null)
        throw new InvalidOperationException(
            "Discord Bridge requires the Opus codec plugin (codec.opus) to be installed.");
    if (!opus.SupportsEncoding)
        throw new InvalidOperationException(
            "The installed codec.opus plugin doesn't advertise encoding support.");

    _encoder = opus.CreateEncoder(48000, 2, 64_000)
        ?? throw new InvalidOperationException("codec.opus refused to build a stereo encoder.");

    // ...rest of your platform's connect handshake...
}
```

Document the codec dependency in your plugin's README so users know to install it. The host doesn't enforce plugin-dependency manifests today; surfacing a meaningful runtime error is your responsibility.

### Inbound audio (bridge → host)

If your bridge supports receive — e.g. a Mumble bridge piping other channel members back into the local mix — use the `IBridgeHost` you got at `CreateSettingsControl` time:

```csharp
public object CreateSettingsControl(IBridgeHost host, IPluginContext context)
{
    _host = host;
    // ...build and return your settings control...
}

// Later, when a remote user starts speaking:
private void OnRemoteUserJoined(string username)
{
    _sinksByUser[username] = _host!.OpenInboundStream(username);
    // Each sink shows up as a separate mixer strip the user can volume / pan.
}

private void OnRemoteAudioReceived(string username, ReadOnlySpan<float> pcm)
{
    if (_sinksByUser.TryGetValue(username, out var sink))
        sink.Push(pcm);   // 48 kHz stereo float interleaved
}

private void OnRemoteUserLeft(string username)
{
    if (_sinksByUser.Remove(username, out var sink))
        sink.Dispose();   // removes the strip from the mixer
}
```

For a single anonymous receive stream (no per-speaker breakdown), use `host.PushInboundPcm(pcm)` directly — that auto-creates a single "Bridge In" strip and reuses it.

### Settings UI

The bridge owns its own settings UI. The host's Settings → Bridges section calls your `CreateSettingsControl(host, context)` and embeds the returned Avalonia `Control` in a card. Build the control however you like — AXAML, MVVM, or pure code. The reference implementation (`gmsb-bridge-discord`) uses pure code (no AXAML) so the plugin doesn't drag in the Avalonia XAML compiler:

```csharp
public object CreateSettingsControl(IBridgeHost host, IPluginContext context)
{
    var grid = new Grid { /* ...token / guild / channel rows... */ };

    var connectBtn = new Button { Content = "Connect" };
    connectBtn.Click += async (_, _) =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await ConnectAsync(cts.Token); }
        catch (Exception ex) { /* show error in status text */ }
    };
    grid.Children.Add(connectBtn);

    // Live status updates — marshal to UI thread, our event may fire
    // from Discord.Net / Zoom SDK / etc threads.
    EventHandler statusHandler = (_, _) =>
        Dispatcher.UIThread.Post(() => statusText.Text = StatusLine());
    StatusChanged += statusHandler;
    grid.DetachedFromVisualTree += (_, _) => StatusChanged -= statusHandler;

    return grid;
}
```

The return type is `object` rather than `Control` so the SDK doesn't force Avalonia into its public surface — but the host casts the returned value to `Control`, so it must be one.

### Config persistence

Persist credentials and settings under `IPluginContext.PluginDataPath/config.json` (or whatever filename you prefer in that folder). The host's settings export does NOT include bridge config — each bridge handles its own. See `DiscordBridgePlugin.LoadConfigFromDisk` / `SaveConfigToDisk` for the canonical pattern using `System.Text.Json`.

### Threading rules

| Thread | Code that runs there |
|---|---|
| UI thread | `CreateSettingsControl`, button click handlers, property setters bound from the UI. Marshal to it from other threads via `Dispatcher.UIThread.Post` before touching Avalonia state. |
| `AudioBridgeHost` worker thread | `SendOutboundPcm`. Synchronous I/O against your network sink is fine here — that's why the worker exists. |
| Audio thread (NEVER touched by your plugin directly) | The host's `MasterMixer.Read`. Your `SendOutboundPcm` sees post-mix audio one cycle later. |
| Platform SDK threads (Discord.Net, Zoom SDK, …) | Status events, log events. Marshal to UI before touching controls. |

### Restart-required behaviour

Bridges are wired into `AudioBridgeHost` at startup via `RegisterBridges`. There's no hot-load path today — installing a new bridge plugin flips `RestartRequired = true` and the user clicks Restart Now. Themes still hot-load; codec / sampler / bridge plugins don't.

### Lifecycle expectations

- **`Initialize`**: Read your config from disk. Don't open network sockets yet.
- **`ConnectAsync`**: Driven from a user click in your settings UI. Synchronously transition to `Connecting` and fire `StatusChanged`. Do the handshake on a background Task. Set `WantsOutboundAudio = true` only when you reach `Connected` and are ready to consume PCM.
- **`SendOutboundPcm`**: Called by the worker thread once `WantsOutboundAudio` is true. May see chunks before your connection is fully established (small race window) — guard with a null-check on your encoder / output stream.
- **`DisconnectAsync`**: Tear down the network sink, dispose the encoder, flip `WantsOutboundAudio` to false, transition to `Disconnected`. Idempotent — calling on an already-disconnected bridge is a no-op.
- **`Shutdown`** (from `IPlugin`): Called by the host at app close. Call `DisconnectAsync().GetAwaiter().GetResult()` here so the network cleans up before the process dies.

### Manifest

```json
{
  "publisher": "com.example",
  "id": "bridge.myservice",
  "name": "My Service Bridge",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Streams audio to MyService. Requires codec.opus.",
  "entryDll": "MyServiceBridgePlugin.dll",
  "isTheme": false
}
```

## 4.6. Sidechain — cross-bus detection

Sidechain detection lets a sampler attached to one bus (the **carrier**) observe samples from a different bus (the **trigger**). The canonical use is a ducker that pulls Music down when SFX plays — attach to Music as the carrier, subscribe to the SFX bus as the trigger, drive gain reduction from the trigger's envelope.

### The contract

A plugin gets the registry from `IPluginContext.Sidechain` at `Initialize` time:

```csharp
public sealed class DuckingPlugin : IAudioSamplerPlugin
{
    private IPluginContext? _context;
    public SamplerAttachmentPoints SupportedAttachments
        => SamplerAttachmentPoints.Master | SamplerAttachmentPoints.Bus;
    public void Initialize(IPluginContext context) => _context = context;
    public ISamplerInstance CreateInstance() => new DuckingInstance(_context);
    ...
}
```

The instance enumerates available sources lazily. **Don't query `_context.Sidechain.GetSources()` from inside `IPlugin.Initialize`** — at that point the host hasn't finished constructing the registry, so the property is `null` and stays `null` for any plugin that captured the reference eagerly. Query from `CreateInstance` / `CreateEffect` / `CreateControl` (any path that runs after `Initialize` finishes), or query and re-query on every use. The host's `IPluginContext.Sidechain` becomes non-null shortly after `Initialize` returns, but plugins that cache the `null` reference get stuck.

Subscribe like this:

```csharp
var source = _context.Sidechain.GetSourceById("bus:3"); // e.g. SFX
_subscription = source.Subscribe(OnSidechainBuffer);

private void OnSidechainBuffer(float[] buffer, int count)
{
    // RUNS ON THE AUDIO THREAD. No allocations, no UI access, no blocking.
    // Update an envelope from peak/RMS — don't retain the buffer reference.
    float peak = 0f;
    for (int i = 0; i < count; i++)
    {
        var s = MathF.Abs(buffer[i]);
        if (s > peak) peak = s;
    }
    // Smooth via a one-pole IIR, write to a Volatile-protected envelope
    // field; the carrier's CreateEffect Read reads that field.
    _env = 0.9f * _env + 0.1f * peak;
}
```

The carrier's `CreateEffect` reads `_env` and applies gain reduction. Dispose the subscription in `ISamplerInstance.Dispose` to detach.

### Source ids

`ISidechainSource.Id` is a stable opaque string (`"bus:<id>"` today; treat as opaque). Persist the chosen id in your config so reattach across launches uses the same source:

```csharp
public string SerializeConfig() => JsonSerializer.Serialize(new
{
    SidechainSourceId = _sidechainSourceId,
    /* other params */
});
```

On `DeserializeConfig`, call `GetSourceById` on the persisted id — `null` means the bus was deleted between launches, fall back to "self-detect" or "(none)".

### Live source list

The host fires `ISidechainRegistry.SourcesChanged` when the user adds / renames / deletes a bus in Settings → Buses. Source-picker UIs should re-read `GetSources()` on the event and marshal to their dispatcher before mutating bindings:

```csharp
_context.Sidechain.SourcesChanged += (_, _) =>
    Dispatcher.UIThread.Post(() => RebuildSourceDropdown());
```

Source-reference stability: `SidechainRegistry` reuses the existing `ISidechainSource` instance when the underlying bus id is unchanged across refreshes (a rename doesn't invalidate subscriptions). The `DisplayName` mutates in place; re-read it inside the event handler.

### Threading rules

The subscriber callback runs ON THE AUDIO THREAD. Same rules as `ISampleProvider.Read`:

- No allocations on the hot path. The host already allocates a fresh per-subscriber copy of the buffer for you; don't pile on.
- No UI access, no blocking I/O. If you need state to flow to a UI control, write to a `Volatile.Int32` bit-pattern of a float and let the UI's refresh tick read it.
- Don't retain the buffer reference past the callback's stack frame. It's a fresh array, but reusing it across calls breaks the host's fan-out contract (each call gets a new copy because the host has no idea how long you'll hold it).
- Don't throw. The host's `BusMixer` catches and logs, but other subscribers still pay the latency.

### Attachment points

A sidechain-aware plugin typically advertises `SamplerAttachmentPoints.Master | SamplerAttachmentPoints.Bus`. Per-shortcut / per-preset / per-playlist makes no sense — those are leaf playbacks where the carrier and trigger would be the same.

### Reference

`gmsb-sampler-ducking` (v1.1+) is the reference sidechain plugin. See its `DuckingPlugin.cs` for the full envelope + smoother + UI source-picker pattern.

## 5. Building & packaging

Publish your plugin:

```powershell
dotnet publish -c Release -o publish
```

This produces your DLL plus any dependencies (NAudio.Core, NLayer, etc.),
a `.deps.json` the host's `AssemblyDependencyResolver` uses to wire
transitive refs at load time, and — if you wired `plugin.json` via the
`<None Update="plugin.json">` snippet above — the manifest at the
publish root.

Zip the publish output for distribution:

```powershell
Compress-Archive -Path publish/* -DestinationPath Acme.SunsetTheme.zip
```

The zip can be flat (files at the root) or wrapped in one top-level
folder — the installer normalises both. What matters is that
`plugin.json` is **at the same level as your entry DLL** inside the zip.

### Automated packaging

Each sibling plugin repo ships its own `scripts/package.ps1` plus
`build.yml` + `release.yml` workflows. Run the script locally to
produce an installable zip from your `src/` folder; push a `v*` tag to
cut a GitHub Release with the zip attached. See any of the
`gmsb-*` repos for working examples — the layout is identical across
all of them.

```powershell
pwsh scripts/package.ps1
# → dist/<your-plugin-id>-<version>.zip
```

The packaging script:

- Runs `dotnet publish` to a staging folder.
- Reads `plugin.json` to derive the zip name + wrapper-folder name.
- Strips `.pdb` files (debug symbols).
- Compresses into the wrapper-folder shape the installer expects.
- Sanity-checks the produced zip before declaring success.

**Third-party plugin authors** can copy any sibling repo's
`scripts/package.ps1` and `.github/workflows/release.yml` verbatim.
Both are plugin-agnostic — the script reads everything it needs from
your plugin's `plugin.json`.

## 6. Installing

Two paths, both work on every OS:

### a. Drop the zip on the Plugin Manager (recommended)

Open Settings, scroll to the Plugin Manager section, and drag your
`.zip` onto the drop zone (or click Browse). Multiple zips can be
dropped at once. The installer reads each manifest, places the folder
under `Plugins\` or `Themes\` based on `isTheme`, and:

- **Themes** are hot-loaded immediately — your palettes appear under
  Settings → Appearance → Theme without a restart, and the user can
  pick them right away.
- **Other plugins** show up as "pending activation"; the host wires
  them into the audio chain / codec registry at the next launch, so a
  Restart-Required banner appears. Click Restart Now (or relaunch
  manually) and toggle the plugin on under Settings → Plugins.

A zip with a broken or missing `plugin.json` is rejected with a clear
error message; the install does not move any files.

### b. Manual copy

Extract your zip into the relevant host folder:

| Plugin kind | Install path (Windows) |
|---|---|
| Theme | `%LocalAppData%\GameMasterSoundBoard\Themes\<your-id>\` |
| Codec / sampler / UI extension | `%LocalAppData%\GameMasterSoundBoard\Plugins\<your-id>\` |

On macOS the base is `~/Library/Application Support/GameMasterSoundBoard/`;
on Linux it's `~/.local/share/GameMasterSoundBoard/`. Both folders are
auto-created on first launch and each contains a `README.txt` describing
what goes in it.

The folder must contain `plugin.json` plus your entry DLL plus any
dependencies. The runtime scanner reads `entryDll` from the manifest;
sibling DLLs (NLayer, NAudio.Core, your transitive deps) are pulled in
on demand by `AssemblyDependencyResolver` — they are NOT probed as
candidate plugins.

After a manual install: themes are picked up on the **next** app launch
(only the zip-drop path hot-loads them); codecs/samplers/UI plugins
need a restart and an explicit toggle under Settings → Plugins.

## 7. Failure handling

If your plugin throws on load, the host **auto-disables** it (or, for
themes, reverts the selection to Default) before the next launch so a
broken DLL cannot cycle-crash the app. The settings UI keeps a row
showing your plugin with status **Failed** and a tooltip carrying the
exception message. Fix the bug, drop the new DLL in, and toggle it back
on.

## 8. Licensing

The host is GPL-3.0-only, but there's a written exception (see
[`LICENSE-EXCEPTION`](../LICENSE-EXCEPTION)) covering plugins and themes:
as long as your code interacts with the host **only** via the five
extension interfaces in `SoundBoard.PluginApi` — `IAudioCodecPlugin`,
`IAudioSamplerPlugin`, `IThemePlugin`, `IUIExtensionPlugin`, or
`IAudioBridgePlugin` — your plugin can be licensed however you want.

The exception also covers the supporting types in the same assembly that
exist to make those interfaces usable: `IPlugin`, `IPluginContext`,
`IWindowService`, `UIPlacement`, and (for bridges) `IBridgeHost`,
`IInboundAudioSink`, `BridgeStatus`, `IAudioFrameEncoder`,
`IAudioFrameDecoder`, plus the `IAudioCodecRegistry` exposed via
`IPluginContext` for codec dispatch and codec-borrowing, and
`ISidechainRegistry` / `ISidechainSource` for cross-bus sidechain
subscriptions.

Reaching into host internals via reflection, runtime patching, or any
other backdoor voids the exception. The `SoundBoard.PluginApi` assembly
is intentionally narrow so the exception perimeter is unambiguous —
including third-party code distributed as a bridge plugin (Discord.Net,
Zoom RTMS SDK, etc.) under its own license without conflicting with the
host's GPL.
