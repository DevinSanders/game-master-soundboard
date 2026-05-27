using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Audio;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using SoundBoard.UI.Services;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Root view model for the main application window. Holds singleton
/// references to every secondary window's view model (so the "open X"
/// commands always activate the same instance), the Now Playing bar's
/// transport state (global pause aggregate, solo-playlist prev/next), and
/// the visualizer's audio-samples source.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public LibraryViewModel LibraryViewModel { get; }
    public ShortcutsViewModel ShortcutsViewModel { get; }
    public MixerViewModel MixerViewModel { get; }
    private readonly Func<BusMixerViewModel> _busMixerFactory;
    public PlaylistsViewModel PlaylistsViewModel { get; }
    public PresetsViewModel PresetsViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public AboutViewModel AboutViewModel { get; }
    private readonly Func<UriBuilderViewModel> _uriBuilderFactory;
    private ViewModelBase _currentViewModel;
    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        set => SetProperty(ref _currentViewModel, value);
    }

    private readonly IWindowManagerService _windowManager;
    private readonly IAudioPlaybackEngine _playbackEngine;

    public IRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand OpenLibraryCommand { get; }
    public IRelayCommand OpenPlaylistsCommand { get; }
    public IRelayCommand OpenPresetsCommand { get; }
    public IRelayCommand OpenMixerCommand { get; }
    public IRelayCommand OpenBusMixerCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand OpenAboutCommand { get; }
    public IRelayCommand OpenUriBuilderCommand { get; }

    public ObservableCollection<PlayingTrackViewModel> ActiveTracks => _playbackEngine.ActiveTracks;
    public ObservableCollection<IActiveMixerItem> ActiveItems => _playbackEngine.ActiveItems;

    /// <summary>True when anything (track, preset, or playlist-driven item)
    /// is in the mixer — drives Now Playing bar visibility. The older
    /// <c>HasActiveTracks</c> only counted tracks, which hid the bar for
    /// preset-only sessions.</summary>
    public bool HasActiveItems => ActiveItems.Count > 0;

    /// <summary>Wraps the master mixer's output as an IAudioSamples so the
    /// Now Playing bar's visualizer can render the final mix (all tracks +
    /// presets combined, after plugin DSP).</summary>
    public IAudioSamples MasterOutput { get; }

    // ── Now Playing transport state ──────────────────────────────────────
    // GlobalIsPaused is "true" only when EVERY active item is paused. Setting
    // it cascades to every item; reading it reflects the aggregate. _isBulkSet
    // suppresses the recompute that would otherwise fire mid-cascade and
    // flip the flag back to "false" between item updates.

    private bool _globalIsPaused;
    private bool _isBulkSet;

    public bool GlobalIsPaused
    {
        get => _globalIsPaused;
        set
        {
            if (_globalIsPaused == value) return;
            _isBulkSet = true;
            try
            {
                _globalIsPaused = value;
                foreach (var item in ActiveItems) item.IsPaused = value;
            }
            finally { _isBulkSet = false; }
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlobalPlayPauseText));
        }
    }

    public string GlobalPlayPauseText => GlobalIsPaused ? "▶ Resume all" : "⏸ Pause all";

    /// <summary>Exactly-one-playlist guard. The Now Playing bar shows prev/next
    /// buttons only when a single playlist is the running playlist context —
    /// surfacing prev/next when multiple playlists are active would be
    /// ambiguous about which one they target.</summary>
    [ObservableProperty]
    private PlayingPlaylistViewModel? _soloPlaylist;

    public bool HasSoloPlaylist => SoloPlaylist != null;

    partial void OnSoloPlaylistChanged(PlayingPlaylistViewModel? value)
        => OnPropertyChanged(nameof(HasSoloPlaylist));

    [RelayCommand]
    private void StopAllPlayback() => _playbackEngine.StopAll();

    [RelayCommand]
    private void PlaylistSkipForward()
    {
        if (SoloPlaylist != null) _playbackEngine.SkipPlaylistForward(SoloPlaylist.Playlist);
    }

    [RelayCommand]
    private void PlaylistSkipBackward()
    {
        if (SoloPlaylist != null) _playbackEngine.SkipPlaylistBackward(SoloPlaylist.Playlist);
    }

#pragma warning disable CS8618
    public MainWindowViewModel() { }
#pragma warning restore CS8618

    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ShortcutsViewModel shortcutsViewModel,
        MixerViewModel mixerViewModel,
        PlaylistsViewModel playlistsViewModel,
        PresetsViewModel presetsViewModel,
        SettingsViewModel settingsViewModel,
        AboutViewModel aboutViewModel,
        IWindowManagerService windowManager,
        IAudioPlaybackEngine playbackEngine,
        Func<UriBuilderViewModel> uriBuilderFactory,
        Func<BusMixerViewModel> busMixerFactory,
        MasterMixer masterMixer)
    {
        LibraryViewModel = libraryViewModel;
        ShortcutsViewModel = shortcutsViewModel;
        MixerViewModel = mixerViewModel;
        PlaylistsViewModel = playlistsViewModel;
        PresetsViewModel = presetsViewModel;
        SettingsViewModel = settingsViewModel;
        AboutViewModel = aboutViewModel;
        _windowManager = windowManager;
        _playbackEngine = playbackEngine;
        _uriBuilderFactory = uriBuilderFactory;
        _busMixerFactory = busMixerFactory;
        MasterOutput = new MasterOutputSource(masterMixer);

        _currentViewModel = shortcutsViewModel; // Main window always shows Soundboard

        NavigateCommand = new RelayCommand<string>(Navigate!);
        OpenLibraryCommand = new RelayCommand(() => _windowManager.ShowWindow(LibraryViewModel, "Library Manager", 1000, 700));
        OpenPlaylistsCommand = new RelayCommand(() => _windowManager.ShowWindow(PlaylistsViewModel, "Playlist Manager", 900, 600));
        OpenPresetsCommand = new RelayCommand(() => _windowManager.ShowWindow(PresetsViewModel, "Presets", 700, 600));
        OpenMixerCommand = new RelayCommand(() => _windowManager.ShowWindow(MixerViewModel, "Main Mixer", 1100, 750));
        // Bus Mixer is keyed so a second click focuses the existing window
        // instead of stacking duplicates. Factory-resolved each time the
        // window is first opened so the bus list is fresh (the Settings →
        // Buses page mutates the table and emits a reload signal).
        OpenBusMixerCommand = new RelayCommand(() =>
            _windowManager.ShowWindow(_busMixerFactory(), "bus-mixer", "Bus Mixer", 700, 480));
        OpenSettingsCommand = new RelayCommand(() => _windowManager.ShowWindow(SettingsViewModel, "Application Settings", 800, 600));
        OpenAboutCommand = new RelayCommand(() => _windowManager.ShowWindow(AboutViewModel, "About", 700, 500));
        OpenUriBuilderCommand = new RelayCommand(() =>
            _windowManager.ShowWindow(_uriBuilderFactory(), "URI Builder", 1000, 700));

        ActiveItems.CollectionChanged += OnActiveItemsChanged;
        // Seed for items that already exist (rare during normal startup but
        // possible if the engine pre-populates).
        foreach (var item in ActiveItems)
        {
            if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnActiveItemPropertyChanged;
        }
        RecomputeGlobalPauseState();
        RecomputeSoloPlaylist();
    }

    private void OnActiveItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnActiveItemPropertyChanged;
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged -= OnActiveItemPropertyChanged;
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // CollectionChanged.Reset doesn't carry old items, so we can't
            // unsubscribe individually — accept a small leak in the unlikely
            // case the engine ever calls Clear(). Re-subscribe all current.
            foreach (var item in ActiveItems)
                if (item is INotifyPropertyChanged inpc)
                {
                    inpc.PropertyChanged -= OnActiveItemPropertyChanged;
                    inpc.PropertyChanged += OnActiveItemPropertyChanged;
                }
        }

        OnPropertyChanged(nameof(HasActiveItems));
        RecomputeGlobalPauseState();
        RecomputeSoloPlaylist();
    }

    private void OnActiveItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkSet) return;
        if (e.PropertyName == nameof(IActiveMixerItem.IsPaused))
            RecomputeGlobalPauseState();
    }

    private void RecomputeGlobalPauseState()
    {
        bool allPaused = ActiveItems.Count > 0 && ActiveItems.All(i => i.IsPaused);
        if (_globalIsPaused == allPaused) return;
        _globalIsPaused = allPaused;
        OnPropertyChanged(nameof(GlobalIsPaused));
        OnPropertyChanged(nameof(GlobalPlayPauseText));
    }

    private void RecomputeSoloPlaylist()
    {
        var playlists = ActiveItems.OfType<PlayingPlaylistViewModel>().ToList();
        SoloPlaylist = playlists.Count == 1 ? playlists[0] : null;
    }

    [RelayCommand]
    public void OpenUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* Fallback for some platforms */ }
    }

    private void Navigate(string viewName)
    {
        // For views that are now windows, just open them
        switch (viewName)
        {
            case "Library": OpenLibraryCommand.Execute(null); break;
            case "Playlists": OpenPlaylistsCommand.Execute(null); break;
            case "Presets": OpenPresetsCommand.Execute(null); break;
            case "Mixer": OpenMixerCommand.Execute(null); break;
            case "BusMixer": OpenBusMixerCommand.Execute(null); break;
            case "Settings": OpenSettingsCommand.Execute(null); break;
            case "About": OpenAboutCommand.Execute(null); break;
            default:
                CurrentViewModel = ShortcutsViewModel;
                break;
        }
    }
}
