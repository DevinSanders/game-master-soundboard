using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundBoard.Core.Services;

/// <summary>
/// Owns the in-process state of <see cref="SamplerAttachment"/> rows.
/// Three responsibilities:
///
/// <para>
/// 1. <b>Master bus.</b> At startup, each attachment with
///    <c>OwnerType == Master</c> becomes a persistent
///    <see cref="ISamplerInstance"/>, fed into
///    <see cref="MasterMixer.AddGlobalEffect"/>. The instance lives for
///    the app's lifetime; the editor UI binds to the same instance so
///    knob changes update the audio thread live.
/// </para>
/// <para>
/// 2. <b>Per-playback chains (Shortcut / Preset / Playlist).</b>
///    Attachments are kept as rows here; when the engine starts playback
///    for a specific owner it asks for a chain via
///    <see cref="BuildEphemeralChain"/>. The service spawns a fresh
///    <see cref="ISamplerInstance"/> per attachment, calls
///    <c>DeserializeConfig</c> with the row's JSON, and returns the
///    instances. The engine wraps its audio source with them and disposes
///    them when playback stops. This isolates state per-playback so two
///    spawns of the same shortcut don't share reverb tails or other DSP
///    history — otherwise concurrent plays bleed into each other.
/// </para>
/// <para>
/// 3. <b>Persistence.</b> Add / Remove / Reorder / Bypass / SaveConfig
///    write through to <see cref="SamplerAttachment"/> rows in the
///    database. The editor UI talks to this service; no other code touches
///    the table directly.
/// </para>
/// </summary>
public interface ISamplerChainService
{
    /// <summary>Initialize from the database. Materialises master
    /// attachments into <see cref="MasterMixer"/> and caches all
    /// per-target rows in memory. Call once after
    /// <see cref="IPluginService.DiscoverAndLoad"/>.</summary>
    void Initialize();

    /// <summary>Return a fresh chain of <see cref="ISamplerInstance"/>s
    /// for one playback of <paramref name="ownerType"/>/<paramref name="ownerId"/>.
    /// Caller owns the returned instances and must dispose them when
    /// playback ends.</summary>
    IReadOnlyList<ISamplerInstance> BuildEphemeralChain(SamplerOwnerType ownerType, int? ownerId);

    /// <summary>List the persisted attachments for an owner (for editor UI).</summary>
    IReadOnlyList<SamplerAttachment> GetAttachments(SamplerOwnerType ownerType, int? ownerId);

    /// <summary>Append a new attachment to <paramref name="ownerType"/>/
    /// <paramref name="ownerId"/>'s chain. For Master owners, the instance
    /// is materialised and wired into the mixer immediately so the editor
    /// can bind live. Returns the created row.</summary>
    SamplerAttachment AddAttachment(SamplerOwnerType ownerType, int? ownerId, string pluginId);

    /// <summary>Remove an attachment by row id. For Master owners the live
    /// instance is detached from the mixer and disposed.</summary>
    void RemoveAttachment(int attachmentId);

    /// <summary>Toggle bypass for an attachment. For Master, detaches /
    /// re-attaches from the mixer in place. For per-target, just persists —
    /// the next playback skips bypassed rows.</summary>
    void SetBypass(int attachmentId, bool bypassed);

    /// <summary>Reorder an attachment within its owner's chain. For Master,
    /// rebuilds the mixer chain so audio reflects the new order.</summary>
    void SetOrder(int attachmentId, int newOrder);

    /// <summary>Materialise an <see cref="ISamplerInstance"/> for the
    /// editor UI to host. For Master rows, returns the LIVE instance so
    /// knob changes take effect mid-playback. For other rows, returns a
    /// fresh instance with the row's config deserialised — the caller
    /// disposes it on close. <paramref name="isLive"/> tells the caller
    /// whether they're holding a live reference.</summary>
    ISamplerInstance? CreateEditorInstance(SamplerAttachment row, out bool isLive);

    /// <summary>Serialise <paramref name="instance"/>'s current config and
    /// persist it to <paramref name="row"/>'s ConfigJson. Idempotent —
    /// safe to call repeatedly while the user adjusts knobs. Pushes the
    /// new config to any currently-running ephemeral instances of the
    /// same attachment so per-target edits take effect mid-playback.</summary>
    void SaveEditorInstance(SamplerAttachment row, ISamplerInstance instance);

    /// <summary>Drop an ephemeral instance from the live-config registry.
    /// Engine calls this when a playback ends.</summary>
    void UnregisterEphemeral(ISamplerInstance instance);

    /// <summary>Push the editor instance's current state to every alive
    /// ephemeral instance of the same attachment without touching the DB.
    /// Used by the editor's fast tick so audio responds to a slider drag
    /// in ~100 ms while the actual DB write stays debounced. Idempotent
    /// — safe to call every tick whether the user moved a knob or not.</summary>
    void PushLiveConfig(SamplerAttachment row, ISamplerInstance instance);

    /// <summary>Remove every <see cref="SamplerAttachment"/> row for the
    /// given owner, deferring any live <see cref="ISamplerInstance"/>
    /// disposal. Called when the owning preset / playlist / shortcut is
    /// deleted — otherwise the rows become orphans (the polymorphic
    /// OwnerId has no hard FK to clean them up automatically).
    ///
    /// <para>Pass <c>ownerId = null</c> only with <c>OwnerType.Master</c>
    /// (which the app's UI doesn't currently expose, but the API
    /// supports for symmetry).</para></summary>
    void RemoveAttachmentsFor(SamplerOwnerType ownerType, int? ownerId);

    /// <summary>Returns true if the owning entity for the given attachment
    /// point still exists. Used by the editor to detect that the owner
    /// was deleted from another window while the editor was open so the
    /// UI can show a "this no longer exists" message instead of pretending
    /// the chain is empty. <c>Master</c> always returns true.</summary>
    bool OwnerExists(SamplerOwnerType ownerType, int? ownerId);
}

public class SamplerChainService : ISamplerChainService
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IPluginService _pluginService;
    private readonly MasterMixer _masterMixer;

    // Master instances are persistent. UI edits the live instance; audio
    // thread reads its current config. Keyed by attachment id so we can
    // detach when removed.
    private readonly Dictionary<int, ISamplerInstance> _masterInstances = new();

    // Bus instances are also persistent (one ISamplerInstance per Bus FX
    // attachment, lives for the app's lifetime, edited live just like
    // Master). The extra dict maps attachmentId → busId so RemoveAttachment
    // knows which BusMixer to detach from after the DB row is gone — the
    // OwnerId column we read at delete time has the bus id but caching
    // it here means the delete path doesn't need to round-trip through
    // EF after committing.
    private readonly Dictionary<int, ISamplerInstance> _busInstances = new();
    private readonly Dictionary<int, int> _busIdByAttachment = new();

    // Per-target ephemeral instances currently held by in-flight playbacks.
    // Keyed by attachment id; values are the live instances (one per
    // concurrent playback of that owner). The editor uses this map to
    // push live config changes to running instances so a knob in the
    // sampler editor takes effect mid-play without having to stop/restart.
    private readonly Dictionary<int, List<ISamplerInstance>> _ephemeralByAttachment = new();

    // Single lock for both dicts. They're touched from various UI-thread
    // paths (editor tick, playback spawn, playback end, settings page).
    // Although nominally serialised by the Dispatcher, the dispatcher
    // can interleave between message pumps — e.g. an editor 100 ms tick
    // fires while a playback's OnPlaybackStopped dispatcher continuation
    // is queued. Take the lock around every mutation/iteration to keep
    // the dicts consistent.
    private readonly object _stateLock = new();

    public SamplerChainService(
        ISoundBoardDbContextFactory dbFactory,
        IPluginService pluginService,
        MasterMixer masterMixer)
    {
        _dbFactory = dbFactory;
        _pluginService = pluginService;
        _masterMixer = masterMixer;
    }

    public void Initialize()
    {
        // Idempotency guard: a double-Initialize would re-materialize every
        // persistent attachment and double-add to the mixer's chains,
        // silently producing 2× the FX. Today the only caller is
        // App.OnFrameworkInitializationCompleted, but the contract is
        // worth pinning so a future "library switched, reinitialize"
        // refactor doesn't have a foot-gun.
        lock (_stateLock)
        {
            if (_masterInstances.Count > 0 || _busInstances.Count > 0)
            {
                Log.Warn("Sampler", "Initialize called more than once; ignoring subsequent call.");
                return;
            }
        }

        using var db = _dbFactory.CreateDbContext();

        // Pre-create every configured BusMixer BEFORE wiring bus FX so the
        // EnsureBus path inside AddBusEffect never has to race with a
        // first-track AddMixerInput on the audio thread. Buses without
        // attachments still get mixers — that's fine, an empty BusMixer
        // costs ~nothing and means Track.BusId routing always lands on a
        // ready mixer instead of one materialised mid-buffer.
        // Also seed each BusMixer's volume from the persisted Bus.Volume
        // column so the GM's level balance survives app restarts.
        var buses = db.Buses.Select(b => new { b.Id, b.Volume }).ToList();
        _masterMixer.EnsureBuses(buses.Select(b => b.Id));
        foreach (var b in buses)
        {
            var bm = _masterMixer.GetBus(b.Id);
            if (bm != null) bm.Volume = b.Volume;
        }
        var busIds = buses.Select(b => b.Id).ToList();

        // Include bypassed rows too: the BypassableSamplerInstance wrapper
        // lets us flip bypass at runtime without rebuilding the chain, so
        // a row that's currently bypassed still belongs in the mixer chain
        // (just passing the dry signal through).
        var masterRows = db.SamplerAttachments
            .Where(a => a.OwnerType == SamplerOwnerType.Master)
            .OrderBy(a => a.Order)
            .ToList();

        foreach (var row in masterRows)
        {
            var instance = TryMaterialize(row);
            if (instance == null) continue;
            lock (_stateLock) _masterInstances[row.Id] = instance;
            _masterMixer.AddGlobalEffect(instance);
        }

        // Bus attachments materialise the same way as Master, but route to
        // the named bus's per-bus FX chain instead of the master global
        // chain. The (publisher,id) lineage rule applies — a row whose
        // plugin isn't installed gets logged-and-skipped just like Master.
        var busRows = db.SamplerAttachments
            .Where(a => a.OwnerType == SamplerOwnerType.Bus && a.OwnerId != null)
            .OrderBy(a => a.Order)
            .ToList();

        foreach (var row in busRows)
        {
            var instance = TryMaterialize(row);
            if (instance == null) continue;
            var busId = row.OwnerId!.Value;
            lock (_stateLock)
            {
                _busInstances[row.Id] = instance;
                _busIdByAttachment[row.Id] = busId;
            }
            _masterMixer.AddBusEffect(busId, instance);
        }

        int masterCount, busCount;
        lock (_stateLock)
        {
            masterCount = _masterInstances.Count;
            busCount = _busInstances.Count;
        }
        Log.Info("Sampler", $"Master chain: {masterCount} attachment(s); Bus chains: {busCount} attachment(s) across {busIds.Count} bus(es).");
    }

    public IReadOnlyList<ISamplerInstance> BuildEphemeralChain(SamplerOwnerType ownerType, int? ownerId)
    {
        if (ownerType == SamplerOwnerType.Master)
            throw new InvalidOperationException("Master attachments are persistent, not ephemeral.");
        if (ownerType == SamplerOwnerType.Bus)
            throw new InvalidOperationException("Bus attachments are persistent, not ephemeral.");

        using var db = _dbFactory.CreateDbContext();
        // Include bypassed rows too: the wrapper handles bypass at runtime
        // so toggling it mid-play takes effect without stopping the chain.
        var rows = db.SamplerAttachments
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .OrderBy(a => a.Order)
            .ToList();

        var instances = new List<ISamplerInstance>(rows.Count);
        foreach (var row in rows)
        {
            var instance = TryMaterialize(row);
            if (instance == null) continue;
            instances.Add(instance);
            RegisterEphemeral(row.Id, instance);
        }
        return instances;
    }

    /// <summary>Drop a live ephemeral instance from the registry. The
    /// engine calls this when a playback ends so the next live-config
    /// push doesn't try to update a disposed instance. Safe to call
    /// repeatedly; safe to call on instances that were never registered.</summary>
    public void UnregisterEphemeral(ISamplerInstance instance)
    {
        lock (_stateLock)
        {
            // O(N attachments) walk — N is tiny in practice. Track the
            // hit key while iterating so we can remove from the dict
            // AFTER the foreach loop exits (mutating during iteration
            // throws). Pre-fix this allocated a Keys.ToList snapshot on
            // every call; under heavy playlist crossfades that's
            // unnecessary Gen0 churn given the typical N < 10.
            int? hitKey = null;
            List<ISamplerInstance>? hitList = null;
            foreach (var kvp in _ephemeralByAttachment)
            {
                if (kvp.Value.Remove(instance))
                {
                    hitKey = kvp.Key;
                    hitList = kvp.Value;
                    break;
                }
            }
            if (hitKey.HasValue && hitList!.Count == 0)
                _ephemeralByAttachment.Remove(hitKey.Value);
        }
    }

    private void RegisterEphemeral(int attachmentId, ISamplerInstance instance)
    {
        lock (_stateLock)
        {
            if (!_ephemeralByAttachment.TryGetValue(attachmentId, out var list))
            {
                list = new List<ISamplerInstance>();
                _ephemeralByAttachment[attachmentId] = list;
            }
            list.Add(instance);
        }
    }

    public IReadOnlyList<SamplerAttachment> GetAttachments(SamplerOwnerType ownerType, int? ownerId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.SamplerAttachments
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .OrderBy(a => a.Order)
            .ToList();
    }

    public SamplerAttachment AddAttachment(SamplerOwnerType ownerType, int? ownerId, string pluginId)
    {
        using var db = _dbFactory.CreateDbContext();

        // Append to the end of the chain. Order column is dense; new rows
        // get max+1 so the user's mental model "added at the bottom" holds.
        var maxOrder = db.SamplerAttachments
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .Select(a => (int?)a.Order)
            .Max() ?? -1;

        var row = new SamplerAttachment
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            PluginId = pluginId,
            ConfigJson = "",
            Order = maxOrder + 1,
            IsBypassed = false,
        };
        db.SamplerAttachments.Add(row);
        db.SaveChanges();

        if (ownerType == SamplerOwnerType.Master)
        {
            var instance = TryMaterialize(row);
            if (instance != null)
            {
                lock (_stateLock) _masterInstances[row.Id] = instance;
                _masterMixer.AddGlobalEffect(instance);
            }
        }
        else if (ownerType == SamplerOwnerType.Bus && ownerId.HasValue)
        {
            var instance = TryMaterialize(row);
            if (instance != null)
            {
                lock (_stateLock)
                {
                    _busInstances[row.Id] = instance;
                    _busIdByAttachment[row.Id] = ownerId.Value;
                }
                _masterMixer.AddBusEffect(ownerId.Value, instance);
            }
        }

        Log.Info("Sampler", $"Added attachment #{row.Id}: {pluginId} -> {ownerType}/{ownerId?.ToString() ?? "<master>"}.");
        return row;
    }

    public void RemoveAttachment(int attachmentId)
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.SamplerAttachments.Find(attachmentId);
        if (row == null) return;

        ISamplerInstance? instance = null;
        int? busId = null;
        lock (_stateLock)
        {
            if (row.OwnerType == SamplerOwnerType.Master && _masterInstances.TryGetValue(attachmentId, out instance))
            {
                _masterInstances.Remove(attachmentId);
            }
            else if (row.OwnerType == SamplerOwnerType.Bus && _busInstances.TryGetValue(attachmentId, out instance))
            {
                _busInstances.Remove(attachmentId);
                if (_busIdByAttachment.TryGetValue(attachmentId, out var b))
                {
                    busId = b;
                    _busIdByAttachment.Remove(attachmentId);
                }
            }
        }
        if (instance != null)
        {
            if (row.OwnerType == SamplerOwnerType.Master)
                _masterMixer.RemoveGlobalEffect(instance);
            else if (row.OwnerType == SamplerOwnerType.Bus && busId.HasValue)
                _masterMixer.RemoveBusEffect(busId.Value, instance);

            // Defer the actual Dispose — the audio thread may still be
            // inside Read referencing the removed effect through the
            // pre-rebuild chain snapshot. MasterMixer.DeferDispose hands
            // it to a background drainer after one Read cycle elapses.
            _masterMixer.DeferDispose(instance);
        }

        db.SamplerAttachments.Remove(row);
        db.SaveChanges();
        Log.Info("Sampler", $"Removed attachment #{attachmentId}.");
    }

    public void SetBypass(int attachmentId, bool bypassed)
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.SamplerAttachments.Find(attachmentId);
        if (row == null || row.IsBypassed == bypassed) return;

        row.IsBypassed = bypassed;
        db.SaveChanges();

        // The chain always includes the wrapper; flipping IsBypassed on
        // it switches dry/wet on the next audio buffer without rebuilding.
        // Same code path for Master, Bus, and per-target — all three store
        // BypassableSamplerInstance, in _masterInstances / _busInstances /
        // _ephemeralByAttachment respectively. Snapshot under lock; the
        // actual flag-flip on the BypassableSamplerInstance is itself
        // thread-safe (Volatile.Write).
        ISamplerInstance? masterInst;
        ISamplerInstance? busInst;
        List<ISamplerInstance>? ephemerals;
        lock (_stateLock)
        {
            _masterInstances.TryGetValue(attachmentId, out masterInst);
            _busInstances.TryGetValue(attachmentId, out busInst);
            ephemerals = _ephemeralByAttachment.TryGetValue(attachmentId, out var list)
                ? list.ToList() // snapshot — caller iterates outside the lock
                : null;
        }
        if (masterInst is BypassableSamplerInstance masterBypass)
            masterBypass.IsBypassed = bypassed;
        if (busInst is BypassableSamplerInstance busBypass)
            busBypass.IsBypassed = bypassed;
        if (ephemerals != null)
        {
            foreach (var inst in ephemerals)
                if (inst is BypassableSamplerInstance b) b.IsBypassed = bypassed;
        }
    }

    public void SetOrder(int attachmentId, int newOrder)
    {
        using var db = _dbFactory.CreateDbContext();
        var row = db.SamplerAttachments.Find(attachmentId);
        if (row == null || row.Order == newOrder) return;

        row.Order = newOrder;
        db.SaveChanges();

        // For master, rebuild the mixer chain so the new order is reflected
        // on the audio thread. _masterInstances isn't ordered intrinsically;
        // we'd need to rebuild by re-reading the db. For simplicity, full
        // re-init — chain changes are rare and the cost is negligible.
        if (row.OwnerType == SamplerOwnerType.Master)
            ReinitMasterChain();
        else if (row.OwnerType == SamplerOwnerType.Bus && row.OwnerId.HasValue)
            ReinitBusChain(row.OwnerId.Value);
    }

    /// <summary>Tear down the current master chain and re-materialise from
    /// the DB in row order. Used after order/bypass changes; cheaper than
    /// trying to splice the mixer's internal list in place.</summary>
    private void ReinitMasterChain()
    {
        // Snapshot existing instances and clear under lock; defer disposal
        // so the audio thread can finish whatever Read cycle it's in
        // before the providers hooked into the old chain are torn down.
        List<ISamplerInstance> oldInstances;
        lock (_stateLock)
        {
            oldInstances = _masterInstances.Values.ToList();
            _masterInstances.Clear();
        }
        foreach (var inst in oldInstances) _masterMixer.RemoveGlobalEffect(inst);
        foreach (var inst in oldInstances) _masterMixer.DeferDispose(inst);

        using var db = _dbFactory.CreateDbContext();
        // Include bypassed rows: the BypassableSamplerInstance wrapper
        // stays in the mixer chain even when bypassed so SetBypass can
        // flip a flag instead of rebuilding. Dropping bypassed rows here
        // means a subsequent SetBypass(false) finds nothing to un-bypass
        // — silent regression. (Mirrors Initialize's query above.)
        var rows = db.SamplerAttachments
            .Where(a => a.OwnerType == SamplerOwnerType.Master)
            .OrderBy(a => a.Order)
            .ToList();

        foreach (var row in rows)
        {
            var instance = TryMaterialize(row);
            if (instance == null) continue;
            lock (_stateLock) _masterInstances[row.Id] = instance;
            _masterMixer.AddGlobalEffect(instance);
        }
    }

    /// <summary>Same shape as <see cref="ReinitMasterChain"/>, scoped to
    /// one bus. Snapshots and tears down the bus's current attachments,
    /// then re-reads its rows in row order and re-materialises. Used after
    /// SetOrder on a Bus row. Costs a momentary chain rebuild on that bus
    /// only; other buses keep playing untouched.</summary>
    private void ReinitBusChain(int busId)
    {
        List<(int attachmentId, ISamplerInstance instance)> oldInstances;
        lock (_stateLock)
        {
            oldInstances = _busIdByAttachment
                .Where(kvp => kvp.Value == busId)
                .Select(kvp => (kvp.Key, _busInstances.TryGetValue(kvp.Key, out var i) ? i : null))
                .Where(t => t.Item2 != null)
                .Select(t => (t.Key, t.Item2!))
                .ToList();
            foreach (var (id, _) in oldInstances)
            {
                _busInstances.Remove(id);
                _busIdByAttachment.Remove(id);
            }
        }
        foreach (var (_, inst) in oldInstances) _masterMixer.RemoveBusEffect(busId, inst);
        foreach (var (_, inst) in oldInstances) _masterMixer.DeferDispose(inst);

        using var db = _dbFactory.CreateDbContext();
        var rows = db.SamplerAttachments
            .Where(a => a.OwnerType == SamplerOwnerType.Bus && a.OwnerId == busId)
            .OrderBy(a => a.Order)
            .ToList();

        foreach (var row in rows)
        {
            var instance = TryMaterialize(row);
            if (instance == null) continue;
            lock (_stateLock)
            {
                _busInstances[row.Id] = instance;
                _busIdByAttachment[row.Id] = busId;
            }
            _masterMixer.AddBusEffect(busId, instance);
        }
    }

    public ISamplerInstance? CreateEditorInstance(SamplerAttachment row, out bool isLive)
    {
        // Master rows: hand back the live instance so the editor edits the
        // same object the audio thread is reading from.
        if (row.OwnerType == SamplerOwnerType.Master)
        {
            ISamplerInstance? live;
            lock (_stateLock) _masterInstances.TryGetValue(row.Id, out live);
            if (live != null)
            {
                isLive = true;
                return live;
            }
        }
        // Bus rows: same as Master — the persistent instance is the one
        // the audio thread reads from, so editor knob changes apply live.
        else if (row.OwnerType == SamplerOwnerType.Bus)
        {
            ISamplerInstance? live;
            lock (_stateLock) _busInstances.TryGetValue(row.Id, out live);
            if (live != null)
            {
                isLive = true;
                return live;
            }
        }

        // Per-target (or persistent row that's missing — e.g. plugin failed
        // to materialise at startup): spawn a fresh instance for the editor.
        isLive = false;
        return TryMaterialize(row);
    }

    public void PushLiveConfig(SamplerAttachment row, ISamplerInstance instance)
    {
        // Master and Bus rows: the editor binds to the live instance
        // directly, so the audio thread already sees the latest knob via
        // Interlocked / Volatile inside the plugin. Nothing to push for
        // persistent tiers.
        if (row.OwnerType == SamplerOwnerType.Master) return;
        if (row.OwnerType == SamplerOwnerType.Bus) return;

        // Snapshot the alive list under lock so we don't iterate a
        // mutating collection if a playback ends mid-push.
        List<ISamplerInstance> alive;
        lock (_stateLock)
        {
            if (!_ephemeralByAttachment.TryGetValue(row.Id, out var list) || list.Count == 0)
                return;
            alive = list.ToList();
        }

        string json;
        try { json = instance.SerializeConfig() ?? ""; }
        catch (Exception ex)
        {
            Log.Warn("Sampler", $"PushLiveConfig: SerializeConfig threw for #{row.Id}: {ex.Message}");
            return;
        }

        foreach (var inst in alive)
        {
            if (ReferenceEquals(inst, instance)) continue;
            try { inst.DeserializeConfig(json); }
            catch (Exception ex)
            {
                Log.Warn("Sampler", $"PushLiveConfig: DeserializeConfig threw for #{row.Id}: {ex.Message}");
            }
        }
    }

    public void SaveEditorInstance(SamplerAttachment row, ISamplerInstance instance)
    {
        string json;
        try { json = instance.SerializeConfig() ?? ""; }
        catch (Exception ex)
        {
            Log.Error("Sampler", $"SerializeConfig threw for attachment #{row.Id} ({row.PluginId})", ex);
            return;
        }

        using var db = _dbFactory.CreateDbContext();
        var tracked = db.SamplerAttachments.Find(row.Id);
        if (tracked == null) return;
        tracked.ConfigJson = json;
        db.SaveChanges();

        // For per-target rows, push the new config to every currently-
        // running ephemeral instance of this attachment so the GM hears
        // the change without stop/restart. Plugins' DeserializeConfig
        // must be thread-safe for this — UI thread calls it while the
        // audio thread is reading the instance's state.
        // Persistent tiers (Master, Bus) edit the live instance directly,
        // so the save-time push has nothing to do for them.
        if (row.OwnerType != SamplerOwnerType.Master && row.OwnerType != SamplerOwnerType.Bus)
        {
            List<ISamplerInstance>? alive = null;
            lock (_stateLock)
            {
                if (_ephemeralByAttachment.TryGetValue(row.Id, out var list))
                    alive = list.ToList();
            }
            if (alive != null)
            {
                // Skip the instance the editor is holding — it already has
                // the new state (it's what we just serialised).
                foreach (var inst in alive)
                {
                    if (ReferenceEquals(inst, instance)) continue;
                    try { inst.DeserializeConfig(json); }
                    catch (Exception ex)
                    {
                        Log.Warn("Sampler", $"Live config push failed for attachment #{row.Id}: {ex.Message}");
                    }
                }
            }
        }
    }

    public void RemoveAttachmentsFor(SamplerOwnerType ownerType, int? ownerId)
    {
        // Pull the row list under a fresh context. We dispose live
        // master instances + ephemerals AFTER the DB delete commits, so
        // a half-completed delete doesn't leave a wrapper running on
        // material that no longer has a persisted definition.
        using var db = _dbFactory.CreateDbContext();
        var rows = db.SamplerAttachments
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .ToList();
        if (rows.Count == 0) return;

        db.SamplerAttachments.RemoveRange(rows);
        db.SaveChanges();

        // Tear down any live instances tied to these rows.
        foreach (var row in rows)
        {
            if (ownerType == SamplerOwnerType.Master)
            {
                ISamplerInstance? master;
                lock (_stateLock)
                {
                    if (_masterInstances.TryGetValue(row.Id, out master))
                        _masterInstances.Remove(row.Id);
                }
                if (master != null)
                {
                    _masterMixer.RemoveGlobalEffect(master);
                    _masterMixer.DeferDispose(master);
                }
            }
            else if (ownerType == SamplerOwnerType.Bus)
            {
                ISamplerInstance? busInst;
                int? busId = null;
                lock (_stateLock)
                {
                    if (_busInstances.TryGetValue(row.Id, out busInst))
                        _busInstances.Remove(row.Id);
                    if (_busIdByAttachment.TryGetValue(row.Id, out var b))
                    {
                        busId = b;
                        _busIdByAttachment.Remove(row.Id);
                    }
                }
                if (busInst != null && busId.HasValue)
                {
                    _masterMixer.RemoveBusEffect(busId.Value, busInst);
                    _masterMixer.DeferDispose(busInst);
                }
            }
            else
            {
                // Ephemerals: the playback that spawned them owns Dispose
                // (it'll find them gone from _ephemeralByAttachment via
                // UnregisterEphemeral on its OnPlaybackStopped). We just
                // drop the registry entry so live-config pushes don't
                // target now-orphaned instances. The audio thread keeps
                // reading from them until the playback ends naturally —
                // safe, since the underlying ISamplerInstance objects
                // are still alive on the engine's owning list.
                lock (_stateLock) _ephemeralByAttachment.Remove(row.Id);
            }
        }

        Log.Info("Sampler", $"Cleaned up {rows.Count} attachment(s) for {ownerType}/{ownerId?.ToString() ?? "<master>"}.");
    }

    public bool OwnerExists(SamplerOwnerType ownerType, int? ownerId)
    {
        if (ownerType == SamplerOwnerType.Master) return true;
        if (ownerId is null) return false;
        using var db = _dbFactory.CreateDbContext();
        return ownerType switch
        {
            SamplerOwnerType.Preset   => db.Presets.Any(p => p.Id == ownerId),
            SamplerOwnerType.Playlist => db.Playlists.Any(p => p.Id == ownerId),
            SamplerOwnerType.Shortcut => db.ShortcutButtons.Any(b => b.Id == ownerId),
            SamplerOwnerType.Bus      => db.Buses.Any(b => b.Id == ownerId),
            _ => false,
        };
    }

    /// <summary>Resolve the plugin, instantiate, deserialize config, wrap
    /// in a <see cref="BypassableSamplerInstance"/> so the host can flip
    /// bypass at runtime. Returns null if the plugin isn't loaded — the
    /// row stays in the DB so the user can reinstall and recover.</summary>
    private ISamplerInstance? TryMaterialize(SamplerAttachment row)
    {
        var plugin = _pluginService.LoadedPlugins
            .OfType<IAudioSamplerPlugin>()
            .FirstOrDefault(p => p.Id == row.PluginId);

        if (plugin == null)
        {
            Log.Warn("Sampler", $"Attachment #{row.Id} references plugin '{row.PluginId}' which is not loaded; skipping.");
            return null;
        }

        try
        {
            var instance = plugin.CreateInstance();
            if (!string.IsNullOrEmpty(row.ConfigJson))
                instance.DeserializeConfig(row.ConfigJson);
            // Wrap so runtime bypass can switch dry/wet on the audio thread
            // without rebuilding the chain. Plugin authors never see the
            // wrapper — CreateControl forwards to the inner instance.
            return new BypassableSamplerInstance(instance, row.IsBypassed);
        }
        catch (Exception ex)
        {
            Log.Error("Sampler", $"Plugin '{row.PluginId}' threw during CreateInstance/DeserializeConfig.", ex);
            return null;
        }
    }
}
