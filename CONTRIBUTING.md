# Contributing to Game Master Sound Board

Thanks for your interest in contributing. This guide covers how to get a dev environment running and what to expect when you open a PR. The README, the per-project READMEs in `SoundBoard.Core/`, `SoundBoard.UI/`, and `SoundBoard.Desktop/`, and the inline comments on the trickier subsystems (audio pipeline, plugin loader, audio bridges) cover the architecture.

## Quick start

Prerequisites: **.NET 10 SDK**. No other tooling required.

```powershell
# Replace <your-username> with your GitHub username after forking the repo on github.com.
git clone https://github.com/<your-username>/game-master-soundboard.git
cd "game-master-soundboard"

# Add the upstream remote so you can pull in new changes:
git remote add upstream https://github.com/DevinSanders/game-master-soundboard.git

# Restore + build the whole solution
dotnet build SoundBoard.slnx

# Run the app (full entry — single-instance, URI activation, etc.)
dotnet run --project SoundBoard.Desktop

# Or run the UI project directly for dev — no single-instance lock, DevTools enabled
dotnet run --project SoundBoard.UI -- --debug
```

Tests live in `SoundBoard.Tests/` (xunit v3 + NSubstitute + FluentAssertions + Avalonia.Headless). Run them with `dotnet test SoundBoard.Tests/SoundBoard.Tests.csproj`. The `scratch/` folder is for throwaway experimentation and isn't part of the solution.

### Sandboxed dev data

Both projects' `launchSettings.json` set the `GMSOUNDBOARD_APPDATA` environment variable to `./.debug-appdata`. `AppPaths` resolves that against the binary folder, so a dev run lands its data in `SoundBoard.Desktop/bin/Debug/net10.0/.debug-appdata/` (or the equivalent for `SoundBoard.UI`) instead of `%LocalAppData%\GameMasterSoundBoard\`.

The effect:
- Plugin installs, library swaps, theme changes, broken settings — all isolated to the sandbox. Your installed copy's data is never touched.
- Each branch / clone / contributor gets their own state via their own `bin/` folder. No cross-pollination.
- `dotnet clean` (or deleting `bin/`) wipes the dev sandbox; the next build starts fresh.
- The sandbox sits under `bin/`, which `.gitignore` already excludes, so it never reaches commits.

To force a dev run to use your installed copy's data instead — useful for repro work — clear the env var:

```powershell
# PowerShell
$env:GMSOUNDBOARD_APPDATA = ""; dotnet run --project SoundBoard.Desktop
```

```bash
# bash / zsh
GMSOUNDBOARD_APPDATA= dotnet run --project SoundBoard.Desktop
```

## Where to start

- Browse open issues; anything labelled `good first issue` or `help wanted` is a good entry point.
- If you have an idea that isn't already filed, **open an issue first** so we can sanity-check the approach before you sink time into it. For bugs, please use the bug-report template and attach a debug log (see below).

## Branching & PRs

- Branch off `main`.
- Keep PRs focused. A bug fix doesn't need surrounding cleanup; if you spot a separate issue while you're in there, file it separately.
- Reference the issue you're fixing (`Closes #123`) in the PR description.
- Fill out the PR template — especially the project-specific checklist. Those items cover the easy-to-miss conventions below.
- CI builds on Windows, Linux, and macOS. All three need to pass before merge.

## Code conventions

These are the points that aren't obvious from reading the code:

- **MVVM**: use `[ObservableProperty]` and `[RelayCommand]` source generators from `CommunityToolkit.Mvvm`. Don't hand-roll `INotifyPropertyChanged`.
- **Compiled bindings**: every `.axaml` root needs `x:DataType`. Avalonia silently no-ops a binding without it.
- **Models are POCO** — properties don't notify by themselves. If you add a property that needs to update the UI at runtime, expose an `[ObservableProperty]` shim on the relevant ViewModel that writes through to the model. See `TrackIcon` / `PresetIcon` / `PlaylistIcon` for the canonical pattern.
- **Schema migrations.** Schema upgrades live in `SoundBoard.Core/Data/SchemaMigrations.cs` as a versioned, append-only list: `(Baseline + N, "description", "ALTER TABLE …")`. When you add a column or table to a `Model`, append a new migration entry — never add raw `ExecuteSqlRaw` calls in `App.axaml.cs`. Fresh installs get the column via `EnsureCreated()`; the migration's `try/catch` swallows the resulting "duplicate column"/"table already exists" so the version is still recorded.
- **Core stays UI-free.** No `using Avalonia.*` anywhere in `SoundBoard.Core`. And **never** add static `using NAudio.Wasapi;` or `using NAudio.CoreAudioApi;` — those types are Windows-only at runtime and a static reference breaks the cross-platform build. The Windows backend loads them via `Assembly.Load` + reflection.
- **Audio thread**: anything subscribed to `MasterMixer.AudioDataAvailable`, `TrackSampleProvider.AudioDataAvailable`, or any other audio-thread event runs on the audio thread. Marshal to UI via `Dispatcher.UIThread.Post(...)` before touching Avalonia state.
- **`MasterMixer.Read`'s buffer is not a real `float[]`.** It's an NAudio `WaveBuffer.FloatBuffer` aliased over a `byte[]`. Direct indexer access works; `Array.Copy`/`Buffer.BlockCopy`/LINQ over it does not. If you need a copy of the post-mix audio, copy from the internal `tempBuffer` — there's a comment in `MasterMixer.cs` with the details.

## Reporting bugs

The single most useful thing you can attach is a debug log. To generate one:

1. Launch with `--debug`.
2. Reproduce the issue.
3. Exit cleanly.
4. Grab the most recent log file from:
   - **Windows:** `%LocalAppData%\GameMasterSoundBoard\logs\`
   - **macOS:** `~/Library/Application Support/GameMasterSoundBoard/logs/`
   - **Linux:** `~/.local/share/GameMasterSoundBoard/logs/`

Use the bug-report template and drop the log in. If the app crashed, the same folder will also have a `crash-*.log` worth attaching.

## Releases

Releases are driven from GitHub Actions ([.github/workflows/release.yml](.github/workflows/release.yml)):

- **Stable**: push a tag like `v1.2.3` → builds installers and archives for all platforms and attaches them to a new GitHub Release.
- **Pre-release**: push a tag like `v1.2.3-beta.1` or `v1.2.3-rc.1` → same flow, marked as pre-release.
- **Unscheduled beta**: trigger the **Release** workflow manually from the Actions tab and supply the tag (defaults to pre-release).

Each release produces:

- **Windows**: Inno Setup installer (`.exe`), Chocolatey package (`.nupkg`), portable `.zip`.
- **macOS**: drag-to-Applications `.dmg` (arm64 + x64), `.app` bundle inside a `.zip`.
- **Linux**: `.AppImage` (portable), `.deb`, `.rpm`, portable `.tar.gz`.

Build scripts and installer templates live in [packaging/](packaging/) — see [packaging/README.md](packaging/README.md) for the layout and how to test each builder locally.

## License

This project is licensed under **GPL v3** (see [LICENSE](LICENSE)) with the additional exception in [LICENSE-EXCEPTION](LICENSE-EXCEPTION). By submitting a contribution you agree that your contribution will be released under the same terms.
