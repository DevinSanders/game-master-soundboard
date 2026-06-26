using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.Tests.Fakes;
using SoundBoard.UI.Services;
using System.Text.Json;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Pins the library-import duplicate-handling contract: the
/// <see cref="DuplicatePolicy"/> chosen by the user is honored uniformly
/// across tracks, presets, playlists, and shortcut pages — for both
/// existing-DB collisions and within-file collisions — and the per-entity
/// counters on <see cref="ImportResult"/> reflect what actually happened.
///
/// <para>Tests use a real <see cref="SqliteInMemoryDbFixture"/> and a real
/// temp directory of empty .wav files so <see cref="PathResolver"/> resolves
/// every imported track path. Each test owns its temp directory and disposes
/// the fixture so DB state never leaks between tests.</para>
/// </summary>
public sealed class LibraryTransferServiceImportTests : IDisposable
{
    private readonly SqliteInMemoryDbFixture _fx = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "gmsb-import-test-" + Guid.NewGuid().ToString("N").Substring(0, 12));
    private readonly LibraryManagerService _libraryManager = new();

    public LibraryTransferServiceImportTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Tracks: FilePath collision always collapses ────────────────────────

    [Fact]
    public async Task Tracks_FilePathCollision_CollapsesRegardlessOfPolicy()
    {
        // Existing row: same FilePath as the import's "Old Name" track.
        var sharedPath = MakeAudioFile("shared.wav");
        using (var seed = _fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Name = "Existing Name", FilePath = sharedPath });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new { Id = 1, Name = "Old Name", FilePath = sharedPath } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        // AllowDuplicates would normally rename a name-colliding track, but
        // here the FilePath match takes precedence — same audio file, same
        // row, no second insert.
        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.AllowDuplicates,
        });

        using var read = _fx.CreateContext();
        read.Tracks.Count().Should().Be(1);
        read.Tracks.Single().Name.Should().Be("Existing Name"); // existing fields preserved
        result.TracksRenamed.Should().Be(0);
        result.SuccessfullyImported.Single().Id.Should().Be(read.Tracks.Single().Id);
    }

    // ── Tracks: name collision under each policy ───────────────────────────

    [Fact]
    public async Task Tracks_Skip_NameCollision_MapsImportedIdToExistingRow()
    {
        var existingPath = MakeAudioFile("existing.wav");
        var importedPath = MakeAudioFile("imported.wav");
        using (var seed = _fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Name = "Battle", FilePath = existingPath });
            seed.SaveChanges();
        }

        // The imported preset references the imported track (Id=1) — under
        // Skip, that reference must remap to the existing same-named track.
        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new { Id = 1, Name = "Battle", FilePath = importedPath } },
            Presets = new[]
            {
                new { Id = 10, Name = "Combat Preset",
                      Tracks = new[] { new { TrackId = 1, Order = 0 } } }
            },
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Skip,
        });

        using var read = _fx.CreateContext();
        read.Tracks.Count().Should().Be(1);
        result.TracksSkipped.Should().Be(1);

        var preset = read.Presets.Include(p => p.Tracks).Single();
        preset.Tracks.Single().TrackId.Should().Be(read.Tracks.Single().Id);
    }

    [Fact]
    public async Task Tracks_Replace_NameCollision_OverwritesExistingFields()
    {
        var existingPath = MakeAudioFile("existing.wav");
        var importedPath = MakeAudioFile("imported.wav");
        using (var seed = _fx.CreateContext())
        {
            seed.Tracks.Add(new Track
            {
                Name = "Battle", FilePath = existingPath, Volume = 0.5f, Icon = "old-icon",
            });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new
            {
                Id = 1, Name = "Battle", FilePath = importedPath,
                Volume = 1.5f, Icon = "new-icon",
            } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Replace,
        });

        using var read = _fx.CreateContext();
        read.Tracks.Count().Should().Be(1);
        var row = read.Tracks.Single();
        row.FilePath.Should().Be(importedPath);
        row.Volume.Should().BeApproximately(1.5f, 0.001f);
        row.Icon.Should().Be("new-icon");
        result.TracksReplaced.Should().Be(1);
    }

    [Theory]
    [InlineData(DuplicateRenameStrategy.NumericSuffix, "Battle (2)")]
    [InlineData(DuplicateRenameStrategy.CopySuffix,    "Battle (copy)")]
    public async Task Tracks_AllowDuplicates_NameCollision_RenamesPerStrategy(
        DuplicateRenameStrategy strategy, string expectedName)
    {
        var existingPath = MakeAudioFile("existing.wav");
        var importedPath = MakeAudioFile("imported.wav");
        using (var seed = _fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Name = "Battle", FilePath = existingPath });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new { Id = 1, Name = "Battle", FilePath = importedPath } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.AllowDuplicates,
            RenameStrategy = strategy,
        });

        using var read = _fx.CreateContext();
        read.Tracks.Count().Should().Be(2);
        read.Tracks.Select(t => t.Name).Should().Contain(new[] { "Battle", expectedName });
        result.TracksRenamed.Should().Be(1);
    }

    // ── Within-file duplicates (Tracks) ────────────────────────────────────

    [Fact]
    public async Task Tracks_WithinFileDuplicate_Skip_OnlyFirstInserts()
    {
        var pathA = MakeAudioFile("a.wav");
        var pathB = MakeAudioFile("b.wav");

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[]
            {
                new { Id = 1, Name = "Sword Clash", FilePath = pathA },
                new { Id = 2, Name = "Sword Clash", FilePath = pathB },
            },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Skip,
        });

        using var read = _fx.CreateContext();
        read.Tracks.Count().Should().Be(1);
        read.Tracks.Single().FilePath.Should().Be(pathA); // first one wins
        result.TracksSkipped.Should().Be(1);
    }

    [Fact]
    public async Task Tracks_WithinFileDuplicate_AllowDuplicates_RenamesSecondNumeric()
    {
        var pathA = MakeAudioFile("a.wav");
        var pathB = MakeAudioFile("b.wav");
        var pathC = MakeAudioFile("c.wav");

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[]
            {
                new { Id = 1, Name = "Sword Clash", FilePath = pathA },
                new { Id = 2, Name = "Sword Clash", FilePath = pathB },
                new { Id = 3, Name = "Sword Clash", FilePath = pathC },
            },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.AllowDuplicates,
            RenameStrategy = DuplicateRenameStrategy.NumericSuffix,
        });

        using var read = _fx.CreateContext();
        var names = read.Tracks.Select(t => t.Name).OrderBy(n => n).ToList();
        names.Should().BeEquivalentTo(new[] { "Sword Clash", "Sword Clash (2)", "Sword Clash (3)" });
        result.TracksRenamed.Should().Be(2);
    }

    // ── Presets ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Presets_Skip_DoesNotReplaceExisting()
    {
        var path = MakeAudioFile("loop.wav");
        using (var seed = _fx.CreateContext())
        {
            var t = new Track { Name = "Loop", FilePath = path };
            seed.Tracks.Add(t);
            seed.SaveChanges();
            seed.Presets.Add(new Preset { Name = "Combat", Icon = "old-icon" });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = Array.Empty<object>(),
            Presets = new[]
            {
                new { Id = 10, Name = "Combat", Icon = "new-icon", Tracks = Array.Empty<object>() }
            },
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Skip,
        });

        using var read = _fx.CreateContext();
        read.Presets.Count().Should().Be(1);
        read.Presets.Single().Icon.Should().Be("old-icon"); // existing kept as-is
        result.PresetsSkipped.Should().Be(1);
        result.PresetsImported.Should().Be(0);
    }

    [Fact]
    public async Task Presets_Replace_CascadeDeletesAndReinserts()
    {
        var path = MakeAudioFile("loop.wav");
        int oldPresetId;
        using (var seed = _fx.CreateContext())
        {
            var t = new Track { Name = "Loop", FilePath = path };
            seed.Tracks.Add(t);
            seed.SaveChanges();
            var p = new Preset { Name = "Combat", Icon = "old-icon" };
            seed.Presets.Add(p);
            seed.SaveChanges();
            seed.PresetTracks.Add(new PresetTrack { PresetId = p.Id, TrackId = t.Id, Order = 0 });
            seed.SaveChanges();
            oldPresetId = p.Id;
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = Array.Empty<object>(),
            Presets = new[]
            {
                new { Id = 10, Name = "Combat", Icon = "new-icon", Tracks = Array.Empty<object>() }
            },
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Replace,
        });

        using var read = _fx.CreateContext();
        read.Presets.Count().Should().Be(1);
        var preset = read.Presets.Include(p => p.Tracks).Single();
        preset.Id.Should().NotBe(oldPresetId);            // cascade-deleted + re-inserted
        preset.Icon.Should().Be("new-icon");
        preset.Tracks.Should().BeEmpty();                 // import had no tracks
        read.PresetTracks.Count().Should().Be(0);         // old PresetTrack cascade-removed
        result.PresetsReplaced.Should().Be(1);
        result.PresetsImported.Should().Be(1);
    }

    [Fact]
    public async Task Presets_AllowDuplicates_InsertsRenamed()
    {
        using (var seed = _fx.CreateContext())
        {
            seed.Presets.Add(new Preset { Name = "Combat" });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = Array.Empty<object>(),
            Presets = new[]
            {
                new { Id = 10, Name = "Combat", Tracks = Array.Empty<object>() }
            },
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.AllowDuplicates,
            RenameStrategy = DuplicateRenameStrategy.NumericSuffix,
        });

        using var read = _fx.CreateContext();
        read.Presets.Select(p => p.Name).OrderBy(n => n).Should().Equal("Combat", "Combat (2)");
        result.PresetsRenamed.Should().Be(1);
    }

    // ── Playlists + Shortcut pages: smoke coverage ─────────────────────────

    [Fact]
    public async Task Playlists_Skip_AndShortcuts_Skip_PreserveExisting()
    {
        using (var seed = _fx.CreateContext())
        {
            seed.Playlists.Add(new Playlist { Name = "Session 1" });
            seed.ShortcutPages.Add(new ShortcutPage { Name = "Pg", OrderIndex = 0 });
            seed.SaveChanges();
        }

        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = Array.Empty<object>(),
            Presets = Array.Empty<object>(),
            Playlists = new[]
            {
                new { Id = 10, Name = "Session 1", Items = Array.Empty<object>() }
            },
            ShortcutPages = new[]
            {
                new { Id = 20, Name = "Pg", OrderIndex = 0, Buttons = Array.Empty<object>() }
            },
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        var result = await svc.ImportLibraryAsync(jsonPath, new ImportOptions
        {
            DuplicateHandling = DuplicatePolicy.Skip,
        });

        using var read = _fx.CreateContext();
        read.Playlists.Count().Should().Be(1);
        read.ShortcutPages.Count().Should().Be(1);
        result.PlaylistsSkipped.Should().Be(1);
        result.ShortcutsSkipped.Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string MakeAudioFile(string name)
    {
        var p = Path.Combine(_tempDir, name);
        File.WriteAllBytes(p, Array.Empty<byte>()); // content doesn't matter — only existence
        return p;
    }

    /// <summary>Serialize the given anonymous object as the library export
    /// document and return the temp file path. Uses the same JsonSerializer
    /// defaults the service does (no naming policy — PascalCase keys).</summary>
    private string WriteExport(object doc)
    {
        var p = Path.Combine(_tempDir, "export-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(p, json);
        return p;
    }
}
