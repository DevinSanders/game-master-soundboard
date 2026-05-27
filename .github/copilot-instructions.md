# Copilot instructions — Game Master Sound Board

A cross-platform Avalonia desktop soundboard for tabletop RPGs. .NET 10, four-project layered solution. Deeper architecture notes live in the per-project `README.md` files and `docs/`; the points below are the ones that affect day-to-day code suggestions.

## Project layout

- `SoundBoard.PluginApi` — the public plugin/theme SDK (NuGet-packable, MIT-licensed). The only assembly third-party plugins reference: `IPlugin` + the marker interfaces (`IAudioCodecPlugin`, `IAudioSamplerPlugin`, `IAudioBridgePlugin`, `IThemePlugin`, `IUIExtensionPlugin`). Depends on `NAudio.Core` only — no Avalonia, no EF.
- `SoundBoard.Core` — engine. SQLite + EF Core, audio pipeline, host-side plugin machinery. **No UI framework references.** Don't add `using Avalonia.*` or any UI types here.
- `SoundBoard.UI` — Avalonia 12 app with MVVM. Composition root is `App.axaml.cs`.
- `SoundBoard.Desktop` — thin `WinExe` shell with single-instance enforcement and `gmsound://` URI activation forwarding.

Dependency flow is strictly **Desktop → UI → Core → PluginApi**.

## MVVM conventions

- ViewModels inherit `ViewModelBase` (which is `ObservableObject` from `CommunityToolkit.Mvvm`). Use `[ObservableProperty]` and `[RelayCommand]` source generators — don't hand-roll `INotifyPropertyChanged`.
- Compiled bindings are on by default. Every `.axaml` root needs `x:DataType="vm:SomeViewModel"`. Avalonia will silently no-op a binding without it.
- Open secondary windows through `IWindowManagerService.ShowWindow(viewModel, key, title, w, h)` — windows are deduped by the string `key`, not by VM type. Don't `new Window()` from a VM.
- Cross-VM signalling uses `CommunityToolkit.Mvvm.Messaging` (`WeakReferenceMessenger`). See `Messages/`.

## POCO model shim pattern

Plain model properties on `Track`, `Preset`, `Playlist`, etc. **don't notify** when mutated, so compiled bindings against them won't refresh the UI at runtime. Editor VMs expose `[ObservableProperty]` shim properties (`TrackIcon`, `PresetIcon`, `PlaylistIcon`) that write through to the model and raise `PropertyChanged`. Follow this pattern for any new live-editable model field.

## Audio pipeline

The signal chain runs through `MasterMixer` at 48 kHz IEEE float stereo. Inputs that don't match must be resampled / channel-converted **before** `AddMixerInput` — see `AudioPlaybackEngine.PlayTrack` for the canonical pattern.

**NAudio `WaveBuffer` aliasing trap.** In `MasterMixer.Read(float[] buffer, ...)`, `buffer` is *not* a real `float[]` — it's `WaveBuffer.FloatBuffer`, an explicit-layout union over a `byte[]`. Direct indexer access (`buffer[i] = x`) works because it's JIT-compiled against the static `float[]` type. But anything that inspects the runtime element type **sees `byte`** and gets the wrong size:

- ❌ `Array.Copy(buffer, ...)` — copies 1 byte per slot
- ❌ `Buffer.BlockCopy` — same problem
- ❌ LINQ over the array
- ❌ Generic helpers that cache `typeof(T)`
- ✅ `buffer[i] = float`, `buffer[i] += float`

If you need to broadcast or copy the post-mix audio (visualizers, recording, the per-bridge broadcast fan-out consumed by `IAudioBridgePlugin`s), copy from the internal `tempBuffer = new float[count]` instead — that one really is a `float[]`. Per-track providers don't have this problem because `MixingSampleProvider` allocates its own real `float[]` per source.

Anything subscribed to `AudioDataAvailable` runs on the audio thread — marshal to UI thread via `Dispatcher.UIThread` before touching Avalonia state.

## Data layer

SQLite via `Microsoft.EntityFrameworkCore.Sqlite`. Schema upgrades live in `SoundBoard.Core/Data/SchemaMigrations.cs` as a versioned, append-only list: `(Baseline + N, "description", "ALTER TABLE …")`. When you add a column or table to a `Model`, append a new migration entry — **never** add raw `ExecuteSqlRaw` calls in `App.axaml.cs`. Fresh installs get the column via `EnsureCreated()`; the migration's `try/catch` records the version even when the column already exists.

## Platform notes

`NAudio.Wasapi` and `NAudio.WinMM` are conditionally restored only on Windows. **Never** add a static `using NAudio.Wasapi;` or `using NAudio.CoreAudioApi;` in Core — those types only exist at runtime on Windows, and a static reference breaks the cross-platform build. The Windows backend (`NAudioWindowsBackend`) loads them via `Assembly.Load("NAudio.Wasapi")` + reflection.

macOS/Linux audio goes through `OpenALBackend`, which uses OpenAL Soft via the `Silk.NET.OpenAL` managed bindings + `Silk.NET.OpenAL.Soft.Native` per-RID natives. The Core project intentionally has no `#if WINDOWS` blocks — platform dispatch happens at `LocalAudioPlayer.CreateBackendForPlatform()`. OpenAL Soft auto-selects its real backend (CoreAudio on Mac; PipeWire / PulseAudio / ALSA / JACK on Linux) at runtime.

## Logging

`SoundBoard.Core.Logging.Log` is the static logger. Prefer `Log.Info / Warn / Error / Debug(category, message)` over `Console.WriteLine`. Log output is suppressed by default and enabled by launching with `--debug`.
