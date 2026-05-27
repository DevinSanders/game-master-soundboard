using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Top-level Playlists window. Items can be Tracks or Presets, dropped onto
/// the editor pane via drag-and-drop. <see cref="IAudioPlaybackEngine.PlayPlaylist"/>
/// chains them sequentially.
///
/// Persistence pattern: every mutating method opens a fresh context, calls
/// <c>db.Set.Find(id)</c> to get a clean tracked instance, mutates only the
/// columns it cares about, and saves. We deliberately avoid
/// <c>db.Set.Update(detachedEntity)</c> because the entity graphs loaded into
/// the editor (Playlist → Items → Track/Preset) share nav references across
/// rows — re-attaching them would throw "instance with same key already
/// tracked" inside the same SaveChanges.
/// </summary>
public partial class PlaylistsViewModel : ViewModelBase, IRecipient<LibraryRefreshedMessage>
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly ISamplerChainService _samplerChain;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly IPluginService _pluginService;
    private readonly IWindowManagerService _windowManager;
    private readonly IAudioPlaybackEngine _playbackEngine;

    /// <summary>Debounced save coordinator. Slider drags suspend the timer
    /// via the view's <c>SliderBurstBehavior</c> and flush on release.</summary>
    public EditPersistence Persistence { get; } = new();

    public ObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>The current playlist's items wrapped in per-row VMs so the
    /// per-item loop-override button can repaint without a row replace.</summary>
    public ObservableCollection<PlaylistEditorItemViewModel> CurrentItems { get; } = new();

    [ObservableProperty]
    private Playlist? _selectedPlaylist;

    [ObservableProperty]
    private PlaylistEditorItemViewModel? _selectedItem;

    /// <summary>Observable shim over <see cref="Playlist.Icon"/> — same rationale
    /// as PresetEditorViewModel.PresetIcon (POCO model = no auto-notify).</summary>
    public string? SelectedPlaylistIcon
    {
        get => SelectedPlaylist?.Icon;
        set
        {
            if (SelectedPlaylist == null || SelectedPlaylist.Icon == value) return;
            SelectedPlaylist.Icon = value;
            SchedulePlaylistSave();
            OnPropertyChanged();
        }
    }

    /// <summary>Playlist-wide fade-in in seconds. Editor slider binds here;
    /// the setter persists to the underlying <see cref="Playlist.FadeInDuration"/>.</summary>
    public double SelectedPlaylistFadeInSeconds
    {
        get => SelectedPlaylist?.FadeInDuration.TotalSeconds ?? 0;
        set
        {
            if (SelectedPlaylist == null) return;
            var clamped = System.Math.Max(0, value);
            var newSpan = System.TimeSpan.FromSeconds(clamped);
            if (SelectedPlaylist.FadeInDuration == newSpan) return;
            SelectedPlaylist.FadeInDuration = newSpan;
            SchedulePlaylistSave();
            OnPropertyChanged();
        }
    }

    public double SelectedPlaylistFadeOutSeconds
    {
        get => SelectedPlaylist?.FadeOutDuration.TotalSeconds ?? 0;
        set
        {
            if (SelectedPlaylist == null) return;
            var clamped = System.Math.Max(0, value);
            var newSpan = System.TimeSpan.FromSeconds(clamped);
            if (SelectedPlaylist.FadeOutDuration == newSpan) return;
            SelectedPlaylist.FadeOutDuration = newSpan;
            SchedulePlaylistSave();
            OnPropertyChanged();
        }
    }

    public bool SelectedPlaylistAutoplay
    {
        get => SelectedPlaylist?.Autoplay ?? true;
        set
        {
            if (SelectedPlaylist == null || SelectedPlaylist.Autoplay == value) return;
            SelectedPlaylist.Autoplay = value;
            SchedulePlaylistSave();
            OnPropertyChanged();
        }
    }

    public bool SelectedPlaylistRandom
    {
        get => SelectedPlaylist?.Random ?? false;
        set
        {
            if (SelectedPlaylist == null || SelectedPlaylist.Random == value) return;
            SelectedPlaylist.Random = value;
            SchedulePlaylistSave();
            OnPropertyChanged();
        }
    }

    private void SchedulePlaylistSave()
    {
        if (SelectedPlaylist == null) return;
        var id = SelectedPlaylist.Id;
        Persistence.Schedule($"Playlist:{id}", () =>
        {
            if (SelectedPlaylist == null) return;
            _dbFactory.EditorSave<Core.Models.Playlist>(id, tracked =>
            {
                tracked.Icon = SelectedPlaylist.Icon;
                tracked.FadeInDuration = SelectedPlaylist.FadeInDuration;
                tracked.FadeOutDuration = SelectedPlaylist.FadeOutDuration;
                tracked.Autoplay = SelectedPlaylist.Autoplay;
                tracked.Random = SelectedPlaylist.Random;
            });
        });
    }


    public PlaylistsViewModel(ISoundBoardDbContextFactory dbFactory, IAudioPlaybackEngine playbackEngine,
        ISamplerChainService samplerChain, ISamplerLauncherService samplerLauncher,
        IPluginService pluginService, IWindowManagerService windowManager)
    {
        _dbFactory = dbFactory;
        _playbackEngine = playbackEngine;
        _samplerChain = samplerChain;
        _samplerLauncher = samplerLauncher;
        _pluginService = pluginService;
        _windowManager = windowManager;
        WeakReferenceMessenger.Default.Register(this);
        Reload();
    }

    public void Receive(LibraryRefreshedMessage message) => Reload();

    [RelayCommand]
    private void OpenFxChain()
    {
        if (SelectedPlaylist == null) return;
        _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Playlist, SelectedPlaylist.Id, SelectedPlaylist.Name ?? "");
    }

    public void Reload()
    {
        var prevSelectedId = SelectedPlaylist?.Id;

        using var db = _dbFactory.CreateDbContext();
        Playlists.Clear();
        // Include Items so the "N item(s)" badge in the list shows a fresh
        // count. AsNoTracking + AsNoTracking-equivalent on the nav prevents
        // the graph-attach hazards we avoided in the mutation methods.
        foreach (var p in db.Playlists
                .AsNoTracking()
                .Include(p => p.Items)
                .OrderBy(p => p.Name)
                .ToList())
        {
            Playlists.Add(p);
        }

        if (prevSelectedId.HasValue)
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == prevSelectedId.Value);
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        RebuildCurrentItems();
        OnPropertyChanged(nameof(SelectedPlaylistIcon));
        OnPropertyChanged(nameof(SelectedPlaylistFadeInSeconds));
        OnPropertyChanged(nameof(SelectedPlaylistFadeOutSeconds));
        OnPropertyChanged(nameof(SelectedPlaylistAutoplay));
        OnPropertyChanged(nameof(SelectedPlaylistRandom));
    }

    /// <summary>Re-queries the items for the selected playlist from the DB so
    /// the editor always reflects current state — never trust the stale
    /// <see cref="Playlist.Items"/> nav from a prior load.</summary>
    private void RebuildCurrentItems()
    {
        CurrentItems.Clear();
        if (SelectedPlaylist == null) return;

        using var db = _dbFactory.CreateDbContext();
        var items = db.PlaylistItems
            .AsNoTracking()
            .Where(i => i.PlaylistId == SelectedPlaylist.Id)
            .Include(i => i.Track)
            .Include(i => i.Preset)
            .OrderBy(i => i.Order)
            .ToList();

        foreach (var item in items)
            CurrentItems.Add(new PlaylistEditorItemViewModel(item));
    }

    // ── Playlist CRUD ────────────────────────────────────────────────────────

    [RelayCommand]
    private void CreatePlaylist()
    {
        using var db = _dbFactory.CreateDbContext();
        var pl = new Playlist { Name = $"Playlist {Playlists.Count + 1}" };
        db.Playlists.Add(pl);
        db.SaveChanges();
        Reload();
        SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == pl.Id);
    }

    [RelayCommand]
    private void DeletePlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.Playlists
            .Include(p => p.Items)
            .FirstOrDefault(p => p.Id == playlist.Id);
        if (tracked == null) return;

        // Same FK-cleanup story as DeletePreset / DeleteTrack — soundboard
        // buttons that target this playlist have a nullable FK with no SQLite
        // cascade, so we have to remove them ourselves.
        var playlistId = tracked.Id;
        var orphanedButtons = db.ShortcutButtons.Where(b => b.PlaylistId == playlistId).ToList();
        db.ShortcutButtons.RemoveRange(orphanedButtons);

        db.PlaylistItems.RemoveRange(tracked.Items);
        db.Playlists.Remove(tracked);
        db.SaveChanges();

        // Drop FX Chain attachments owned by this playlist. The shortcut
        // buttons that pointed to it (deleted above) may have Shortcut-tier
        // attachments tied to them as well; clean those up by id too.
        _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Playlist, playlistId);
        foreach (var b in orphanedButtons)
            _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Shortcut, b.Id);

        Reload();
    }

    // ── Add items (called from drag-drop handlers in the view) ───────────────

    public void AddTrack(Track track)
    {
        if (SelectedPlaylist == null) return;
        var playlistId = SelectedPlaylist.Id;
        using (var db = _dbFactory.CreateDbContext())
        {
            var item = new PlaylistItem
            {
                PlaylistId = playlistId,
                TrackId = track.Id,
                Order = CurrentItems.Count,
            };
            db.PlaylistItems.Add(item);
            db.SaveChanges();
        }
        // Reload picks up the new count on the list row; the selection-changed
        // path re-queries CurrentItems with the new entry.
        Reload();
        WeakReferenceMessenger.Default.Send(new PlaylistItemsChangedMessage(playlistId));
    }

    public void AddPreset(Preset preset)
    {
        if (SelectedPlaylist == null) return;
        var playlistId = SelectedPlaylist.Id;
        using (var db = _dbFactory.CreateDbContext())
        {
            var item = new PlaylistItem
            {
                PlaylistId = playlistId,
                PresetId = preset.Id,
                Order = CurrentItems.Count,
            };
            db.PlaylistItems.Add(item);
            db.SaveChanges();
        }
        Reload();
        WeakReferenceMessenger.Default.Send(new PlaylistItemsChangedMessage(playlistId));
    }

    // ── Item management ──────────────────────────────────────────────────────

    [RelayCommand]
    private void RemoveItem(PlaylistEditorItemViewModel? row)
    {
        if (row == null || SelectedPlaylist == null) return;
        var playlistId = SelectedPlaylist.Id;
        using (var db = _dbFactory.CreateDbContext())
        {
            // Use Find to get a clean tracked entity (no nav graph attached)
            // — db.PlaylistItems.Remove(detachedEntity) would otherwise drag in
            // the row's Track/Preset references and collide with the renumber
            // Find calls below.
            var tracked = db.PlaylistItems.Find(row.Model.Id);
            if (tracked == null) return;
            db.PlaylistItems.Remove(tracked);

            // Renumber the remaining rows for this playlist in-DB so the order
            // stays gap-free after the removal.
            var remaining = db.PlaylistItems
                .Where(i => i.PlaylistId == playlistId && i.Id != row.Model.Id)
                .OrderBy(i => i.Order)
                .ToList();
            for (int i = 0; i < remaining.Count; i++)
                remaining[i].Order = i;

            db.SaveChanges();
        }
        Reload();
        WeakReferenceMessenger.Default.Send(new PlaylistItemsChangedMessage(playlistId));
    }

    /// <summary>Cycles a playlist item's loop override: inherit (null) →
    /// force-on (true) → force-off (false) → inherit (null). Persisted on
    /// the underlying <see cref="PlaylistItem"/> row; the per-row VM raises
    /// PropertyChanged so the bound button refreshes in place.</summary>
    [RelayCommand]
    private void CycleItemLoopOverride(PlaylistEditorItemViewModel? row)
    {
        if (row == null) return;
        row.OverrideIsLooping = row.OverrideIsLooping switch
        {
            null => true,
            true => false,
            false => null,
        };
        _dbFactory.EditorSave<Core.Models.PlaylistItem>(row.Model.Id, tracked =>
            tracked.OverrideIsLooping = row.OverrideIsLooping);
    }

    /// <summary>Called by the view during a drag-reorder. Moves the source
    /// row adjacent to <paramref name="target"/> in the current playlist and
    /// rewrites every entry's <see cref="PlaylistItem.Order"/> so sequential
    /// playback stays correct.</summary>
    public void MoveItem(PlaylistEditorItemViewModel source, PlaylistEditorItemViewModel target)
    {
        if (source == target) return;
        int s = CurrentItems.IndexOf(source);
        int t = CurrentItems.IndexOf(target);
        if (s < 0 || t < 0) return;

        CurrentItems.Move(s, t);
        for (int i = 0; i < CurrentItems.Count; i++) CurrentItems[i].Order = i;
    }

    /// <summary>Persist the current visual order to the database. Called by the
    /// view on drop completion to avoid hammering SQLite on every DragOver.</summary>
    public void PersistOrder()
    {
        if (CurrentItems.Count == 0) return;
        using var db = _dbFactory.CreateDbContext();
        foreach (var row in CurrentItems)
        {
            var tracked = db.PlaylistItems.Find(row.Model.Id);
            if (tracked != null) tracked.Order = row.Order;
        }
        db.SaveChanges();
    }

    public void SetIconForSelected(string? icon)
    {
        SelectedPlaylistIcon = icon;
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedPlaylist == null) return;
        _playbackEngine.PlayPlaylist(SelectedPlaylist);
    }

    [RelayCommand]
    private void StopSelected()
    {
        if (SelectedPlaylist == null) return;
        _playbackEngine.StopPlaylist(SelectedPlaylist);
    }
}
