using System.Collections.Generic;

namespace SoundBoard.UI.Services;

/// <summary>How a library import should be applied to the user's existing data.</summary>
public enum ImportMode
{
    /// <summary>Merge the export into the currently-open library.
    /// Presets, playlists, and shortcut pages whose names already exist are
    /// fully replaced (cascade-deleted then re-inserted); tracks are matched
    /// by resolved <see cref="SoundBoard.Core.Models.Track.FilePath"/> so the
    /// same file isn't duplicated.</summary>
    Merge,

    /// <summary>Create a brand-new empty library and import everything into
    /// it. The caller restarts the app so the new library becomes active.</summary>
    NewLibrary,
}

/// <summary>
/// Caller-supplied options for <see cref="ILibraryTransferService.ImportLibraryAsync"/>.
/// Choose merge vs new-library mode, name the new library (if applicable),
/// and supply roots to search when reconciling track file paths.
/// </summary>
public sealed class ImportOptions
{
    public ImportMode Mode { get; set; } = ImportMode.Merge;

    /// <summary>Required when <see cref="Mode"/> is <see cref="ImportMode.NewLibrary"/>.</summary>
    public string? NewLibraryName { get; set; }

    /// <summary>Root directories to recursively scan when resolving file
    /// paths in the export that no longer exist verbatim — handles "exported
    /// from Windows, imported on macOS" and "audio lives on Google Drive
    /// mounted at a different path" cases.</summary>
    public List<string> SearchPaths { get; set; } = new();
}
