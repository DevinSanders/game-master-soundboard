using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the per-owner sampler chain editor window. One instance edits the
/// chain for one owner: Master, a Shortcut, a Preset, or a Playlist. The
/// list of attached samplers is rendered as cards; each card hosts the
/// plugin's own <see cref="ISamplerInstance.CreateControl"/> for live
/// editing of its parameters.
///
/// <para><b>Lifecycle.</b> For Master owners the editor binds to the LIVE
/// <see cref="ISamplerInstance"/> objects already wired into the master
/// mixer — knob changes apply mid-playback. For per-target owners the
/// editor spawns FRESH instances (deserialised from the row's ConfigJson)
/// and disposes them on close; the next playback of that owner reads the
/// saved config and creates its own playback-scoped instances. Config is
/// persisted on every knob movement via the EditPersistence debouncer plus
/// a final flush on close.</para>
/// </summary>
public partial class SamplerEditorViewModel : ViewModelBase, IDisposable
{
    private readonly ISamplerChainService _chainService;
    private readonly IPluginService _pluginService;

    /// <summary>Owner of the chain being edited. Read-only after construction.</summary>
    public SamplerOwnerType OwnerType { get; }
    public int? OwnerId { get; }

    /// <summary>Human-readable label of the owning entity (e.g. "Tavern
    /// Ambience" for a shortcut). Drives the window title.</summary>
    public string OwnerDisplayName { get; }

    /// <summary>Currently-attached samplers, in chain order. Each item
    /// wraps a row + its editor-bound instance + the plugin's control.
    /// Reference is stable for the lifetime of the VM — mutated via
    /// Clear()+Add rather than reassigned (the DataGrid sort-stability
    /// pattern: reassigning ItemsSource drops the user's sort). No setter
    /// needed, so no <see cref="ObservableProperty"/>.</summary>
    public ObservableCollection<AttachedSamplerViewModel> Attached { get; } = new();

    /// <summary>Sampler plugins that can be attached to this owner (filtered
    /// by <see cref="IAudioSamplerPlugin.SupportedAttachments"/>). The view
    /// shows them as a draggable list on the left; the user drops one onto
    /// the chain panel to attach it. Same stable-reference contract as
    /// <see cref="Attached"/>.</summary>
    public ObservableCollection<IAudioSamplerPlugin> AvailablePlugins { get; } = new();

    /// <summary>Empty-state message shown when there are no attachments.</summary>
    public string EmptyStateMessage => IsOwnerMissing
        ? $"The {OwnerType.ToString().ToLower()} this FX chain belonged to was deleted. Close this window."
        : "No effects attached. Pick one from the list above to start.";

    /// <summary>True when the owning preset/playlist/shortcut was deleted
    /// while this editor was open. The view binds the empty-state message
    /// off this so the user gets a clear "close me" hint instead of a
    /// misleading "no effects attached".</summary>
    [ObservableProperty]
    private bool _isOwnerMissing;

    /// <summary>Debounced persister — knob changes write through to the DB
    /// without spamming SaveChanges on every slider tick.</summary>
    public Services.EditPersistence Persistence { get; } = new();

    /// <summary>Polls each instance on a fast tick. Per-target rows: push
    /// the editor's current state to alive ephemeral instances via the
    /// chain service's fast no-DB path so audio responds within ~100 ms of
    /// the slider moving. The same tick also schedules a debounced DB
    /// persist so the value survives a restart, but the audio doesn't
    /// have to wait for that write.
    ///
    /// <para>The tick body is change-driven: each
    /// <see cref="AttachedSamplerViewModel.PushLiveConfigIfChanged"/>
    /// diffs the plugin's serialized config against the last value pushed
    /// and short-circuits when nothing changed. Idle ticks cost one
    /// SerializeConfig + string compare per attachment — no audio-thread
    /// work, no DB write, no debounce churn. Plugins aren't required to be
    /// observable, so subscribing to a PropertyChanged event isn't an
    /// option; diff-on-serialize is the practical equivalent.</para></summary>
    private readonly DispatcherTimer _autoSaveTimer;

    public SamplerEditorViewModel(
        ISamplerChainService chainService,
        IPluginService pluginService,
        SamplerOwnerType ownerType,
        int? ownerId,
        string ownerDisplayName)
    {
        _chainService = chainService;
        _pluginService = pluginService;
        OwnerType = ownerType;
        OwnerId = ownerId;
        OwnerDisplayName = ownerDisplayName;

        LoadAvailablePlugins();
        LoadAttached();

        _autoSaveTimer = new DispatcherTimer { Interval = Services.UiConstants.SamplerEditorTick };
        _autoSaveTimer.Tick += (s, e) =>
        {
            foreach (var item in Attached)
            {
                // Returns non-null when serialized config changed — audio
                // thread sees the new value within the next buffer; DB
                // persist is debounced through EditPersistence.
                item.PushLiveConfigIfChanged();
            }
        };
        _autoSaveTimer.Start();
    }

    private void LoadAvailablePlugins()
    {
        var point = OwnerType switch
        {
            SamplerOwnerType.Master   => SamplerAttachmentPoints.Master,
            SamplerOwnerType.Shortcut => SamplerAttachmentPoints.Shortcut,
            SamplerOwnerType.Preset   => SamplerAttachmentPoints.Preset,
            SamplerOwnerType.Playlist => SamplerAttachmentPoints.Playlist,
            _                         => SamplerAttachmentPoints.None
        };

        AvailablePlugins.Clear();
        foreach (var plugin in _pluginService.LoadedPlugins
                                             .OfType<IAudioSamplerPlugin>()
                                             .Where(p => p.SupportedAttachments.HasFlag(point))
                                             .OrderBy(p => p.Name))
        {
            AvailablePlugins.Add(plugin);
        }
    }

    private void LoadAttached()
    {
        // Dispose any current editor-bound instances first (this is a
        // reload, e.g. after Add/Remove).
        foreach (var existing in Attached) existing.DisposeEditorInstance();
        Attached.Clear();

        // Detect the owner-was-deleted case BEFORE pulling attachments —
        // if the owner is gone, attachments are stale orphans that should
        // have been cleaned up by RemoveAttachmentsFor on the delete path.
        // Show the user a "close me" message rather than letting them
        // edit dead rows.
        IsOwnerMissing = !_chainService.OwnerExists(OwnerType, OwnerId);
        OnPropertyChanged(nameof(EmptyStateMessage));

        if (!IsOwnerMissing)
        {
            foreach (var row in _chainService.GetAttachments(OwnerType, OwnerId))
            {
                var vm = AttachedSamplerViewModel.Create(row, _chainService, _pluginService, Persistence);
                if (vm != null) Attached.Add(vm);
            }
        }
        OnPropertyChanged(nameof(HasAttached));
    }

    /// <summary>True when there is at least one row attached. Drives the
    /// empty-state visibility in the view.</summary>
    public bool HasAttached => Attached.Count > 0;

    /// <summary>Attach a fresh instance of <paramref name="plugin"/> to the
    /// owner's chain. Called from the View's drop handler when the user
    /// drags from the Available list onto the chain pane.</summary>
    public void AddSampler(IAudioSamplerPlugin plugin)
    {
        if (plugin == null) return;
        _chainService.AddAttachment(OwnerType, OwnerId, plugin.Id);
        LoadAttached();
    }

    [RelayCommand]
    private void RemoveFxChainItem(AttachedSamplerViewModel? item)
    {
        if (item == null) return;
        item.DisposeEditorInstance();
        _chainService.RemoveAttachment(item.AttachmentId);
        Attached.Remove(item);
        OnPropertyChanged(nameof(HasAttached));
    }

    [RelayCommand]
    private void MoveUp(AttachedSamplerViewModel? item)
    {
        if (item == null) return;
        var idx = Attached.IndexOf(item);
        if (idx <= 0) return;
        SwapInDb(Attached[idx - 1], item);
        Attached.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveDown(AttachedSamplerViewModel? item)
    {
        if (item == null) return;
        var idx = Attached.IndexOf(item);
        if (idx < 0 || idx >= Attached.Count - 1) return;
        SwapInDb(item, Attached[idx + 1]);
        Attached.Move(idx, idx + 1);
    }

    private void SwapInDb(AttachedSamplerViewModel a, AttachedSamplerViewModel b)
    {
        var oa = a.Order;
        a.Order = b.Order;
        b.Order = oa;
        _chainService.SetOrder(a.AttachmentId, a.Order);
        _chainService.SetOrder(b.AttachmentId, b.Order);
    }

    /// <summary>Reorder <paramref name="source"/> next to <paramref name="target"/>
    /// in the visible chain. Called during drag-over so the user sees the
    /// shuffle as they move the pointer. No DB write here — that happens
    /// on drop via <see cref="PersistOrder"/>.</summary>
    public void MoveItemVisually(AttachedSamplerViewModel source, AttachedSamplerViewModel target)
    {
        var sIdx = Attached.IndexOf(source);
        var tIdx = Attached.IndexOf(target);
        if (sIdx < 0 || tIdx < 0 || sIdx == tIdx) return;
        Attached.Move(sIdx, tIdx);
    }

    /// <summary>Rebase every attachment's <c>Order</c> to its current
    /// position in the visible <see cref="Attached"/> collection, then
    /// persist via <see cref="ISamplerChainService.SetOrder"/>. Called
    /// once on drop after the drag-over passes have settled the order
    /// visually. Skips rows whose order hasn't changed.</summary>
    public void PersistOrder()
    {
        for (int i = 0; i < Attached.Count; i++)
        {
            var item = Attached[i];
            if (item.Order == i) continue;
            item.Order = i;
            _chainService.SetOrder(item.AttachmentId, i);
        }
    }

    public void Dispose()
    {
        _autoSaveTimer.Stop();

        // Final pass before flush — captures any edit made in the gap
        // since the last 2-second tick.
        foreach (var item in Attached) item.SchedulePersist();

        // Flush any pending debounced saves before tearing down editor
        // instances — otherwise an in-flight knob change would never make
        // it to disk.
        try { Persistence.Flush(); }
        catch (Exception ex) { Log.Warn("Sampler", "EditPersistence.Flush threw on editor close", ex); }

        foreach (var item in Attached) item.DisposeEditorInstance();
        Attached.Clear();
    }
}

/// <summary>
/// One row in the sampler editor. Pairs the <see cref="SamplerAttachment"/>
/// from the DB with a live <see cref="ISamplerInstance"/> the editor can
/// host. <see cref="Control"/> is the plugin-supplied UI; <see cref="HasControl"/>
/// distinguishes "no UI" from "UI is present" so the card can render an
/// empty-state hint instead of a blank panel.
/// </summary>
public partial class AttachedSamplerViewModel : ViewModelBase
{
    private readonly ISamplerChainService _chainService;
    private readonly IPluginService _pluginService;
    private readonly Services.EditPersistence _persistence;
    private readonly SamplerAttachment _row;
    private ISamplerInstance? _instance;
    private bool _isLive;

    /// <summary>Last serialized config we pushed/persisted. Lets the
    /// editor's tick treat an unchanged config as a no-op instead of
    /// re-shipping it to alive ephemerals every 100 ms. Null until the
    /// first push, then tracks the most recent SerializeConfig output.</summary>
    private string? _lastConfigJson;

    public int AttachmentId => _row.Id;
    public string PluginId => _row.PluginId;
    public string PluginName { get; }
    public object? Control { get; }
    public bool HasControl => Control != null;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private bool _isBypassed;

    /// <summary>"(live)" badge text shown for master rows so the user knows
    /// edits apply mid-playback (vs. on next-play for other tiers).</summary>
    public string TierLabel => _isLive ? "live" : "applies on next play";

    private AttachedSamplerViewModel(
        SamplerAttachment row,
        ISamplerInstance instance,
        bool isLive,
        IAudioSamplerPlugin plugin,
        ISamplerChainService chainService,
        IPluginService pluginService,
        Services.EditPersistence persistence)
    {
        _row = row;
        _instance = instance;
        _isLive = isLive;
        _chainService = chainService;
        _pluginService = pluginService;
        _persistence = persistence;
        PluginName = plugin.Name;
        Control = instance.CreateControl();
        _order = row.Order;
        _isBypassed = row.IsBypassed;
    }

    /// <summary>Factory — returns null if the underlying plugin isn't
    /// loaded (uninstalled while the attachment row remained). Caller is
    /// expected to skip the row in that case; the row stays in the DB so
    /// reinstalling recovers it.</summary>
    public static AttachedSamplerViewModel? Create(
        SamplerAttachment row,
        ISamplerChainService chainService,
        IPluginService pluginService,
        Services.EditPersistence persistence)
    {
        var plugin = pluginService.LoadedPlugins.OfType<IAudioSamplerPlugin>().FirstOrDefault(p => p.Id == row.PluginId);
        if (plugin == null)
        {
            Log.Warn("Sampler", $"Editor row #{row.Id}: plugin '{row.PluginId}' not loaded; hiding from editor.");
            return null;
        }
        var instance = chainService.CreateEditorInstance(row, out var isLive);
        if (instance == null) return null;

        return new AttachedSamplerViewModel(row, instance, isLive, plugin, chainService, pluginService, persistence);
    }

    partial void OnIsBypassedChanged(bool value)
    {
        if (_row.IsBypassed == value) return;
        _chainService.SetBypass(_row.Id, value);
        _row.IsBypassed = value;
    }

    /// <summary>Schedule a debounced persist of the instance's current
    /// config. Audio change has already been pushed via PushLiveConfig;
    /// this is purely for survival-across-restart.</summary>
    public void SchedulePersist()
    {
        if (_instance == null) return;
        var inst = _instance;
        var row = _row;
        _persistence.Schedule($"sampler-{row.Id}", () => _chainService.SaveEditorInstance(row, inst));
    }

    /// <summary>Tick from the editor: push to alive ephemerals + schedule
    /// a persist ONLY if the plugin's serialized config has actually
    /// changed since the last push. Idle ticks become a single
    /// SerializeConfig + string compare per attachment — no audio-thread
    /// work, no debounce reschedule, no DB write.
    ///
    /// <para>Returns the serialized config when it changed (else null) so
    /// callers / tests can observe diff-vs-no-diff without re-serializing.
    /// The plugin SDK's SerializeConfig is the contract — we can't subscribe
    /// to a plugin-defined PropertyChanged because plugins aren't required
    /// to be observable. Diff-on-serialize is the practical equivalent.</para></summary>
    public string? PushLiveConfigIfChanged()
    {
        if (_instance == null) return null;

        string current;
        try { current = _instance.SerializeConfig() ?? ""; }
        catch (Exception ex)
        {
            Log.Warn("Sampler", $"SerializeConfig threw for editor row #{_row.Id}; skipping tick.", ex);
            return null;
        }

        if (current == _lastConfigJson) return null;
        _lastConfigJson = current;
        _chainService.PushLiveConfig(_row, _instance);
        SchedulePersist();
        return current;
    }

    public void DisposeEditorInstance()
    {
        // Master instances are owned by the chain service for their app
        // lifetime — DO NOT dispose them when the editor closes; the audio
        // thread is still using them. Per-target instances are owned by
        // the editor and are safe to dispose.
        if (_isLive) { _instance = null; return; }

        var inst = _instance;
        _instance = null;
        if (inst != null)
        {
            try { inst.Dispose(); }
            catch (Exception ex) { Log.Warn("Sampler", $"Editor instance Dispose threw for #{_row.Id}", ex); }
        }
    }
}
