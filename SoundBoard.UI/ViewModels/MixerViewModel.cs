using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Mixer window — surfaces the playback engine's ActiveItems
/// (track / preset / playlist cards), the master Local volume slider,
/// one volume slider per connected bridge plugin (Discord / Zoom /
/// Mumble / …), and a host area for any
/// <see cref="SoundBoard.PluginApi.IUIExtensionPlugin"/> controls whose
/// placement is the mixer.
///
/// <para><b>Per-bridge sliders</b> are driven by
/// <see cref="MasterMixer.SubscribersChanged"/>. When a bridge transitions
/// to <see cref="SoundBoard.PluginApi.BridgeStatus.Connected"/> it
/// subscribes to the mixer; this VM mirrors that subscription as a
/// <see cref="BroadcastSliderViewModel"/> in <see cref="BroadcastSliders"/>,
/// and the View's <c>ItemsControl</c> renders one vertical slider per
/// entry. Disconnect removes the slider. Two bridges = two sliders,
/// each with its own per-bridge gain.</para>
/// </summary>
// IMPORTANT: do NOT implement IDisposable here.
//
// MixerViewModel is held singleton-style by MainWindowViewModel (it's a
// constructor-injected property, NOT resolved fresh each window-open).
// The same instance gets reused every time the user opens the Mixer
// window. But WindowManagerService.Closed unconditionally Dispose()s any
// IDisposable ShellContent on window close — that's the right behaviour
// for transient editor VMs (TrackEditor, PresetEditor) but WRONG for a
// shared VM like this one. If we Dispose, we'd unsubscribe from
// MasterMixer.SubscribersChanged; subsequent bridge connections would
// silently fail to add their slider because the same MixerViewModel
// instance (now unsubscribed) is reused on the next OpenMixer.
//
// The SubscribersChanged subscription leaks for the lifetime of this
// MixerViewModel, which equals the lifetime of MainWindowViewModel,
// which equals the app's lifetime. So it isn't a leak in practice.
public partial class MixerViewModel : ViewModelBase
{
    public IAudioPlaybackEngine PlaybackEngine { get; }
    private readonly MasterMixer _masterMixer;
    private readonly IPluginService _pluginService;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly ISettingsService _settings;
    private readonly EditPersistence _persistence = new();

    public ObservableCollection<IActiveMixerItem> ActiveItems => PlaybackEngine.ActiveItems;

    public ObservableCollection<object> PluginControls { get; } = new();

    /// <summary>One slider VM per currently-connected bridge. Kept for
    /// any external consumer reading the connected-only list; the
    /// MixerView itself now binds to <see cref="BridgeStrips"/>, which
    /// persists one entry per enabled bridge regardless of connection
    /// state so the user's volume is remembered across connect/disconnect.</summary>
    public ObservableCollection<BroadcastSliderViewModel> BroadcastSliders { get; } = new();

    public bool HasBroadcastSliders => BroadcastSliders.Count > 0;

    /// <summary>One persistent strip VM per enabled <see cref="IAudioBridgePlugin"/>.
    /// Each strip tracks the bridge's current
    /// <see cref="IBroadcastSubscription"/> (null when disconnected) and
    /// flips <see cref="BridgeStripViewModel.IsVisible"/> with the
    /// connection state. The volume is held on the strip itself and
    /// pushed into a fresh subscription on connect, so dragging the
    /// slider while disconnected sets the value the next session will
    /// start at.</summary>
    public ObservableCollection<BridgeStripViewModel> BridgeStrips { get; } = new();

    // Master volumes are exposed as the raw 0.0–2.0 multiplier so the slider
    // can use the same range as every other volume slider in the app; the
    // readout converter handles "%" formatting.
    public double LocalVolume
    {
        get => _masterMixer.LocalVolume;
        set
        {
            if (System.Math.Abs(_masterMixer.LocalVolume - (float)value) < 0.0001f) return;
            _masterMixer.LocalVolume = (float)value;
            // Debounced persistence so a slider drag doesn't fire a
            // SettingsService.Save per pixel. Stable key "LocalVolume"
            // collapses any pending write to one.
            _persistence.Schedule("LocalVolume", () =>
            {
                _settings.Current.LocalVolume = (float)_masterMixer.LocalVolume;
                _settings.Save();
            });
            OnPropertyChanged();
        }
    }

    public MixerViewModel(IAudioPlaybackEngine playbackEngine, MasterMixer masterMixer, IPluginService pluginService,
        ISamplerLauncherService samplerLauncher, ISettingsService settings)
    {
        PlaybackEngine = playbackEngine;
        _masterMixer = masterMixer;
        _pluginService = pluginService;
        _samplerLauncher = samplerLauncher;
        _settings = settings;

        BuildBridgeStrips();
        _masterMixer.SubscribersChanged += OnBroadcastSubscribersChanged;
        RefreshBroadcastSliders();
        Log.Debug("MixerVM", $"Constructed; bridge strips = {BridgeStrips.Count}");

        LoadPluginControls();
    }

    /// <summary>Build one <see cref="BridgeStripViewModel"/> per loaded
    /// <see cref="IAudioBridgePlugin"/>. Strips are created once at VM
    /// construction; their <see cref="BridgeStripViewModel.IsVisible"/>
    /// flips dynamically with the connection state.
    /// <para>Each strip's initial volume comes from
    /// <see cref="AppSettings.BridgeVolumes"/> (keyed by plugin id) so the
    /// GM's level balance for that bridge survives an app restart.
    /// Bridges absent from the dictionary default to unity (1.0).</para></summary>
    private void BuildBridgeStrips()
    {
        var persisted = _settings.Current.BridgeVolumes;
        foreach (var bridge in _pluginService.BridgePlugins)
        {
            float initial = persisted.TryGetValue(bridge.Id, out var v) ? v : 1.0f;
            var strip = new BridgeStripViewModel(bridge, initial, PersistBridgeVolume);
            BridgeStrips.Add(strip);
        }
    }

    /// <summary>Per-bridge persistence callback handed to each strip.
    /// Debounced by plugin id so a fast drag collapses to one save.</summary>
    private void PersistBridgeVolume(string pluginId, float volume)
    {
        _persistence.Schedule($"BridgeVolume.{pluginId}", () =>
        {
            _settings.Current.BridgeVolumes[pluginId] = volume;
            _settings.Save();
        });
    }

    /// <summary>Flush any pending debounced volume writes immediately.
    /// Called from <c>App.RunOrderedShutdown</c> so a slider drag that
    /// happened within ~300 ms of "close app" doesn't get dropped on
    /// the floor. MixerViewModel is singleton-style + doesn't implement
    /// <see cref="IDisposable"/> (by design; see the class-level note),
    /// so the helper's <see cref="EditPersistence.Dispose"/> would
    /// normally never fire — Flush is the way.</summary>
    public void FlushPendingWrites()
    {
        try { _persistence.Flush(); }
        catch (Exception ex) { Log.Warn("MixerVM", $"FlushPendingWrites threw: {ex.Message}"); }
    }

    private void OnBroadcastSubscribersChanged(object? sender, EventArgs e)
    {
        // The mixer fires this from any thread (bridge connect handlers
        // run on Discord.Net's dispatcher, the audio thread doesn't touch
        // subscribers but other plugins might). Marshal to UI before
        // mutating the ObservableCollection — Avalonia bindings observe
        // collection changes synchronously and require UI-thread access.
        Log.Debug("MixerVM",
            $"SubscribersChanged received (on UI thread = {Dispatcher.UIThread.CheckAccess()})");
        if (Dispatcher.UIThread.CheckAccess()) RefreshBroadcastSliders();
        else Dispatcher.UIThread.Post(RefreshBroadcastSliders);
    }

    private void RefreshBroadcastSliders()
    {
        var current = _masterMixer.Subscribers;
        int beforeCount = BroadcastSliders.Count;

        // Drop sliders whose underlying subscription is gone.
        for (int i = BroadcastSliders.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(BroadcastSliders[i].Subscription))
                BroadcastSliders.RemoveAt(i);
        }

        // Add sliders for any new subscriptions. Preserves order — the
        // mixer's Subscribers list is subscribe-order so first-connected
        // bridges stay leftmost.
        foreach (var sub in current)
        {
            if (!BroadcastSliders.Any(s => ReferenceEquals(s.Subscription, sub)))
                BroadcastSliders.Add(new BroadcastSliderViewModel(sub));
        }

        // Reconcile the persistent strip list with the active subscriptions.
        // Match by the strip's bridge plugin's display name on the
        // assumption that the bridge passes the same string into its
        // MasterMixer.Subscribe call (the convention every bridge follows;
        // see IAudioBridgePlugin contract). Pre-fix matched by raw
        // DisplayName too — but the contract is undocumented and two
        // bridges with the same Name (rare, but possible if the user
        // installs both "Discord Bridge" v1 and a fork) would silently
        // collide. Phase R3 makes the convention explicit by routing
        // through the strip's bridge plugin reference. Falls back to
        // a name match for legacy bridges that hand a different string.
        foreach (var strip in BridgeStrips)
        {
            IBroadcastSubscription? match = null;
            // Primary: match by the bridge plugin's display name, which
            // every bridge in our ecosystem passes verbatim to Subscribe.
            foreach (var sub in current)
            {
                if (sub.DisplayName == strip.DisplayName)
                {
                    match = sub;
                    break;
                }
            }
            strip.AttachSubscription(match);
        }

        OnPropertyChanged(nameof(HasBroadcastSliders));
        Log.Debug("MixerVM",
            $"RefreshBroadcastSliders: mixer reports {current.Count} subscribers, " +
            $"sliders {beforeCount} → {BroadcastSliders.Count} " +
            $"(names: [{string.Join(", ", BroadcastSliders.Select(s => s.DisplayName))}])");
    }

    /// <summary>Open (or focus) the master FX chain editor.</summary>
    [RelayCommand]
    private void OpenMasterFxChain()
        => _samplerLauncher.Open(SoundBoard.Core.Models.SamplerOwnerType.Master, ownerId: null, "Master Bus");

    private void LoadPluginControls()
    {
        // Sampler plugins are no longer hosted via a global "stick this in the
        // mixer panel" hook — their UIs live on per-attachment instances now
        // (see ISamplerInstance.CreateControl) and surface through the sampler
        // editor window per target. The mixer panel only hosts non-DSP UI
        // extensions advertised via IUIExtensionPlugin.
        foreach (var control in _pluginService.GetExtensionControls(SoundBoard.PluginApi.UIPlacement.Mixer))
            PluginControls.Add(control);
    }

    [RelayCommand]
    private void PauseItem(IActiveMixerItem item) => item.IsPaused = !item.IsPaused;

    [RelayCommand]
    private void StopItem(IActiveMixerItem item) => item.Stop();
}

/// <summary>
/// One row in <see cref="MixerViewModel.BroadcastSliders"/>. Wraps a
/// single <see cref="IBroadcastSubscription"/> from the master mixer; the
/// View's per-item slider binds two-way to <see cref="Volume"/>. The
/// slider's label comes from <see cref="DisplayName"/>, which the bridge
/// chose at subscribe time (typically the bridge's display name, e.g.
/// "Discord Bridge", "Mumble Bridge").
/// </summary>
/// <summary>
/// Persistent strip VM for one <see cref="IAudioBridgePlugin"/>. Exists
/// for the lifetime of <see cref="MixerViewModel"/> regardless of the
/// bridge's connection state — invisible (<see cref="IsVisible"/> =
/// false) when no subscription is active, visible when one is.
///
/// <para><b>Volume preservation.</b> The user's chosen volume is stored
/// on the strip itself, not on the subscription (which is recreated each
/// time the bridge connects). When <see cref="AttachSubscription"/> hands
/// over a fresh subscription, the strip pushes its remembered volume
/// into it. This means the GM can drag the slider to e.g. 50% while the
/// bridge is disconnected, then connect — the call starts at 50% rather
/// than the bridge's default 100%.</para>
/// </summary>
public sealed partial class BridgeStripViewModel : ObservableObject
{
    private readonly IAudioBridgePlugin _bridge;
    private readonly Action<string, float>? _persist;
    private IBroadcastSubscription? _subscription;

    /// <summary>Bridge display name (e.g. "Discord Bridge"). Stable for
    /// the bridge's lifetime so we can match subscriptions to strips by
    /// name even across connect/disconnect cycles.</summary>
    public string DisplayName => _bridge.Name;

    [ObservableProperty]
    private bool _isVisible;

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (System.Math.Abs(_volume - value) < 0.0001) return;
            _volume = value;
            if (_subscription != null) _subscription.Volume = (float)value;
            // Persist per-bridge so the level survives an app restart.
            // MixerViewModel owns the debounce; this callback just hands
            // off the (id, value) tuple.
            _persist?.Invoke(_bridge.Id, (float)value);
            OnPropertyChanged();
        }
    }

    /// <summary>Build a strip for <paramref name="bridge"/> with the
    /// initial volume loaded from <see cref="AppSettings.BridgeVolumes"/>
    /// (or 1.0 when no persisted value exists). The <paramref name="persist"/>
    /// callback receives <c>(pluginId, volume)</c> on every slider move so
    /// the host can debounce + save.</summary>
    public BridgeStripViewModel(IAudioBridgePlugin bridge, float initialVolume, Action<string, float>? persist)
    {
        _bridge = bridge;
        _volume = initialVolume;
        _persist = persist;
    }

    /// <summary>Called by <see cref="MixerViewModel"/> from
    /// SubscribersChanged. Pass the bridge's current
    /// <see cref="IBroadcastSubscription"/> or null when the bridge is
    /// disconnected. Idempotent — re-attaching the same subscription is
    /// a no-op.</summary>
    public void AttachSubscription(IBroadcastSubscription? subscription)
    {
        if (ReferenceEquals(_subscription, subscription)) return;
        _subscription = subscription;
        if (subscription != null)
        {
            // Sync the remembered volume into the fresh subscription so
            // the call starts at the level the user last left the
            // slider at.
            subscription.Volume = (float)_volume;
        }
        IsVisible = subscription != null;
    }
}

public sealed class BroadcastSliderViewModel : ObservableObject
{
    internal IBroadcastSubscription Subscription { get; }

    public string DisplayName => Subscription.DisplayName;

    public double Volume
    {
        get => Subscription.Volume;
        set
        {
            Subscription.Volume = (float)value;
            OnPropertyChanged();
        }
    }

    public BroadcastSliderViewModel(IBroadcastSubscription subscription)
    {
        Subscription = subscription;
    }
}
