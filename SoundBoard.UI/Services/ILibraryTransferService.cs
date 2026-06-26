using SoundBoard.Core.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Exports the active library to a portable bundle file, and imports such
/// bundles back into either the current library (merge) or a brand new one.
/// Handles path reconciliation when imported tracks reference audio files
/// that have moved on the destination machine.
/// </summary>
public interface ILibraryTransferService
{
    Task ExportLibraryAsync(string filePath);

    /// <summary>Import the contents of <paramref name="filePath"/> according to
    /// <paramref name="options"/>. For <see cref="ImportMode.Merge"/> the data
    /// is folded into the active library; name collisions on tracks, presets,
    /// playlists, and shortcut pages are reconciled per
    /// <see cref="ImportOptions.DuplicateHandling"/>. Track rows whose resolved
    /// <see cref="SoundBoard.Core.Models.Track.FilePath"/> matches an existing
    /// row are always collapsed regardless of the duplicate policy — a single
    /// audio file maps to a single library entry.
    /// For <see cref="ImportMode.NewLibrary"/> a fresh SQLite DB is created at
    /// the path returned in <see cref="ImportResult.CreatedLibraryPath"/>
    /// and the caller is expected to switch to it (typically by restarting).</summary>
    Task<ImportResult> ImportLibraryAsync(string filePath, ImportOptions options);
}

/// <summary>
/// Outcome of a library import — counts per entity type plus the list of
/// tracks whose audio files weren't found on the destination machine, so
/// the UI can surface them for manual reconciliation.
/// </summary>
public class ImportResult
{
    /// <summary>Tracks that landed in the library (either freshly inserted, or
    /// the existing rows they were matched against under
    /// <see cref="DuplicatePolicy.Skip"/> / <see cref="DuplicatePolicy.Replace"/>).</summary>
    public List<Track> SuccessfullyImported { get; } = new();

    /// <summary>Tracks whose <see cref="Track.FilePath"/> could not be resolved
    /// on the destination machine — neither at the verbatim path nor under any
    /// of the configured <see cref="ImportOptions.SearchPaths"/>.</summary>
    public List<Track> MissingFiles { get; } = new();

    public int PresetsImported { get; set; }
    public int ShortcutsImported { get; set; }
    public int PlaylistsImported { get; set; }

    // Per-entity duplicate counters, populated by ImportMergeAsync when a name
    // collision triggers the chosen DuplicatePolicy. "Imported" counts above
    // do NOT include skipped duplicates; renamed inserts DO count as imported.
    public int TracksSkipped { get; set; }
    public int TracksReplaced { get; set; }
    public int TracksRenamed { get; set; }

    public int PresetsSkipped { get; set; }
    public int PresetsReplaced { get; set; }
    public int PresetsRenamed { get; set; }

    public int PlaylistsSkipped { get; set; }
    public int PlaylistsReplaced { get; set; }
    public int PlaylistsRenamed { get; set; }

    public int ShortcutsSkipped { get; set; }
    public int ShortcutsReplaced { get; set; }
    public int ShortcutsRenamed { get; set; }

    /// <summary>For <see cref="ImportMode.NewLibrary"/>: the path of the freshly
    /// created library file. Null for merge imports.</summary>
    public string? CreatedLibraryPath { get; set; }
}

/// <summary>
/// Exports / imports <see cref="Models.AppSettings"/> as JSON. Companion to
/// the library transfer service for migrating an entire app installation
/// between machines.
/// </summary>
public interface ISettingsTransferService
{
    Task ExportSettingsAsync(string filePath);
    Task<bool> ImportSettingsAsync(string filePath);
}
