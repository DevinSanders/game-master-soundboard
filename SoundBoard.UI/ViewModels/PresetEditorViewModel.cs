using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.UI.Messages;
using SoundBoard.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Edits one Preset — a soundboard-style grid of cards, one per
/// <see cref="PresetTrack"/>. The same Track may appear multiple times with
/// independent overrides. Save persists every override + the Preset.Name.
/// </summary>
public partial class PresetEditorViewModel : ViewModelBase, IDisposable
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;
    private readonly ISamplerChainService _samplerChain;
    private readonly IPluginService _pluginService;
    private readonly IWindowManagerService _windowManager;
    private readonly ISamplerLauncherService _samplerLauncher;

    /// <summary>Debounced save coordinator. Shared with the per-card VMs so
    /// a slider drag anywhere in the editor only writes once to SQLite.</summary>
    public EditPersistence Persistence { get; } = new();

    public ObservableCollection<PresetTrackCardViewModel> Cards { get; } = new();

    [ObservableProperty]
    private Preset? _preset;

    public string PresetName
    {
        get => Preset?.Name ?? "";
        set
        {
            if (Preset != null && Preset.Name != value)
            {
                Preset.Name = value;
                SchedulePresetSave();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Observable shim over <see cref="Preset.Icon"/> — Preset is a POCO
    /// so a direct binding never fires PropertyChanged on icon changes. The
    /// editor's icon swatch binds to this instead.</summary>
    public string? PresetIcon
    {
        get => Preset?.Icon;
        set
        {
            if (Preset != null && Preset.Icon != value)
            {
                Preset.Icon = value;
                SchedulePresetSave();
                OnPropertyChanged();
            }
        }
    }

    // ── Bus override ────────────────────────────────────────────────
    //
    // A preset can optionally force every track it spawns onto one bus
    // regardless of each track's own routing. The dropdown shows
    // "(Inherit from track)" as the null option plus every configured
    // bus. Use case: a "Combat Ambience" preset whose tracks normally
    // route to Music but should route to Ambient when this preset plays.

    /// <summary>Snapshot of every bus available for override. Inherit is
    /// modelled as a synthetic entry with <c>Id = null</c>.</summary>
    public ObservableCollection<BusChoice> AvailableBusChoices { get; } = new();

    /// <summary>Currently selected override id (null = inherit from track).</summary>
    public int? PresetBusIdOverride
    {
        get => Preset?.BusIdOverride;
        set
        {
            if (Preset == null || Preset.BusIdOverride == value) return;
            Preset.BusIdOverride = value;
            SchedulePresetSave();
            OnPropertyChanged();
        }
    }

    private void LoadAvailableBusChoices()
    {
        AvailableBusChoices.Clear();
        // Inherit-from-track lives at the top of the dropdown as the
        // explicit "no override" choice.
        AvailableBusChoices.Add(new BusChoice(null, "(Inherit from track)"));
        using var db = _dbFactory.CreateDbContext();
        foreach (var bus in db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id))
            AvailableBusChoices.Add(new BusChoice(bus.Id, bus.Name));
    }

    private void SchedulePresetSave()
    {
        if (Preset == null) return;
        var id = Preset.Id;
        Persistence.Schedule($"Preset:{id}", () =>
        {
            if (Preset == null) return;
            bool displayChanged = false;
            _dbFactory.EditorSave<Preset>(id, tracked =>
            {
                displayChanged = tracked.Name != Preset.Name || tracked.Icon != Preset.Icon;
                tracked.Name = Preset.Name;
                tracked.Icon = Preset.Icon;
                tracked.BusIdOverride = Preset.BusIdOverride;
            });

            if (displayChanged)
            {
                // Same fan-out pattern as TrackEditor's ScheduleSave: emit
                // PresetItemsChangedMessage so any open window with a
                // preset name/icon binding (Presets list, Playlist editor
                // rows that reference this preset, Shortcuts that show
                // a preset label) reloads. Cheap; emitted only on real
                // user-visible mutations, not on every debounced tick.
                WeakReferenceMessenger.Default
                    .Send(new Messages.PresetItemsChangedMessage(id));
            }
        });
    }

    /// <summary>Currently-playing instance of this preset, if any. Card sliders
    /// use it to push live setting changes through to the audio thread.</summary>
    public PlayingPresetViewModel? LivePreset { get; private set; }

    // Captured as a field so Dispose can unhook. Pre-fix this was a
    // lambda-only registration; every "edit a different preset" swap
    // leaked a handler rooted on the engine singleton.
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _activeItemsChanged;

    public PresetEditorViewModel(ISoundBoardDbContextFactory dbFactory, IAudioPlaybackEngine playbackEngine,
        ISamplerChainService samplerChain, IPluginService pluginService, IWindowManagerService windowManager,
        ISamplerLauncherService samplerLauncher)
    {
        _samplerLauncher = samplerLauncher;
        _dbFactory = dbFactory;
        _playbackEngine = playbackEngine;
        _samplerChain = samplerChain;
        _pluginService = pluginService;
        _windowManager = windowManager;
        _activeItemsChanged = (s, e) => RefreshLivePreset();
        _playbackEngine.ActiveItems.CollectionChanged += _activeItemsChanged;
    }

    public void Dispose()
    {
        if (_activeItemsChanged != null)
        {
            _playbackEngine.ActiveItems.CollectionChanged -= _activeItemsChanged;
            _activeItemsChanged = null;
        }
        // Flush any pending debounced writes before the editor goes away.
        try { Persistence.Flush(); } catch { /* never throw from Dispose */ }
    }

    [RelayCommand]
    private void OpenFxChain()
    {
        if (Preset == null) return;
        _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Preset, Preset.Id, Preset.Name ?? "");
    }

    private void RefreshLivePreset()
    {
        var live = _playbackEngine.ActiveItems
            .OfType<PlayingPresetViewModel>()
            .FirstOrDefault(p => Preset != null && p.Preset.Id == Preset.Id);
        if (!ReferenceEquals(live, LivePreset))
        {
            LivePreset = live;
            OnPropertyChanged(nameof(LivePreset));
        }
    }

    partial void OnPresetChanged(Preset? value) => RefreshLivePreset();

    public void LoadPreset(int presetId)
    {
        // If the editor VM is reused (e.g. opened on Preset A then
        // re-pointed at Preset B via type-keyed window dedupe — a path
        // that should be obsolete now that PresetsViewModel uses per-id
        // window keys, but defensively flush anyway), commit anything
        // still in the debouncer for the previous preset before its
        // shim properties are blown away by the new load.
        try { Persistence.Flush(); } catch { /* swallow — Load must always run */ }

        using var db = _dbFactory.CreateDbContext();
        var loaded = db.Presets
            .AsNoTracking()
            .Include(p => p.Tracks.OrderBy(t => t.Order))
                .ThenInclude(t => t.Track)
            .FirstOrDefault(p => p.Id == presetId);

        Preset = loaded;
        Cards.Clear();
        if (loaded != null)
        {
            foreach (var entry in loaded.Tracks.OrderBy(t => t.Order))
                Cards.Add(new PresetTrackCardViewModel(entry, this, _dbFactory, Persistence));
        }
        LoadAvailableBusChoices();
        OnPropertyChanged(nameof(PresetName));
        OnPropertyChanged(nameof(PresetIcon));
        OnPropertyChanged(nameof(PresetBusIdOverride));
    }

    /// <summary>One entry in the bus-override dropdown. Backed by a record
    /// rather than the raw <see cref="Bus"/> so the "(Inherit)" null
    /// option can sit alongside real bus rows with the same key/label
    /// shape — the ComboBox uses <c>SelectedValuePath="Id"</c> and
    /// <c>DisplayMemberBinding="Label"</c>.</summary>
    public sealed record BusChoice(int? Id, string Label);

    /// <summary>Called by the view's drop handler when a Track is dragged in
    /// from the Library. Allowed to be the same Track multiple times.</summary>
    public void AddTrack(Track track)
    {
        if (Preset == null) return;
        using var db = _dbFactory.CreateDbContext();

        var entry = new PresetTrack
        {
            PresetId = Preset.Id,
            TrackId = track.Id,
            Order = Cards.Count
        };
        db.PresetTracks.Add(entry);
        db.SaveChanges();

        // Re-fetch the navigation prop so the card can show the track name.
        entry.Track = db.Tracks.AsNoTracking().FirstOrDefault(t => t.Id == track.Id);
        Preset.Tracks.Add(entry);
        Cards.Add(new PresetTrackCardViewModel(entry, this, _dbFactory, Persistence));
        WeakReferenceMessenger.Default.Send(new PresetItemsChangedMessage(Preset.Id));
    }

    [RelayCommand]
    private void RemoveCard(PresetTrackCardViewModel? card)
    {
        if (card == null || Preset == null) return;
        using var db = _dbFactory.CreateDbContext();

        // Find-pattern: attaching the detached card.Entry would drag its
        // Track nav property into the tracker and collide with the Find
        // calls used to renumber the remaining rows below.
        var tracked = db.PresetTracks.Find(card.Entry.Id);
        if (tracked != null) db.PresetTracks.Remove(tracked);
        Preset.Tracks.Remove(card.Entry);
        Cards.Remove(card);

        // Renumber remaining cards: load each as a tracked entity and set
        // the Order column directly. No Update() calls.
        for (int i = 0; i < Cards.Count; i++)
        {
            Cards[i].Entry.Order = i;
            var trackedRow = db.PresetTracks.Find(Cards[i].Entry.Id);
            if (trackedRow != null) trackedRow.Order = i;
        }
        db.SaveChanges();
        WeakReferenceMessenger.Default.Send(new PresetItemsChangedMessage(Preset.Id));
    }

    /// <summary>Called from the view during drag-reorder. Moves
    /// <paramref name="source"/> adjacent to <paramref name="target"/> and
    /// rewrites <see cref="PresetTrack.Order"/> so playback order matches the
    /// visual order.</summary>
    public void MoveCard(PresetTrackCardViewModel source, PresetTrackCardViewModel target)
    {
        if (source == target) return;
        int s = Cards.IndexOf(source);
        int t = Cards.IndexOf(target);
        if (s < 0 || t < 0) return;

        Cards.Move(s, t);
        for (int i = 0; i < Cards.Count; i++) Cards[i].Entry.Order = i;
    }

    public void PersistOrder()
    {
        if (Cards.Count == 0) return;
        using var db = _dbFactory.CreateDbContext();
        foreach (var card in Cards)
        {
            var tracked = db.PresetTracks.Find(card.Entry.Id);
            if (tracked != null) tracked.Order = card.Entry.Order;
        }
        db.SaveChanges();
    }

    public void SetIcon(string? icon)
    {
        PresetIcon = icon;
    }

    [RelayCommand]
    private void PlayPreview()
    {
        if (Preset == null) return;
        // Flush any pending debounced edits so the engine sees current overrides.
        Persistence.Flush();
        _playbackEngine.PlayPreset(Preset);
    }

    [RelayCommand]
    private void StopPreview()
    {
        if (Preset == null) return;
        _playbackEngine.StopPreset(Preset);
    }
}
