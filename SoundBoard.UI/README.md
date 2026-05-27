# SoundBoard.UI

The Avalonia 12 app. MVVM via `CommunityToolkit.Mvvm` source generators. Compiled bindings on by default.

Sits between `SoundBoard.Core` (the engine, no UI references) and `SoundBoard.Desktop` (the entry-point shell). Most actual feature work happens here.

## What lives here

| Folder | Role |
|---|---|
| `App.axaml.cs` | DI composition root. The single place where services and ViewModels are registered, settings are loaded, plugins are discovered, the database is bootstrapped (`EnsureCreated()` + `SchemaMigrations.Apply`), and the audio backend is initialized. Schema upgrades live in `SoundBoard.Core/Data/SchemaMigrations.cs` — never add raw SQL here. |
| `ViewModels/` | All VMs inherit `ViewModelBase` (`ObservableObject`). Use `[ObservableProperty]` and `[RelayCommand]` source generators. |
| `Views/` | One `.axaml` + code-behind per view. Every root needs `x:DataType` (compiled bindings silently no-op without it). |
| `Controls/` | Custom Avalonia controls: `AudioVisualizer`, `RangeSlider`, `StaticWaveformDisplay`, `RpgIcon`. |
| `Services/` | UI-side services — `AudioPlaybackEngine`, `WindowManagerService`, `FileService`, `LibraryTransferService`, `PathResolver`, etc. |
| `Converters/` | `IValueConverter`s referenced from XAML. |
| `Messages/` | `WeakReferenceMessenger` payload types for cross-VM communication. |
| `Themes/` | Resource dictionaries and the `RpgAwesome` font assets. |
| `Assets/` | Icons, images, anything bundled as `AvaloniaResource`. |

## Conventions

- **Single Avalonia `Window` per key**: open secondary windows through `IWindowManagerService.ShowWindow(viewModel, key, title, w, h)` — don't `new Window()` directly. The service dedupes by the string `key`, so calling twice with the same key activates the existing window (pop-outs use a key per page so multiple can coexist).
- **POCO model fields need observable shims**: plain `Track.Icon` / `Preset.Icon` / `Playlist.Icon` don't notify. The editor VMs expose `[ObservableProperty] TrackIcon` etc. that write through to the model and raise `PropertyChanged`. Follow that pattern for any new live-editable model field.
- **Audio-thread events must marshal**: anything subscribed to `MasterMixer.AudioDataAvailable`, `TrackSampleProvider.AudioDataAvailable`, or playback callbacks runs on the audio thread. Use `Dispatcher.UIThread.Post(...)` before touching Avalonia state.
- **Drag-drop**: typed `DataFormat<T>` instances live in `Services/DragFormats.cs`. `DragGuards.IsInteractiveChild` walks the logical tree to ignore drags that start on a slider/button/textbox so card-drag doesn't hijack control interactions.

## Running this project directly

```powershell
dotnet run --project SoundBoard.UI -- --debug
```

This entry point is for dev: it skips the single-instance mutex and the named-pipe URI listener (those live in `SoundBoard.Desktop`), and turns on `.WithDeveloperTools()` under `#if DEBUG` so you get the F12 Avalonia DevTools inspector. Useful when you want to spawn multiple debug copies side-by-side.

The user-facing entry point is `SoundBoard.Desktop`.

## Pointers

- Audio pipeline lives in `SoundBoard.Core/Audio/`. The NAudio `WaveBuffer` aliasing trap is described in `MasterMixer.cs` and worth reading once before touching anything that copies the post-mix buffer.
- Contributor conventions / project-specific footguns: [../CONTRIBUTING.md](../CONTRIBUTING.md).
