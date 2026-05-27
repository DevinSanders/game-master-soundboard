using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Library window — the master list of tracks with tag filtering,
/// search, per-row play/pause, drag source for Soundboard/Playlist/Preset
/// drop targets, and the entry point to the track editor. Refreshes when the
/// active library changes (via <see cref="LibraryRefreshedMessage"/>) or
/// when the soundboard's selected page changes (so the "send to page" menu
/// item targets the right page).
/// </summary>
public partial class LibraryViewModel : ViewModelBase, IRecipient<ShortcutPageChangedMessage>, IRecipient<LibraryRefreshedMessage>
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;

    [ObservableProperty]
    private ObservableCollection<Track> _tracks = new();

    /// <summary>Filter + sort target for the library DataGrid. Kept as a
    /// stable reference (mutated via <see cref="ObservableCollection{T}.Clear"/>
    /// + Add rather than reassigned) so the grid's internal sort state
    /// survives filter changes — replacing the collection blows away the
    /// user's chosen sort column.</summary>
    public ObservableCollection<Track> FilteredTracks { get; } = new();

    [ObservableProperty]
    private ObservableCollection<string> _availableTags = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _selectedTag;

    [ObservableProperty]
    private Track? _selectedTrack;

    [ObservableProperty]
    private ObservableCollection<ShortcutPage> _soundboardPages = new();

    private readonly IFileService _fileService;
    private readonly IWindowManagerService _windowManager;
    private readonly SoundBoard.Core.Services.IPluginService _pluginService;
    private readonly ILibraryTransferService _transferService;
    private readonly Func<TrackEditorViewModel> _trackEditorFactory;

    public LibraryViewModel(ISoundBoardDbContextFactory dbFactory, IFileService fileService, IWindowManagerService windowManager,
        IAudioPlaybackEngine playbackEngine, SoundBoard.Core.Services.IPluginService pluginService,
        ILibraryTransferService transferService, Func<TrackEditorViewModel> trackEditorFactory)
    {
        _dbFactory = dbFactory;
        _fileService = fileService;
        _windowManager = windowManager;
        _playbackEngine = playbackEngine;
        _pluginService = pluginService;
        _transferService = transferService;
        _trackEditorFactory = trackEditorFactory;

        WeakReferenceMessenger.Default.Register<ShortcutPageChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<LibraryRefreshedMessage>(this);

        LoadTracks();
        LoadPages();
    }

    public void Receive(ShortcutPageChangedMessage message)
    {
        LoadPages();
    }

    public void Receive(LibraryRefreshedMessage message)
    {
        // Fresh-context-per-op means there's no shared identity map to
        // invalidate — every reload pulls live data. Just re-query.
        LoadTracks();
        LoadPages();
    }

    private void LoadPages()
    {
        using var db = _dbFactory.CreateDbContext();
        var pages = db.ShortcutPages.AsNoTracking().OrderBy(p => p.OrderIndex).ToList();
        // Mutate in place so any consumer holding a reference (e.g. the
        // soundboard page combo's SelectedItem) keeps its binding intact.
        // Pre-fix this reassigned SoundboardPages, dropping any current
        // selection.
        SoundboardPages.Clear();
        foreach (var p in pages) SoundboardPages.Add(p);
    }

    private void LoadTracks()
    {
        using var db = _dbFactory.CreateDbContext();
        var tracks = db.Tracks.AsNoTracking().ToList();
        // Mutate in place — same rationale as LoadPages above. Any open
        // DataGrid bound to Tracks preserves its sort/selection.
        Tracks.Clear();
        foreach (var t in tracks) Tracks.Add(t);
        UpdateAvailableTags();
        UpdateFilters();
        PopulateMissingDurations();
    }

    partial void OnSearchTextChanged(string value) => UpdateFilters();
    partial void OnSelectedTagChanged(string? value) => UpdateFilters();

    public void UpdateFilters()
    {
        var filtered = Tracks.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(t => t.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase) ||
                                           t.FilePath.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedTag) && SelectedTag != "All")
        {
            filtered = filtered.Where(t => t.Tags.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(tag => tag.Trim())
                                                 .Contains(SelectedTag, System.StringComparer.OrdinalIgnoreCase));
        }

        // Default sort by name. DataGrid then re-sorts on column header click
        // via its own collection view — which only survives because we Clear
        // + Add into the same FilteredTracks instance instead of replacing it.
        var ordered = filtered.OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase).ToList();

        FilteredTracks.Clear();
        foreach (var t in ordered) FilteredTracks.Add(t);
    }

    public void UpdateAvailableTags()
    {
        var tags = Tracks.SelectMany(t => t.Tags.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                          .Select(t => t.Trim())
                          .Distinct(System.StringComparer.OrdinalIgnoreCase)
                          .OrderBy(t => t)
                          .ToList();

        tags.Insert(0, "All");
        // Mutate in place so the tag combo's SelectedItem keeps tracking.
        AvailableTags.Clear();
        foreach (var t in tags) AvailableTags.Add(t);
        if (SelectedTag == null || !AvailableTags.Contains(SelectedTag))
        {
            SelectedTag = "All";
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ImportTracks()
    {
        var files = await _fileService.OpenFileDialogAsync("Select Audio Files", new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" });
        ImportFiles(files);
    }

    public void ImportFiles(System.Collections.Generic.IEnumerable<string> filePaths)
    {
        using var db = _dbFactory.CreateDbContext();
        bool addedAny = false;
        foreach (var path in filePaths)
        {
            if (!db.Tracks.Any(t => t.FilePath == path))
            {
                var track = new Track
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    FileDuration = TryReadDuration(path),
                };
                db.Tracks.Add(track);
                Tracks.Add(track);
                addedAny = true;
            }
        }

        if (addedAny)
        {
            db.SaveChanges();
            UpdateAvailableTags();
            UpdateFilters();
        }
    }

    /// <summary>Open <paramref name="filePath"/> long enough to read its
    /// total time. Returns null on decode error — the caller still adds the
    /// track; we just won't have a length to display until the next time
    /// it's played or scanned.</summary>
    private static TimeSpan? TryReadDuration(string filePath)
    {
        try
        {
            var reader = AudioFileReaderCrossPlatform.Create(filePath);
            try { return reader.TotalTime; }
            finally { if (reader is IDisposable d) d.Dispose(); }
        }
        catch (Exception ex)
        {
            Log.Warn("Library", $"Could not read duration for {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Background pass that fills <see cref="Track.FileDuration"/>
    /// for any track still missing it (older databases, imports that failed
    /// the eager read). Runs once after <see cref="LoadTracks"/>. The newly
    /// populated values land in the bound rows directly so the column
    /// updates as scanning progresses — Track is a POCO so we replace the
    /// item in the collection to trigger the binding refresh.</summary>
    private void PopulateMissingDurations()
    {
        // Snapshot ids that need scanning so we don't hold the collection
        // across the background work.
        var toScan = Tracks.Where(t => !t.FileDuration.HasValue && !string.IsNullOrWhiteSpace(t.FilePath))
                           .Select(t => (t.Id, t.FilePath))
                           .ToList();
        if (toScan.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var (id, path) in toScan)
            {
                var duration = TryReadDuration(path);
                if (!duration.HasValue) continue;
                try
                {
                    if (!_dbFactory.EditorSave<Core.Models.Track>(id, t => t.FileDuration = duration))
                        continue;
                }
                catch (Exception ex)
                {
                    Log.Warn("Library", $"Failed to persist duration for track #{id}: {ex.Message}");
                    continue;
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Reassign the in-memory row's field AND replace the
                    // ObservableCollection entry so the DataGrid re-evaluates
                    // the binding (Track is a POCO with no INPC of its own).
                    int idx = -1;
                    Track? row = null;
                    for (int i = 0; i < Tracks.Count; i++)
                    {
                        if (Tracks[i].Id == id) { idx = i; row = Tracks[i]; break; }
                    }
                    if (row != null && idx >= 0)
                    {
                        row.FileDuration = duration;
                        Tracks[idx] = row;
                    }
                    int fidx = -1;
                    for (int i = 0; i < FilteredTracks.Count; i++)
                    {
                        if (FilteredTracks[i].Id == id) { fidx = i; break; }
                    }
                    if (fidx >= 0 && row != null) FilteredTracks[fidx] = row;
                });
            }
        });
    }

    [RelayCommand]
    private void PlayTrack(Track track)
    {
        if (track != null)
        {
            _playbackEngine.TogglePlayPause(track);
        }
    }

    [RelayCommand]
    private void EditTrack(Track track)
    {
        if (track != null)
        {
            // Resolve through DI so future constructor dependencies (and
            // the Phase 4 generic editor-save helper) flow in without
            // touching every call site. Factory pattern matches the
            // PresetEditor route already in use.
            var editorVm = _trackEditorFactory();
            editorVm.Track = track;
            // Sized to comfortably show the waveform preview, every settings
            // row, AND the Save button without scrolling. ScrollViewer still
            // kicks in if the user shrinks the window or installs plugins
            // that inject extra controls.
            //
            // Per-track key so opening editor for Track A then Track B
            // doesn't swap the content of a single shared window (which
            // would discard A's in-flight debounced writes — the prior
            // editor's Unloaded never fires). Each track gets its own
            // window, deduped to itself.
            _windowManager.ShowWindow(editorVm, key: $"track-editor-{track.Id}",
                title: $"Edit Track - {track.Name}", width: 900, height: 820);
        }
    }

    [RelayCommand]
    private void DeleteTrack(Track track)
    {
        if (track == null) return;
        using var db = _dbFactory.CreateDbContext();

        // Same FK-cleanup problem as DeletePreset — ShortcutButton.TrackId,
        // PlaylistItem.TrackId, and PresetTrack.TrackId all reference this row
        // and would block the delete at the SQLite level. Remove the dependents
        // explicitly so the Track row can go.
        var trackId = track.Id;
        var orphanedButtons = db.ShortcutButtons.Where(b => b.TrackId == trackId).ToList();
        db.ShortcutButtons.RemoveRange(orphanedButtons);

        var orphanedPlaylistItems = db.PlaylistItems.Where(i => i.TrackId == trackId).ToList();
        db.PlaylistItems.RemoveRange(orphanedPlaylistItems);

        var orphanedPresetTracks = db.PresetTracks.Where(pt => pt.TrackId == trackId).ToList();
        db.PresetTracks.RemoveRange(orphanedPresetTracks);

        // The track param is detached; Remove attaches it as Deleted.
        db.Tracks.Remove(track);
        db.SaveChanges();
        Tracks.Remove(track);
        UpdateAvailableTags();
        UpdateFilters();
    }

    [RelayCommand]
    private void SendToPage(int pageId)
    {
        if (SelectedTrack == null) return;
        using var db = _dbFactory.CreateDbContext();

        var count = db.ShortcutButtons.Count(b => b.ShortcutPageId == pageId);

        var btn = new ShortcutButton
        {
            ShortcutPageId = pageId,
            TrackId = SelectedTrack.Id,
            Label = SelectedTrack.Name,
            Row = count
        };
        db.ShortcutButtons.Add(btn);
        db.SaveChanges();

        // Notify the ShortcutsViewModel to refresh
        WeakReferenceMessenger.Default.Send(new ShortcutAddedMessage(pageId));
    }

    [RelayCommand]
    private void SendToSoundboard(Track track)
    {
        if (track == null) return;
        using var db = _dbFactory.CreateDbContext();

        var firstPage = db.ShortcutPages.AsNoTracking().FirstOrDefault();
        if (firstPage != null)
        {
            SendToPage(firstPage.Id);
        }
    }

}
