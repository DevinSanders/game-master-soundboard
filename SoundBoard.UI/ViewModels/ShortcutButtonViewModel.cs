using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// One button on the main-window soundboard grid. Routes clicks to the
/// playback engine's toggle method for its target type (track / preset /
/// playlist) and reflects the target's playing/paused state via the outline
/// color. Listens to per-item PropertyChanged so the outline stays in sync
/// when pause is toggled from elsewhere (mixer card, Now Playing bar).
/// </summary>
public partial class ShortcutButtonViewModel : ViewModelBase, IDisposable
{
    private readonly ShortcutButton _model;
    private readonly IAudioPlaybackEngine _playbackEngine;

    public ShortcutButton ButtonModel => _model;
    public int ModelId => _model.Id;
    public string? Label => _model.Label ?? _model.Track?.Name ?? _model.Preset?.Name ?? _model.Playlist?.Name ?? "Empty";
    public string? ImagePath => _model.ImagePath;

    /// <summary>RPG Awesome icon for this button. Falls through to the linked
    /// track / preset / playlist's icon if the button itself doesn't set one.</summary>
    public string? EffectiveIcon =>
        _model.Icon ?? _model.Track?.Icon ?? _model.Preset?.Icon ?? _model.Playlist?.Icon;

    public bool HasIcon => !string.IsNullOrEmpty(EffectiveIcon);

    /// <summary>Per-button icon color hex, or null to inherit the theme.</summary>
    public string? IconColor => _model.IconColor;

    /// <summary>Per-button background color hex, or null to inherit the theme.</summary>
    public string? ButtonColor => _model.ButtonColor;

    private bool HasCustomButtonColor => !string.IsNullOrWhiteSpace(_model.ButtonColor);

    /// <summary>Brush the icon glyph renders with: the per-button override
    /// when set, otherwise the theme's TextPrimary. Resolved at build time
    /// (the button VM is rebuilt whenever the page reloads).</summary>
    public IBrush IconBrush => ParseOr(_model.IconColor, "TextPrimary", Colors.White);

    /// <summary>Brush the button paints its background with: the per-button
    /// override when set, otherwise the theme's PanelBackground3.</summary>
    public IBrush ButtonBrush => ParseOr(_model.ButtonColor, "PanelBackground3", Color.FromRgb(0x33, 0x41, 0x55));

    // When the label sits over an icon or a custom button color the theme's
    // text/surface contrast can't be assumed, so it renders as white text
    // with a dark outline + drop shadow (legible over any backdrop) instead
    // of covering the icon with a scrim. Plain text-only buttons keep the
    // theme text color with no outline/shadow, so the soundboard reads
    // unchanged.
    private bool NeedsLegibilityHelp => HasIcon || HasCustomButtonColor;

    /// <summary>Label fill color.</summary>
    public IBrush LabelForeground =>
        NeedsLegibilityHelp ? Brushes.White : (SafeResolve("TextPrimary") ?? Brushes.White);

    /// <summary>Label outline (glyph stroke) color, or transparent for plain
    /// text buttons (no visible outline).</summary>
    public IBrush LabelOutline =>
        NeedsLegibilityHelp ? new SolidColorBrush(Color.FromArgb(0xDD, 0, 0, 0)) : Brushes.Transparent;

    /// <summary>Label drop-shadow color, or transparent for plain buttons.</summary>
    public IBrush LabelShadow =>
        NeedsLegibilityHelp ? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) : Brushes.Transparent;

    private static IBrush ParseOr(string? hex, string themeKey, Color hardFallback)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c))
            return new SolidColorBrush(c);
        return SafeResolve(themeKey) ?? new SolidColorBrush(hardFallback);
    }

    // These getters run during XAML binding and must never throw. The theme
    // lookup touches Application.Current.Resources; guard it so an odd
    // resource/thread state can't take down the render.
    private static IBrush? SafeResolve(string key)
    {
        try { return ThemeBrushes.Resolve(key); }
        catch { return null; }
    }

    /// <summary>True when this button targets a <see cref="Track"/>.
    /// The bus-override menu item is hidden for Preset / Playlist
    /// shortcuts (those defer to their target's own routing — Playlists
    /// never override per the design spec; Presets carry their own
    /// override).</summary>
    public bool IsTrackTarget => _model.TrackId.HasValue;

    [ObservableProperty]
    private IBrush _outlineBrush = Brushes.Transparent;

    public ShortcutButtonViewModel(ShortcutButton model, IAudioPlaybackEngine playbackEngine)
    {
        _model = model;
        _playbackEngine = playbackEngine;

        _playbackEngine.ActiveItems.CollectionChanged += OnActiveItemsChanged;
        // Also subscribe to existing items so pause toggles done elsewhere
        // (mixer card, Now Playing bar) keep the outline in sync.
        foreach (var item in _playbackEngine.ActiveItems)
            if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnActiveItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged -= OnItemPropertyChanged;
        UpdateOutline();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IActiveMixerItem.IsPaused))
            UpdateOutline();
    }

    public void UpdateOutline()
    {
        bool isPlaying = false;
        bool isPaused = false;

        if (_model.TrackId.HasValue && _model.Track != null)
        {
            isPlaying = _playbackEngine.IsTrackPlaying(_model.Track);
            isPaused = _playbackEngine.IsTrackPaused(_model.Track);
        }
        else if (_model.PresetId.HasValue && _model.Preset != null)
        {
            isPlaying = _playbackEngine.IsPresetPlaying(_model.Preset);
            isPaused = _playbackEngine.IsPresetPaused(_model.Preset);
        }
        else if (_model.PlaylistId.HasValue && _model.Playlist != null)
        {
            isPaused = _playbackEngine.IsPlaylistPaused(_model.Playlist);
            isPlaying = !isPaused && _playbackEngine.IsPlaylistRunning(_model.Playlist);
        }

        if (isPlaying) OutlineBrush = Brushes.LightGreen;
        else if (isPaused) OutlineBrush = Brushes.Yellow;
        else OutlineBrush = Brushes.Transparent;
    }

    [RelayCommand]
    private void Click()
    {
        // Shortcuts only own their own sampler chain when they point at a
        // single library Track — Tracks have no per-target chain by design,
        // so the shortcut is the natural place to hang DSP on them.
        //
        // For Preset / Playlist targets the target ALREADY has its own
        // sampler chain (managed via the preset / playlist editor). We
        // skip the shortcut layering entirely — otherwise the user would
        // have two places to attach effects for the same audio and the
        // mental model would get muddy. The Configure-samplers menu item
        // on these shortcuts opens the target's editor instead.
        if (_model.TrackId.HasValue && _model.Track != null)
        {
            _playbackEngine.TogglePlayPause(_model.Track, _model.Id);
        }
        else if (_model.PresetId.HasValue && _model.Preset != null)
        {
            _playbackEngine.TogglePlayPausePreset(_model.Preset);
        }
        else if (_model.PlaylistId.HasValue && _model.Playlist != null)
        {
            _playbackEngine.TogglePlayPausePlaylist(_model.Playlist);
        }
        UpdateOutline();
    }

    private bool _disposed;

    /// <summary>Unsubscribe from the singleton playback engine's events. Must
    /// be called when the button is removed from the page (or the user
    /// switches pages) — otherwise the engine retains a reference to this
    /// VM forever via its event delegate list, leaking orphans every page
    /// switch.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playbackEngine.ActiveItems.CollectionChanged -= OnActiveItemsChanged;
        foreach (var item in _playbackEngine.ActiveItems)
            if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged -= OnItemPropertyChanged;
    }
}
