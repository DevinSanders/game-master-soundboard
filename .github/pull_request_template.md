## Summary

<!-- One or two sentences on what this change does and why. -->

## Related issue

<!-- e.g. Closes #123, Refs #456. Skip if not applicable. -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor / cleanup
- [ ] Documentation
- [ ] Build / CI / tooling

## How I tested this

<!-- What you actually did to verify the change. If UI-affecting, mention the platform(s) you ran it on. -->

## Project-specific checklist

<!-- These are the easy-to-miss conventions for this codebase. See CONTRIBUTING.md for the full list. -->

- [ ] `dotnet build SoundBoard.slnx` succeeds locally
- [ ] `dotnet test SoundBoard.Tests/SoundBoard.Tests.csproj` passes locally
- [ ] If I added or changed a column/table on a `Model`, I appended a migration entry to `SoundBoard.Core/Data/SchemaMigrations.cs` (never raw SQL in `App.axaml.cs`)
- [ ] No new static `using NAudio.Wasapi;` or `using NAudio.CoreAudioApi;` in `SoundBoard.Core`
- [ ] If I added a live-editable model field, the editor VM exposes an `[ObservableProperty]` shim so compiled bindings actually refresh
- [ ] Any `.axaml` I added has `x:DataType` on the root
- [ ] Code subscribed to `AudioDataAvailable` (or anything on the audio thread) marshals through `Dispatcher.UIThread` before touching Avalonia state
