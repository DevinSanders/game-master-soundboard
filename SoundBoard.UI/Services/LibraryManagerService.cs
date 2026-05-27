using SoundBoard.Core;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SoundBoard.UI.Services;

/// <summary>
/// Manages the on-disk library folder at
/// <c>%LocalAppData%\GameMasterSoundBoard\Libraries\</c>.
///
/// We no longer surface a raw file picker for library Create/Open; instead the
/// UI talks to this service so libraries always live in a predictable place
/// the user can find, back up, and switch between by name.
/// </summary>
public sealed class LibraryManagerService
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public string LibrariesFolder { get; }

    public LibraryManagerService()
    {
        LibrariesFolder = AppPaths.LibrariesFolder;
        Directory.CreateDirectory(LibrariesFolder);
    }

    /// <summary>One row in the Open-Library list.</summary>
    public sealed record LibraryEntry(string Name, string Path);

    public IReadOnlyList<LibraryEntry> ListLibraries()
    {
        var rows = new List<LibraryEntry>();

        foreach (var file in Directory.EnumerateFiles(LibrariesFolder, "*.db"))
        {
            rows.Add(new LibraryEntry(
                Name: Path.GetFileNameWithoutExtension(file),
                Path: file));
        }

        return rows.OrderBy(r => r.Name).ToList();
    }

    /// <summary>Reserve a path for a new library. The file is NOT created here:
    /// EF Core's <c>EnsureCreated</c> skips schema seeding if the SQLite file
    /// already exists, so pre-touching a 0-byte file would result in an empty
    /// library with no tables. Instead we just hand back the target path; the
    /// next process launch with this path as the active library will create
    /// the file and the schema together.</summary>
    public string ReserveLibraryPath(string name)
    {
        var safe = SanitizeName(name);
        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException("Library name is empty or invalid.", nameof(name));

        var path = Path.Combine(LibrariesFolder, safe + ".db");
        if (File.Exists(path))
            throw new IOException($"A library named '{safe}' already exists.");

        Log.Info("Library", $"Reserved new library '{safe}' at {path} (will be created on next launch)");
        return path;
    }

    /// <summary>Sanitize a user-supplied name into a valid filename — strip or
    /// replace any characters the OS rejects. Returns an empty string if the
    /// result is unusable.</summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            sb.Append(Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c);
        }
        return sb.ToString().TrimEnd('.', ' ');
    }
}
