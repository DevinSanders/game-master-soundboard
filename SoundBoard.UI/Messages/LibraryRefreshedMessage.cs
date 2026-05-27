namespace SoundBoard.UI.Messages;

/// <summary>
/// Broadcast after a bulk library mutation (currently: a merge-mode import).
/// Open view-models that read directly from the database should reload their
/// data on receipt so the user sees the changes without restarting.
/// </summary>
public sealed class LibraryRefreshedMessage
{
}
