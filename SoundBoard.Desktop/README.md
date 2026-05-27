# SoundBoard.Desktop

The user-facing entry point — a thin `WinExe` shell around `SoundBoard.UI`.

This project is small on purpose. It does four things that `SoundBoard.UI` running standalone doesn't:

1. **Single-instance enforcement.** A named `Mutex` (`Global\GMSoundBoard_SingleInstance_Mutex`) ensures only one process runs at a time. Without this, every `gmsound://` link click would spawn a fresh app instead of routing to the running one.
2. **URI activation forwarding.** Second-instance launches connect via a named pipe (`GMSoundBoardInstancePipe`) and forward `gmsound://...` args to the already-running instance, which dispatches them through `UriActivationHandler`. This is what makes the URI scheme actually work.
3. **Process-wide crash handlers.** `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are wired up before Avalonia starts, so exceptions that escape Avalonia's own handlers still hit the log file.
4. **`--debug` flag + WinExe packaging.** Initializes the static `Log` system from CLI args before any other code runs, and `<OutputType>WinExe</OutputType>` suppresses the console window that would otherwise flash on launch.

## Why this exists as its own project

`SoundBoard.UI` is also `WinExe` and has its own `Program.cs` (Avalonia template default — the visual designer needs `BuildAvaloniaApp()` to find), but the UI entry point intentionally **skips** the single-instance lock and the URI listener so multiple debug copies can run side-by-side with DevTools enabled. The Desktop project is the production entry point that puts those pieces back.

## Running this project

```powershell
dotnet run --project SoundBoard.Desktop
dotnet run --project SoundBoard.Desktop -- --debug
```

For dev work, prefer `dotnet run --project SoundBoard.UI -- --debug` (no single-instance lock, F12 DevTools enabled).

## Adding to this project

If you find yourself adding business logic here, it probably belongs in `SoundBoard.UI` or `SoundBoard.Core` instead. This project is supposed to stay thin — single-instance, URI forwarding, crash handlers, and the app icon. Anything else risks fragmenting the composition root.
