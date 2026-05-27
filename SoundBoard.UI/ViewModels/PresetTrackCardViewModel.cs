using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using System;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// One card in the Preset Editor — wraps a single <see cref="PresetTrack"/>
/// and exposes its overrides as nullable-friendly doubles so sliders can
/// drive them.
///
/// Setting an override slider to 0 leaves the override null (= inherit
/// Track default). Any positive value writes a non-null override. The
/// IsLooping toggle has its own explicit "Override?" toggle since false is
/// a meaningful override value.
///
/// Setters mutate the model + live audio immediately, then schedule a
/// debounced save through the parent's <see cref="EditPersistence"/>. A
/// slider drag therefore moves audio in real time and only writes one DB
/// row when the user releases the thumb.
/// </summary>
public partial class PresetTrackCardViewModel : ViewModelBase
{
    private readonly PresetEditorViewModel? _parent;
    private readonly ISoundBoardDbContextFactory? _dbFactory;
    private readonly EditPersistence? _persistence;

    public PresetTrack Entry { get; }

    public string TrackName => Entry.Track?.Name ?? "<missing track>";

    public double EffectiveStartSeconds => Entry.EffectiveStartPoint?.TotalSeconds ?? 0;
    public double EffectiveEndSeconds   => Entry.EffectiveEndPoint?.TotalSeconds ?? 0;

    /// <summary>"m:ss" of how long this card will actually play, taking the
    /// effective StartPoint/EndPoint (preset override → library default) into
    /// account. Returns "—" when the file duration hasn't been cached.</summary>
    public string LengthDisplay
    {
        get
        {
            var start = Entry.EffectiveStartPoint ?? System.TimeSpan.Zero;
            var end = Entry.EffectiveEndPoint ?? Entry.Track?.FileDuration;
            if (!end.HasValue) return "—";
            var len = end.Value - start;
            if (len < System.TimeSpan.Zero) len = System.TimeSpan.Zero;
            return SoundBoard.UI.Converters.DurationDisplayConverter.Format(len);
        }
    }

    public double OverrideVolume
    {
        get => Entry.OverrideVolume ?? Entry.Track?.Volume ?? 1.0;
        set
        {
            Entry.OverrideVolume = (float)value;
            ApplyLive();
            SchedulePersist();
            OnPropertyChanged();
        }
    }

    public double OverrideFadeInSeconds
    {
        get => Entry.OverrideFadeIn?.TotalSeconds ?? Entry.Track?.FadeInDuration.TotalSeconds ?? 0;
        set
        {
            Entry.OverrideFadeIn = TimeSpan.FromSeconds(value);
            // FadeIn only fires at start — already past for an in-flight child.
            SchedulePersist();
            OnPropertyChanged();
        }
    }

    public double OverrideFadeOutSeconds
    {
        get => Entry.OverrideFadeOut?.TotalSeconds ?? Entry.Track?.FadeOutDuration.TotalSeconds ?? 0;
        set
        {
            Entry.OverrideFadeOut = TimeSpan.FromSeconds(value);
            ApplyLive();
            SchedulePersist();
            OnPropertyChanged();
        }
    }

    public double OverrideStartDelaySeconds
    {
        get => Entry.OverrideStartDelay?.TotalSeconds ?? Entry.Track?.StartDelay.TotalSeconds ?? 0;
        set
        {
            Entry.OverrideStartDelay = TimeSpan.FromSeconds(value);
            ApplyLive();
            SchedulePersist();
            OnPropertyChanged();
        }
    }

    public bool IsLooping
    {
        get => Entry.OverrideIsLooping ?? Entry.Track?.IsLooping ?? false;
        set
        {
            Entry.OverrideIsLooping = value;
            ApplyLive();
            SchedulePersist();
            OnPropertyChanged();
        }
    }

    public PresetTrackCardViewModel(PresetTrack entry, PresetEditorViewModel? parent = null, ISoundBoardDbContextFactory? dbFactory = null, EditPersistence? persistence = null)
    {
        Entry = entry;
        _parent = parent;
        _dbFactory = dbFactory;
        _persistence = persistence;
    }

    /// <summary>Schedule a single save closure for this card's row. Keyed
    /// by entry id so rapid slider ticks coalesce into one DB write.
    /// Reads the override columns at flush time so the closure always
    /// persists the *current* in-memory values.</summary>
    private void SchedulePersist()
    {
        if (_persistence == null || _dbFactory == null) return;
        _persistence.Schedule($"PresetTrack:{Entry.Id}", () =>
            _dbFactory.EditorSave<Core.Models.PresetTrack>(Entry.Id, tracked =>
            {
                tracked.OverrideVolume = Entry.OverrideVolume;
                tracked.OverrideFadeIn = Entry.OverrideFadeIn;
                tracked.OverrideFadeOut = Entry.OverrideFadeOut;
                tracked.OverrideStartDelay = Entry.OverrideStartDelay;
                tracked.OverrideIsLooping = Entry.OverrideIsLooping;
            }));
    }

    /// <summary>Forward this card's current override values to the running
    /// child provider, if a live playback exists. No-op when the preset
    /// isn't currently playing.</summary>
    private void ApplyLive()
    {
        var live = _parent?.LivePreset;
        if (live == null) return;
        live.ApplyLiveSettings(
            presetTrackId: Entry.Id,
            baseVolume: (float)OverrideVolume,
            isLooping: IsLooping,
            startDelay: TimeSpan.FromSeconds(OverrideStartDelaySeconds),
            fadeOut: TimeSpan.FromSeconds(OverrideFadeOutSeconds));
    }
}
