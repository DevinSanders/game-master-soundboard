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

    // ── Bus round-trip (schema 3) ──────────────────────────────────────────

    [Fact]
    public async Task Schema3_BuiltInBusIds_MapIdentityOnTrackImport()
    {
        var path = MakeAudioFile("ambient-loop.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Buses = new[]
            {
                new { Id = 1, Name = "Music",   Order = 0, IsBuiltIn = true, Volume = 1.0f },
                new { Id = 2, Name = "Ambient", Order = 1, IsBuiltIn = true, Volume = 1.0f },
                new { Id = 3, Name = "SFX",     Order = 2, IsBuiltIn = true, Volume = 1.0f },
            },
            Tracks = new[] { new { Id = 7, Name = "Forest", FilePath = path, BusId = 2 } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        read.Tracks.Single().BusId.Should().Be(BuiltInBusIds.Ambient);
    }

    [Fact]
    public async Task Schema3_CustomBus_ExistingByName_RemapsToDestinationId()
    {
        // Destination already has a custom bus called "Combat" at id 17.
        // Source's "Combat" is id 99 — the importer must remap source.99 →
        // destination.17 on the track row, NOT insert a duplicate bus.
        int destinationCombatId;
        using (var seed = _fx.CreateContext())
        {
            var existing = new Bus { Name = "Combat", Order = 5, IsBuiltIn = false, Volume = 0.8f };
            seed.Buses.Add(existing);
            seed.SaveChanges();
            destinationCombatId = existing.Id;
        }

        var path = MakeAudioFile("battle-theme.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Buses = new[]
            {
                new { Id = 99, Name = "Combat", Order = 10, IsBuiltIn = false, Volume = 0.5f },
            },
            Tracks = new[] { new { Id = 1, Name = "Battle Theme", FilePath = path, BusId = 99 } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        read.Buses.Where(b => b.Name == "Combat").Count().Should().Be(1);
        var combat = read.Buses.Single(b => b.Name == "Combat");
        combat.Id.Should().Be(destinationCombatId);
        combat.Volume.Should().BeApproximately(0.8f, 0.001f); // destination settings preserved
        read.Tracks.Single().BusId.Should().Be(destinationCombatId);
    }

    [Fact]
    public async Task Schema3_CustomBus_MissingInDestination_InsertedAndTrackRemapped()
    {
        var path = MakeAudioFile("eerie-pad.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Buses = new[]
            {
                new { Id = 50, Name = "Suspense", Order = 7, IsBuiltIn = false,
                      Color = "#FF8800", Volume = 0.6f },
            },
            Tracks = new[] { new { Id = 1, Name = "Eerie Pad", FilePath = path, BusId = 50 } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        var bus = read.Buses.Single(b => b.Name == "Suspense");
        bus.IsBuiltIn.Should().BeFalse();
        bus.Order.Should().Be(7);
        bus.Color.Should().Be("#FF8800");
        bus.Volume.Should().BeApproximately(0.6f, 0.001f);
        read.Tracks.Single().BusId.Should().Be(bus.Id);
    }

    [Fact]
    public async Task Schema3_PresetBusOverride_RoundTripsThroughMap()
    {
        var path = MakeAudioFile("piano.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Buses = new[]
            {
                new { Id = 1, Name = "Music", Order = 0, IsBuiltIn = true, Volume = 1.0f },
                new { Id = 3, Name = "SFX",   Order = 2, IsBuiltIn = true, Volume = 1.0f },
            },
            Tracks = new[] { new { Id = 1, Name = "Piano", FilePath = path, BusId = 1 } },
            Presets = new[]
            {
                new { Id = 10, Name = "Stinger", BusIdOverride = (int?)3,
                      Tracks = new[] { new { TrackId = 1, Order = 0 } } }
            },
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        read.Presets.Single().BusIdOverride.Should().Be(BuiltInBusIds.Sfx);
    }

    [Fact]
    public async Task Schema3_ShortcutBusOverride_RoundTripsThroughMap()
    {
        var path = MakeAudioFile("door-slam.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Buses = new[]
            {
                new { Id = 1, Name = "Music", Order = 0, IsBuiltIn = true, Volume = 1.0f },
                new { Id = 3, Name = "SFX",   Order = 2, IsBuiltIn = true, Volume = 1.0f },
            },
            Tracks = new[] { new { Id = 1, Name = "Door Slam", FilePath = path, BusId = 1 } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = new[]
            {
                new { Id = 20, Name = "Page", OrderIndex = 0,
                      Buttons = new[]
                      {
                          new { Label = "Slam", Row = 0, Column = 0,
                                TrackId = (int?)1, BusIdOverride = (int?)3 }
                      } }
            },
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        var btn = read.ShortcutButtons.Single();
        btn.BusIdOverride.Should().Be(BuiltInBusIds.Sfx);
    }

    [Fact]
    public async Task Schema2_Bundle_TrackWithoutBusId_FallsBackToDefaultBus()
    {
        // Schema-2 bundle: no Buses collection, no BusId on tracks. The
        // importer must not throw, and the track lands on the default bus.
        var path = MakeAudioFile("legacy.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 2,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new { Id = 1, Name = "Legacy", FilePath = path } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = Array.Empty<object>(),
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        read.Tracks.Single().BusId.Should().Be(BuiltInBusIds.DefaultForNewTracks);
    }

    [Fact]
    public async Task Schema3_ShortcutIconAndButtonColor_RoundTrip()
    {
        var path = MakeAudioFile("horn.wav");
        var jsonPath = WriteExport(new
        {
            Schema = 3,
            ExportedAt = DateTime.UtcNow,
            Tracks = new[] { new { Id = 1, Name = "War Horn", FilePath = path } },
            Presets = Array.Empty<object>(),
            Playlists = Array.Empty<object>(),
            ShortcutPages = new[]
            {
                new { Id = 20, Name = "Board", OrderIndex = 0,
                      Buttons = new[]
                      {
                          new { Label = "Horn", Row = 0, Column = 0, TrackId = (int?)1,
                                Icon = "ra-horn-call", IconColor = "#F2C14E", ButtonColor = "#7A1F1F" }
                      } }
            },
        });

        var svc = new LibraryTransferService(_fx.Factory, _libraryManager);
        await svc.ImportLibraryAsync(jsonPath, new ImportOptions());

        using var read = _fx.CreateContext();
        var btn = read.ShortcutButtons.Single();
        btn.Icon.Should().Be("ra-horn-call");
        btn.IconColor.Should().Be("#F2C14E");
        btn.ButtonColor.Should().Be("#7A1F1F");
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
