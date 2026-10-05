using System;

namespace SoundBoard.UI.Services;

/// <summary>
/// Global, session-only "is the soundboard locked?" flag shared by the main
/// <see cref="ViewModels.ShortcutsViewModel"/> and every popped-out
/// <see cref="ViewModels.PoppedShortcutPageViewModel"/>. Locked disables
/// intra-window reorder (button grid + page tabs) so the board is safe to
/// tap during a session; unlocking is an explicit action.
///
/// <para>Deliberately NOT persisted — a fresh instance starts
/// <see cref="IsLocked"/> = true on every launch. Registered as a singleton
/// so all shortcut views observe one state.</para>
/// </summary>
public interface ISoundboardLockService
{
    bool IsLocked { get; set; }

    /// <summary>Raised whenever <see cref="IsLocked"/> changes, from any view.</summary>
    event Action? Changed;
}

/// <inheritdoc />
public sealed class SoundboardLockService : ISoundboardLockService
{
    private bool _isLocked = true; // boards default to locked each launch

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
