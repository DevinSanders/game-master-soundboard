using System.Collections.Generic;

namespace SoundBoard.UI.Services;

/// <summary>How a library import should be applied to the user's existing data.</summary>
public enum ImportMode
{
    /// <summary>Merge the export into the currently-open library. Name collisions
    /// on tracks, presets, playlists, and shortcut pages are governed by
    /// <see cref="ImportOptions.DuplicateHandling"/>; tracks with a matching
    /// resolved <see cref="SoundBoard.Core.Models.Track.FilePath"/> are always
    /// collapsed because the same audio file can't appear twice in a library.</summary>
    Merge,

    /// <summary>Create a brand-new empty library and import everything into
    /// it. The caller restarts the app so the new library becomes active.</summary>
    NewLibrary,
}

/// <summary>How an entity in the import file should be reconciled when its name
/// collides with one already in the library, or with one earlier in the same
/// import. Applies to Tracks (by Name; FilePath collisions are intrinsic),
/// Presets, Playlists, and Shortcut Pages uniformly.</summary>
public enum DuplicatePolicy
{
    /// <summary>Keep the existing entity as-is and discard the imported
    /// duplicate. References from imported presets / playlists / pages that
    /// pointed at the discarded row are remapped to the kept row.</summary>
    Skip,

    /// <summary>Replace the existing entity with the imported one. For presets,
    /// playlists, and shortcut pages this cascades — the existing row and its
    /// child rows are deleted before re-insert. For tracks the existing row's
    /// user fields are overwritten in place from the import.</summary>
    Replace,

    /// <summary>Insert the imported entity as a new row, renaming it to avoid
    /// the collision per <see cref="ImportOptions.RenameStrategy"/>.</summary>
    AllowDuplicates,
}

/// <summary>How the importer should rename an entity when
/// <see cref="DuplicatePolicy.AllowDuplicates"/> is selected and a name
/// collision is detected.</summary>
public enum DuplicateRenameStrategy
{
    /// <summary>Append " (2)", " (3)", … until the name is unique
    /// (e.g. "Battle" → "Battle (2)" → "Battle (3)").</summary>
    NumericSuffix,

    /// <summary>Append " (copy)" on the first collision and " (copy 2)",
    /// " (copy 3)", … on subsequent ones (e.g. "Battle" → "Battle (copy)"
    /// → "Battle (copy 2)").</summary>
    CopySuffix,
}

/// <summary>
/// Caller-supplied options for <see cref="ILibraryTransferService.ImportLibraryAsync"/>.
/// Choose merge vs new-library mode, name the new library (if applicable),
/// supply roots to search when reconciling track file paths, and pick how
/// duplicate names should be handled.
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

    /// <summary>How name collisions are reconciled across tracks, presets,
    /// playlists, and shortcut pages. Defaults to <see cref="DuplicatePolicy.Skip"/>
    /// — the safest choice; nothing existing is mutated and nothing duplicate
    /// is inserted.</summary>
    public DuplicatePolicy DuplicateHandling { get; set; } = DuplicatePolicy.Skip;

    /// <summary>Suffix style applied when <see cref="DuplicateHandling"/> is
    /// <see cref="DuplicatePolicy.AllowDuplicates"/>. Ignored otherwise.</summary>
    public DuplicateRenameStrategy RenameStrategy { get; set; } = DuplicateRenameStrategy.NumericSuffix;
}
