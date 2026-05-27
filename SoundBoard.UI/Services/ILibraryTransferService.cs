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
    /// is folded into the active library (name collisions on presets,
    /// playlists, and shortcut pages cause the existing record to be replaced).
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
    public List<Track> SuccessfullyImported { get; } = new();
    public List<Track> MissingFiles { get; } = new();
    public int PresetsImported { get; set; }
    public int ShortcutsImported { get; set; }
    public int PlaylistsImported { get; set; }
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
