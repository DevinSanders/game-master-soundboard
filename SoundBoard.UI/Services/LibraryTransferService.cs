using SoundBoard.Core.Services;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Round-trips the whole library to/from a JSON document: every track field,
/// every preset (with all per-entry overrides), every playlist (track or
/// preset items), and every shortcut page/button (including icons + preset
/// references). Schema is versioned so older snapshots can be detected.
/// </summary>
public class LibraryTransferService : ILibraryTransferService
{
    private const int CurrentSchemaVersion = 2;

    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly LibraryManagerService _libraryManager;

    public LibraryTransferService(ISoundBoardDbContextFactory dbFactory, LibraryManagerService libraryManager)
    {
        _dbFactory = dbFactory;
        _libraryManager = libraryManager;
    }

    // ── Export ───────────────────────────────────────────────────────────────

    public async Task ExportLibraryAsync(string filePath)
    {
        using var db = _dbFactory.CreateDbContext();
        var doc = await BuildExportAsync(db);
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
        Log.Info("Transfer", $"Exported library to {filePath} (v{CurrentSchemaVersion})");
    }

    // ── Import ───────────────────────────────────────────────────────────────

    public async Task<ImportResult> ImportLibraryAsync(string filePath, ImportOptions options)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonSerializer.Deserialize<ExportDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Library export file is empty or unreadable.");

        if (doc.Schema > CurrentSchemaVersion)
            Log.Warn("Transfer", $"Library export schema v{doc.Schema} is newer than this app supports (v{CurrentSchemaVersion}); unknown fields will be ignored.");

        var resolver = new PathResolver(options.SearchPaths);

        if (options.Mode == ImportMode.NewLibrary)
            return await ImportIntoNewLibraryAsync(doc, options, resolver);

        // Merge runs against a single fresh context so the entire import is
        // one logical operation with its own change-tracker scope.
        using var db = _dbFactory.CreateDbContext();
        return await ImportMergeAsync(doc, options, resolver, db);
    }

    // ── Merge into the active library ────────────────────────────────────────

    private async Task<ImportResult> ImportMergeAsync(ExportDocument doc, ImportOptions options, PathResolver resolver, SoundBoardDbContext db)
    {
        var result = new ImportResult();

        // === Tracks ===
        //
        // Two collision keys: resolved FilePath and Name. FilePath collisions
        // ALWAYS collapse — a single audio file is a single library entry,
        // regardless of the user's chosen DuplicatePolicy. Name collisions
        // (different FilePath, same Name) are governed by the policy.
        var trackIdMap = new Dictionary<int, int>();
        var existingTracks = await db.Tracks.Select(t => new { t.Name, t.Id }).ToListAsync();
        var trackRegistry = new NameRegistry(existingTracks.Select(t => (t.Name, t.Id)), options.RenameStrategy);

        foreach (var et in doc.Tracks)
        {
            var resolved = resolver.Resolve(et.FilePath);
            if (resolved == null)
            {
                result.MissingFiles.Add(new Track { Id = et.Id, Name = et.Name, FilePath = et.FilePath });
                continue;
            }

            // FilePath collision: always collapse. The existing row's user
            // fields (icon, fades, etc.) survive a merge — only Skip semantic
            // is meaningful here.
            var byPath = await db.Tracks.FirstOrDefaultAsync(t => t.FilePath == resolved);
            if (byPath != null)
            {
                trackIdMap[et.Id] = byPath.Id;
                result.SuccessfullyImported.Add(byPath);
                trackRegistry.Note(byPath.Name, byPath.Id);
                continue;
            }

            // Name collision (with existing-or-already-inserted): apply policy.
            if (trackRegistry.Contains(et.Name))
            {
                if (options.DuplicateHandling == DuplicatePolicy.Skip)
                {
                    var keepId = trackRegistry.GetExistingId(et.Name);
                    if (keepId.HasValue) trackIdMap[et.Id] = keepId.Value;
                    result.TracksSkipped++;
                    continue;
                }
                if (options.DuplicateHandling == DuplicatePolicy.Replace)
                {
                    var keepId = trackRegistry.GetExistingId(et.Name);
                    if (keepId.HasValue)
                    {
                        var existing = await db.Tracks.FindAsync(keepId.Value);
                        if (existing != null)
                        {
                            ApplyExportToTrack(existing, et, resolved);
                            await db.SaveChangesAsync();
                            trackIdMap[et.Id] = existing.Id;
                            result.SuccessfullyImported.Add(existing);
                            result.TracksReplaced++;
                            continue;
                        }
                    }
                    // Registry knew the name but the row vanished — fall
                    // through to a fresh insert under the original name.
                }
                else if (options.DuplicateHandling == DuplicatePolicy.AllowDuplicates)
                {
                    var unique = trackRegistry.MakeUnique(et.Name);
                    var renamed = BuildTrackFromExport(et, resolved);
                    renamed.Name = unique;
                    db.Tracks.Add(renamed);
                    await db.SaveChangesAsync();
                    trackIdMap[et.Id] = renamed.Id;
                    trackRegistry.Note(unique, renamed.Id);
                    result.SuccessfullyImported.Add(renamed);
                    result.TracksRenamed++;
                    continue;
                }
            }

            // No collision: fresh insert at original name.
            var fresh = BuildTrackFromExport(et, resolved);
            db.Tracks.Add(fresh);
            await db.SaveChangesAsync();
            trackIdMap[et.Id] = fresh.Id;
            trackRegistry.Note(fresh.Name, fresh.Id);
            result.SuccessfullyImported.Add(fresh);
        }

        // === Presets ===
        var presetIdMap = new Dictionary<int, int>();
        var existingPresets = await db.Presets.Select(p => new { p.Name, p.Id }).ToListAsync();
        var presetRegistry = new NameRegistry(existingPresets.Select(p => (p.Name, p.Id)), options.RenameStrategy);

        foreach (var ep in doc.Presets)
        {
            string nameToUse = ep.Name;

            if (presetRegistry.Contains(ep.Name))
            {
                if (options.DuplicateHandling == DuplicatePolicy.Skip)
                {
                    var keepId = presetRegistry.GetExistingId(ep.Name);
                    if (keepId.HasValue) presetIdMap[ep.Id] = keepId.Value;
                    result.PresetsSkipped++;
                    continue;
                }
                if (options.DuplicateHandling == DuplicatePolicy.Replace)
                {
                    var keepId = presetRegistry.GetExistingId(ep.Name);
                    if (keepId.HasValue)
                    {
                        var existing = await db.Presets
                            .Include(p => p.Tracks)
                            .FirstOrDefaultAsync(p => p.Id == keepId.Value);
                        if (existing != null)
                        {
                            db.PresetTracks.RemoveRange(existing.Tracks);
                            db.Presets.Remove(existing);
                            await db.SaveChangesAsync();
                            presetRegistry.Forget(existing.Name);
                            result.PresetsReplaced++;
                        }
                    }
                    // Fall through to fresh insert under the original name.
                }
                else if (options.DuplicateHandling == DuplicatePolicy.AllowDuplicates)
                {
                    nameToUse = presetRegistry.MakeUnique(ep.Name);
                    result.PresetsRenamed++;
                }
            }

            var preset = BuildPresetFromExport(ep);
            preset.Name = nameToUse;
            db.Presets.Add(preset);
            await db.SaveChangesAsync();
            presetIdMap[ep.Id] = preset.Id;
            presetRegistry.Note(nameToUse, preset.Id);

            foreach (var pt in ep.Tracks)
            {
                if (!trackIdMap.TryGetValue(pt.TrackId, out var newTrackId)) continue;
                db.PresetTracks.Add(BuildPresetTrackFromExport(pt, preset.Id, newTrackId));
            }
            result.PresetsImported++;
        }
        await db.SaveChangesAsync();

        // === Playlists ===
        var existingPlaylists = await db.Playlists.Select(p => new { p.Name, p.Id }).ToListAsync();
        var playlistRegistry = new NameRegistry(existingPlaylists.Select(p => (p.Name, p.Id)), options.RenameStrategy);

        foreach (var ep in doc.Playlists)
        {
            string nameToUse = ep.Name;

            if (playlistRegistry.Contains(ep.Name))
            {
                if (options.DuplicateHandling == DuplicatePolicy.Skip)
                {
                    result.PlaylistsSkipped++;
                    continue;
                }
                if (options.DuplicateHandling == DuplicatePolicy.Replace)
                {
                    var keepId = playlistRegistry.GetExistingId(ep.Name);
                    if (keepId.HasValue)
                    {
                        var existing = await db.Playlists
                            .Include(p => p.Items)
                            .FirstOrDefaultAsync(p => p.Id == keepId.Value);
                        if (existing != null)
                        {
                            db.PlaylistItems.RemoveRange(existing.Items);
                            db.Playlists.Remove(existing);
                            await db.SaveChangesAsync();
                            playlistRegistry.Forget(existing.Name);
                            result.PlaylistsReplaced++;
                        }
                    }
                }
                else if (options.DuplicateHandling == DuplicatePolicy.AllowDuplicates)
                {
                    nameToUse = playlistRegistry.MakeUnique(ep.Name);
                    result.PlaylistsRenamed++;
                }
            }

            var playlist = BuildPlaylistFromExport(ep);
            playlist.Name = nameToUse;
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();
            playlistRegistry.Note(nameToUse, playlist.Id);

            foreach (var item in ep.Items)
            {
                int? newTrackId  = item.TrackId.HasValue  && trackIdMap.TryGetValue(item.TrackId.Value,  out var t) ? t : null;
                int? newPresetId = item.PresetId.HasValue && presetIdMap.TryGetValue(item.PresetId.Value, out var p) ? p : null;
                if (newTrackId == null && newPresetId == null) continue;
                db.PlaylistItems.Add(new PlaylistItem
                {
                    PlaylistId = playlist.Id, Order = item.Order,
                    TrackId = newTrackId, PresetId = newPresetId,
                });
            }
            result.PlaylistsImported++;
        }
        await db.SaveChangesAsync();

        // === Shortcut pages ===
        var existingPages = await db.ShortcutPages.Select(p => new { p.Name, p.Id }).ToListAsync();
        var pageRegistry = new NameRegistry(existingPages.Select(p => (p.Name, p.Id)), options.RenameStrategy);

        foreach (var page in doc.ShortcutPages)
        {
            string nameToUse = page.Name;

            if (pageRegistry.Contains(page.Name))
            {
                if (options.DuplicateHandling == DuplicatePolicy.Skip)
                {
                    result.ShortcutsSkipped++;
                    continue;
                }
                if (options.DuplicateHandling == DuplicatePolicy.Replace)
                {
                    var keepId = pageRegistry.GetExistingId(page.Name);
                    if (keepId.HasValue)
                    {
                        var existing = await db.ShortcutPages
                            .Include(p => p.Buttons)
                            .FirstOrDefaultAsync(p => p.Id == keepId.Value);
                        if (existing != null)
                        {
                            db.ShortcutButtons.RemoveRange(existing.Buttons);
                            db.ShortcutPages.Remove(existing);
                            await db.SaveChangesAsync();
                            pageRegistry.Forget(existing.Name);
                            result.ShortcutsReplaced++;
                        }
                    }
                }
                else if (options.DuplicateHandling == DuplicatePolicy.AllowDuplicates)
                {
                    nameToUse = pageRegistry.MakeUnique(page.Name);
                    result.ShortcutsRenamed++;
                }
            }

            var newPage = new ShortcutPage { Name = nameToUse, OrderIndex = page.OrderIndex };
            db.ShortcutPages.Add(newPage);
            await db.SaveChangesAsync();
            pageRegistry.Note(nameToUse, newPage.Id);

            foreach (var btn in page.Buttons)
            {
                int? newTrackId  = btn.TrackId.HasValue  && trackIdMap.TryGetValue(btn.TrackId.Value,  out var t) ? t : null;
                int? newPresetId = btn.PresetId.HasValue && presetIdMap.TryGetValue(btn.PresetId.Value, out var p) ? p : null;
                if ((btn.TrackId.HasValue && newTrackId == null) || (btn.PresetId.HasValue && newPresetId == null))
                    continue;

                db.ShortcutButtons.Add(new ShortcutButton
                {
                    ShortcutPageId = newPage.Id,
                    Label = btn.Label, ImagePath = btn.ImagePath, Icon = btn.Icon,
                    Row = btn.Row, Column = btn.Column,
                    TrackId = newTrackId, PresetId = newPresetId,
                });
            }
            result.ShortcutsImported++;
        }
        await db.SaveChangesAsync();

        Log.Info("Transfer",
            $"Merge import done: {result.SuccessfullyImported.Count} tracks " +
            $"(skipped {result.TracksSkipped}, replaced {result.TracksReplaced}, renamed {result.TracksRenamed}), " +
            $"{result.PresetsImported} presets " +
            $"(skipped {result.PresetsSkipped}, replaced {result.PresetsReplaced}, renamed {result.PresetsRenamed}), " +
            $"{result.PlaylistsImported} playlists " +
            $"(skipped {result.PlaylistsSkipped}, replaced {result.PlaylistsReplaced}, renamed {result.PlaylistsRenamed}), " +
            $"{result.ShortcutsImported} pages " +
            $"(skipped {result.ShortcutsSkipped}, replaced {result.ShortcutsReplaced}, renamed {result.ShortcutsRenamed}). " +
            $"{result.MissingFiles.Count} missing files.");
        return result;
    }

    /// <summary>Tracks already-known names (existing DB rows + entries inserted
    /// earlier in this import) so the duplicate policy can detect collisions
    /// both with persisted data and with the import file itself, and so a
    /// non-colliding rename can be generated under
    /// <see cref="DuplicatePolicy.AllowDuplicates"/>. Comparisons are
    /// case-insensitive — users perceive "Battle" and "battle" as the same
    /// name.</summary>
    private sealed class NameRegistry
    {
        private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly DuplicateRenameStrategy _strategy;

        public NameRegistry(IEnumerable<(string Name, int Id)> existing, DuplicateRenameStrategy strategy)
        {
            foreach (var (name, id) in existing) _byName[name] = id;
            _strategy = strategy;
        }

        public bool Contains(string name) => _byName.ContainsKey(name);
        public int? GetExistingId(string name) => _byName.TryGetValue(name, out var id) ? id : null;
        public void Note(string name, int id) => _byName[name] = id;
        public void Forget(string name) => _byName.Remove(name);

        public string MakeUnique(string baseName)
        {
            if (!_byName.ContainsKey(baseName)) return baseName;
            if (_strategy == DuplicateRenameStrategy.NumericSuffix)
            {
                for (int i = 2; ; i++)
                {
                    var candidate = $"{baseName} ({i})";
                    if (!_byName.ContainsKey(candidate)) return candidate;
                }
            }

            // CopySuffix: " (copy)" first, then " (copy 2)", " (copy 3)", …
            var first = $"{baseName} (copy)";
            if (!_byName.ContainsKey(first)) return first;
            for (int i = 2; ; i++)
            {
                var candidate = $"{baseName} (copy {i})";
                if (!_byName.ContainsKey(candidate)) return candidate;
            }
        }
    }

    private static void ApplyExportToTrack(Track existing, ExportedTrack et, string resolvedPath)
    {
        existing.Name = et.Name;
        existing.FilePath = resolvedPath;
        existing.Tags = et.Tags ?? "";
        existing.Icon = et.Icon;
        existing.Volume = et.Volume;
        existing.StartPoint = et.StartPointTicks.HasValue ? new TimeSpan(et.StartPointTicks.Value) : null;
        existing.EndPoint   = et.EndPointTicks.HasValue   ? new TimeSpan(et.EndPointTicks.Value)   : null;
        existing.FadeInDuration  = new TimeSpan(et.FadeInTicks);
        existing.FadeOutDuration = new TimeSpan(et.FadeOutTicks);
        existing.StartDelay      = new TimeSpan(et.StartDelayTicks);
        existing.IsLooping = et.IsLooping;
    }

    // ── Import into a fresh library file ─────────────────────────────────────

    private async Task<ImportResult> ImportIntoNewLibraryAsync(ExportDocument doc, ImportOptions options, PathResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(options.NewLibraryName))
            throw new ArgumentException("NewLibraryName is required when Mode is NewLibrary.", nameof(options));

        var newPath = _libraryManager.ReserveLibraryPath(options.NewLibraryName);

        // Build a one-off context pointed at the new file. EnsureCreated will
        // both create the file and populate the schema. We pass null! for the
        // settings service because the options builder is pre-configured, so
        // OnConfiguring's settings-driven branch never runs.
        var opts = new DbContextOptionsBuilder<SoundBoardDbContext>()
            .UseSqlite($"Data Source={newPath}")
            .Options;

        using (var freshDb = new SoundBoardDbContext(opts, null!))
        {
            freshDb.Database.EnsureCreated();

            // ImportMergeAsync's collision-detection branches are no-ops
            // against an empty DB — but the within-file dedup IS active and
            // applies the same DuplicatePolicy. The CreatedLibraryPath signal
            // tells the caller to switch + restart.
            var result = await ImportMergeAsync(doc, options, resolver, freshDb);
            result.CreatedLibraryPath = newPath;
            Log.Info("Transfer", $"Imported into new library '{options.NewLibraryName}' at {newPath}");
            return result;
        }
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static Track BuildTrackFromExport(ExportedTrack et, string resolvedPath) => new()
    {
        Name = et.Name, FilePath = resolvedPath, Tags = et.Tags ?? "", Icon = et.Icon,
        Volume = et.Volume,
        StartPoint = et.StartPointTicks.HasValue ? new TimeSpan(et.StartPointTicks.Value) : null,
        EndPoint   = et.EndPointTicks.HasValue   ? new TimeSpan(et.EndPointTicks.Value)   : null,
        FadeInDuration  = new TimeSpan(et.FadeInTicks),
        FadeOutDuration = new TimeSpan(et.FadeOutTicks),
        StartDelay      = new TimeSpan(et.StartDelayTicks),
        IsLooping = et.IsLooping,
    };

    private static Preset BuildPresetFromExport(ExportedPreset ep) => new()
    {
        Name = ep.Name, Icon = ep.Icon,
    };

    private static PresetTrack BuildPresetTrackFromExport(ExportedPresetTrack pt, int newPresetId, int newTrackId) => new()
    {
        PresetId = newPresetId, TrackId = newTrackId, Order = pt.Order,
        OverrideVolume = pt.OverrideVolume,
        OverrideStartPoint = pt.OverrideStartPointTicks.HasValue ? new TimeSpan(pt.OverrideStartPointTicks.Value) : null,
        OverrideEndPoint   = pt.OverrideEndPointTicks.HasValue   ? new TimeSpan(pt.OverrideEndPointTicks.Value)   : null,
        OverrideFadeIn     = pt.OverrideFadeInTicks.HasValue     ? new TimeSpan(pt.OverrideFadeInTicks.Value)     : null,
        OverrideFadeOut    = pt.OverrideFadeOutTicks.HasValue    ? new TimeSpan(pt.OverrideFadeOutTicks.Value)    : null,
        OverrideStartDelay = pt.OverrideStartDelayTicks.HasValue ? new TimeSpan(pt.OverrideStartDelayTicks.Value) : null,
        OverrideIsLooping  = pt.OverrideIsLooping,
    };

    private static Playlist BuildPlaylistFromExport(ExportedPlaylist ep) => new()
    {
        Name = ep.Name, Icon = ep.Icon,
    };

    // ── Schema serialization ─────────────────────────────────────────────────

    private static async Task<ExportDocument> BuildExportAsync(SoundBoardDbContext db)
    {
        return new ExportDocument
        {
            Schema = CurrentSchemaVersion,
            ExportedAt = DateTime.UtcNow,
            Tracks = await db.Tracks
                .AsNoTracking()
                .Select(t => new ExportedTrack
                {
                    Id = t.Id, Name = t.Name, FilePath = t.FilePath, Tags = t.Tags, Icon = t.Icon,
                    Volume = t.Volume,
                    StartPointTicks = t.StartPoint != null ? t.StartPoint.Value.Ticks : (long?)null,
                    EndPointTicks   = t.EndPoint   != null ? t.EndPoint.Value.Ticks   : (long?)null,
                    FadeInTicks  = t.FadeInDuration.Ticks,
                    FadeOutTicks = t.FadeOutDuration.Ticks,
                    StartDelayTicks = t.StartDelay.Ticks,
                    IsLooping = t.IsLooping,
                }).ToListAsync(),
            Presets = await db.Presets
                .AsNoTracking()
                .Include(p => p.Tracks)
                .Select(p => new ExportedPreset
                {
                    Id = p.Id, Name = p.Name, Icon = p.Icon,
                    Tracks = p.Tracks.OrderBy(t => t.Order).Select(pt => new ExportedPresetTrack
                    {
                        TrackId = pt.TrackId, Order = pt.Order,
                        OverrideVolume = pt.OverrideVolume,
                        OverrideStartPointTicks = pt.OverrideStartPoint != null ? pt.OverrideStartPoint.Value.Ticks : (long?)null,
                        OverrideEndPointTicks   = pt.OverrideEndPoint   != null ? pt.OverrideEndPoint.Value.Ticks   : (long?)null,
                        OverrideFadeInTicks     = pt.OverrideFadeIn     != null ? pt.OverrideFadeIn.Value.Ticks     : (long?)null,
                        OverrideFadeOutTicks    = pt.OverrideFadeOut    != null ? pt.OverrideFadeOut.Value.Ticks    : (long?)null,
                        OverrideStartDelayTicks = pt.OverrideStartDelay != null ? pt.OverrideStartDelay.Value.Ticks : (long?)null,
                        OverrideIsLooping = pt.OverrideIsLooping,
                    }).ToList()
                }).ToListAsync(),
            Playlists = await db.Playlists
                .AsNoTracking()
                .Include(p => p.Items)
                .Select(p => new ExportedPlaylist
                {
                    Id = p.Id, Name = p.Name, Icon = p.Icon,
                    Items = p.Items.OrderBy(i => i.Order).Select(i => new ExportedPlaylistItem
                    {
                        Order = i.Order, TrackId = i.TrackId, PresetId = i.PresetId
                    }).ToList()
                }).ToListAsync(),
            ShortcutPages = await db.ShortcutPages
                .AsNoTracking()
                .Include(p => p.Buttons)
                .OrderBy(p => p.OrderIndex)
                .Select(p => new ExportedShortcutPage
                {
                    Id = p.Id, Name = p.Name, OrderIndex = p.OrderIndex,
                    Buttons = p.Buttons.OrderBy(b => b.Row).ThenBy(b => b.Column).Select(b => new ExportedShortcutButton
                    {
                        Label = b.Label, ImagePath = b.ImagePath, Icon = b.Icon,
                        Row = b.Row, Column = b.Column,
                        TrackId = b.TrackId, PresetId = b.PresetId,
                    }).ToList()
                }).ToListAsync(),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ExportDocument
    {
        public int Schema { get; set; }
        public DateTime ExportedAt { get; set; }
        public List<ExportedTrack> Tracks { get; set; } = new();
        public List<ExportedPreset> Presets { get; set; } = new();
        public List<ExportedPlaylist> Playlists { get; set; } = new();
        public List<ExportedShortcutPage> ShortcutPages { get; set; } = new();
    }

    private sealed class ExportedTrack
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string? Tags { get; set; }
        public string? Icon { get; set; }
        public float Volume { get; set; } = 1.0f;
        public long? StartPointTicks { get; set; }
        public long? EndPointTicks { get; set; }
        public long FadeInTicks { get; set; }
        public long FadeOutTicks { get; set; }
        public long StartDelayTicks { get; set; }
        public bool IsLooping { get; set; }

        // AutoTrimSilence was previously persisted here; the flag has been
        // replaced with an editor-side "Trim silence now" action. We still
        // deserialize the JSON field if present (no-op getter/setter) so
        // older export bundles import cleanly.
        public bool AutoTrimSilence { get; set; }
    }

    private sealed class ExportedPreset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Icon { get; set; }
        public List<ExportedPresetTrack> Tracks { get; set; } = new();
    }

    private sealed class ExportedPresetTrack
    {
        public int TrackId { get; set; }
        public int Order { get; set; }
        public float? OverrideVolume { get; set; }
        public long? OverrideStartPointTicks { get; set; }
        public long? OverrideEndPointTicks { get; set; }
        public long? OverrideFadeInTicks { get; set; }
        public long? OverrideFadeOutTicks { get; set; }
        public long? OverrideStartDelayTicks { get; set; }
        public bool? OverrideIsLooping { get; set; }
    }

    private sealed class ExportedPlaylist
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Icon { get; set; }
        public List<ExportedPlaylistItem> Items { get; set; } = new();
    }

    private sealed class ExportedPlaylistItem
    {
        public int Order { get; set; }
        public int? TrackId { get; set; }
        public int? PresetId { get; set; }
    }

    private sealed class ExportedShortcutPage
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int OrderIndex { get; set; }
        public List<ExportedShortcutButton> Buttons { get; set; } = new();
    }

    private sealed class ExportedShortcutButton
    {
        public string? Label { get; set; }
        public string? ImagePath { get; set; }
        public string? Icon { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int? TrackId { get; set; }
        public int? PresetId { get; set; }
    }
}
