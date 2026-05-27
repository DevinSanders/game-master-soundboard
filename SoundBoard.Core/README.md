# SoundBoard.Core

The engine layer. UI-framework-free, cross-platform.

This is the bottom of the dependency stack: **Desktop → UI → Core**. Core must never reference Avalonia or anything in `SoundBoard.UI`.

## What lives here

| Folder | Role |
|---|---|
| `Models/` | Plain POCO entities — `Track`, `Preset`, `Playlist`, `ShortcutButton`, `AppSettings`, etc. EF Core picks them up via convention. |
| `Data/` | `SoundBoardDbContext` (SQLite). The path is resolved lazily from `SettingsService` so users can switch libraries at runtime. |
| `Audio/` | The signal chain — built-in `SeekableWaveSampleProvider` (.wav only; every other codec is a plugin), `TrackSampleProvider` (volume/fade/loop/start-delay/range), `MasterMixer`, `BusMixer`, and the two platform backends (`NAudioWindowsBackend` on Windows, `OpenALBackend` on macOS/Linux). |
| `Plugins/` | Host-side plugin machinery: `PluginContext`, `PluginLoadContext` (collectible `AssemblyLoadContext`), `PluginManifestFile`. The contract interfaces (`IPlugin`, `IAudioCodecPlugin`, `IAudioSamplerPlugin`, `IAudioBridgePlugin`, `IThemePlugin`, `IUIExtensionPlugin`) live in `SoundBoard.PluginApi`, not here. |
| `Services/` | `PluginService`, `SettingsService`, `SamplerChainService` (FX-chain owner), `AudioBridgeHost` (per-bridge worker thread dispatcher for `IAudioBridgePlugin`), `SidechainRegistry`. |
| `Activation/` | `SoundboardUri` parser/builder and `UriActivationHandler` for the `gmsound://` scheme. |
| `Logging/` | Static `Log` (`Log.Info / Warn / Error / Debug` + `Log.Checkpoint` for synchronous startup markers). Output is suppressed unless the process was launched with `--debug`. |

## Things to know before touching code here

- **Schema migrations live in `Data/SchemaMigrations.cs`.** Versioned, append-only list of `(Baseline + N, "description", "ALTER TABLE …")` entries. Add a column to a `Model` here? Append a new migration entry — don't add raw `ExecuteSqlRaw` calls in `App.axaml.cs`.
- **48 kHz IEEE float stereo** through `MasterMixer`. Inputs that don't match must be wrapped in `WdlResamplingSampleProvider` / `MonoToStereoSampleProvider` before `AddMixerInput`.
- **`MasterMixer.Read`'s `buffer` parameter is not a real `float[]`** — it's `NAudio.WaveBuffer.FloatBuffer`, a union over a `byte[]`. Direct indexer access (`buffer[i] = x`) works; `Array.Copy`/`Buffer.BlockCopy`/LINQ does not. There's a comment in `MasterMixer.cs` with the details.
- **Don't add `using NAudio.Wasapi;` or `using NAudio.CoreAudioApi;` statically anywhere here.** Those packages are conditionally restored only on Windows; a static reference breaks the cross-platform build. `NAudioWindowsBackend` loads them via `Assembly.Load` + reflection.
- **Audio thread**: `MasterMixer.Read` and everything subscribed to `AudioDataAvailable` runs on the audio thread. If a consumer needs to touch UI, it has to marshal — but that's the UI project's problem; Core itself stays UI-free.

For the audio pipeline diagram and plugin architecture overview, see the inline comments in `Audio/MasterMixer.cs` and `Plugins/IPlugin.cs`.
