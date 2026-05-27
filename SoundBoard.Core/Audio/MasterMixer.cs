using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.PluginApi;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SoundBoard.Core.Audio;

/// <summary>
/// The single mixing point for every sound the app produces. Sources are
/// added via <see cref="AddMixerInput(ISampleProvider, int)"/> targeting a
/// specific bus; each bus has its own internal <see cref="BusMixer"/>
/// with an independent per-bus FX chain. The buses then combine into the
/// master output, run through any registered master-tier
/// <see cref="Plugins.IAudioSamplerPlugin"/> effects, and split into the
/// local output (via NAudio on Windows, OpenAL Soft on macOS/Linux) and N bridge-plugin subscriptions
/// (Discord / Zoom / Mumble / …). Master runs at 48 kHz stereo IEEE float;
/// non-matching inputs must be converted before they're added.
///
/// <para><b>Why buses.</b> Sidechain plugins (the canonical example being
/// a ducker that ducks Music when SFX plays) need to detect on one signal
/// and apply gain to a different signal. A single global mix can't deliver
/// that — by the time the audio reaches the master FX chain, every source
/// is already summed and the ducker has nothing useful to listen to.
/// Splitting the mix into named buses gives sidechain sources discrete
/// signals to subscribe to. Built-in buses are Music / Ambient / SFX (see
/// <see cref="BuiltInBusIds"/>).</para>
///
/// <para><b>Broadcast subscriptions.</b> Pre-Phase-1 we had a single
/// "Discord queue"; Phase 1 generalised the name but still allowed only
/// one consumer because the bounded queue lived directly on the mixer.
/// Two bridges sharing one queue race-dequeued the same chunks — each
/// got fewer than half the audio frames. The fix: each bridge calls
/// <see cref="Subscribe(string)"/> to register its own
/// <see cref="IBroadcastSubscription"/> with a private bounded queue +
/// per-bridge volume. The audio thread enqueues a separate volume-applied
/// copy of every post-mix buffer to every subscriber, so two bridges
/// transmitting simultaneously both see the full audio stream. Bridges
/// always receive the post-master mix (every bus combined + master FX) —
/// there's no per-bus broadcast routing because the GM's expectation is
/// "what plays locally is what plays into the call."</para>
/// </summary>
public class MasterMixer : ISampleProvider, IDisposable
{
    // The combiner is what every BusMixer feeds into. It replaces the
    // pre-bus `_mixer` field that used to be a single global
    // MixingSampleProvider; today every per-track AddMixerInput call lands
    // on a specific bus, the bus mixes its sources, optionally runs the
    // bus FX chain, and the result is added to _combiner as a single
    // ISampleProvider input.
    private readonly MixingSampleProvider _combiner;

    // Lazy-instantiated per-bus mixers. Keyed by Bus.Id. EnsureBus creates
    // a new BusMixer + AddMixerInputs it on the combiner; once created,
    // the BusMixer stays in the combiner for the mixer's lifetime (the
    // user can rename / re-add buses freely without churning the audio
    // chain). Lookups take _busesLock; the audio thread doesn't need it
    // because everything below _combiner is wired at creation time and
    // never mutated.
    private readonly Dictionary<int, BusMixer> _buses = new();
    private readonly object _busesLock = new();

    private ISampleProvider _postProcessedMixer;
    private readonly List<ISamplerInstance> _globalEffects = new();

    /// <summary>Max chunks any single broadcast subscriber may have queued
    /// before further chunks get dropped on its behalf. Per-subscriber so
    /// one slow bridge can't starve another. 50 chunks at 10ms each = 0.5
    /// seconds of audio held — plenty for transient network jitter,
    /// short enough that the user notices a real stall instead of growing
    /// indefinite latency.</summary>
    public const int BroadcastQueueCap = 50;

    // ── Broadcast subscribers (per-bridge fan-out) ─────────────────────
    //
    // Mutating this list (Subscribe / unsubscribe-via-Dispose) takes
    // _subscribersLock. Reading from the audio thread takes a snapshot
    // through Volatile.Read on the count and a defensive copy under the
    // lock when count > 0. Locks happen at most ~100 Hz on the audio
    // thread (per Read cycle) and are uncontended in steady state, so
    // this is cheap.
    private readonly object _subscribersLock = new();
    private readonly List<BroadcastSubscription> _subscribers = new();
    private int _subscriberCount; // volatile-read on audio thread for the fast-path skip

    /// <summary>Fired (off the audio thread) when subscribers join or leave.
    /// MixerViewModel listens so the per-bridge volume sliders can appear /
    /// disappear without a polling loop.</summary>
    public event EventHandler? SubscribersChanged;

    /// <summary>Snapshot of currently-attached subscribers, in
    /// subscribe-order. Safe to enumerate from any thread.</summary>
    public IReadOnlyList<IBroadcastSubscription> Subscribers
    {
        get
        {
            lock (_subscribersLock) return _subscribers.ToArray();
        }
    }

    /// <summary>Register a new broadcast subscriber. The returned
    /// <see cref="IBroadcastSubscription"/> owns its own bounded queue +
    /// volume; dispose to detach it from the mixer. Caller (typically
    /// <c>AudioBridgeHost</c>) is responsible for the lifecycle —
    /// <see cref="MasterMixer.Dispose"/> does not detach orphaned subs.</summary>
    public IBroadcastSubscription Subscribe(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Bridge";
        var sub = new BroadcastSubscription(this, displayName);
        lock (_subscribersLock)
        {
            _subscribers.Add(sub);
            Volatile.Write(ref _subscriberCount, _subscribers.Count);
        }
        SubscribersChanged?.Invoke(this, EventArgs.Empty);
        Log.Info("Audio", $"Broadcast subscriber added: '{displayName}' (total now {_subscriberCount}).");
        return sub;
    }

    internal void RemoveSubscription(BroadcastSubscription sub)
    {
        bool removed;
        lock (_subscribersLock)
        {
            removed = _subscribers.Remove(sub);
            Volatile.Write(ref _subscriberCount, _subscribers.Count);
        }
        if (removed)
        {
            SubscribersChanged?.Invoke(this, EventArgs.Empty);
            Log.Info("Audio", $"Broadcast subscriber removed: '{sub.DisplayName}' (total now {_subscriberCount}).");
        }
    }

    // Guards _globalEffects mutations and the paired RebuildProcessingChain
    // call. The audio thread reads _postProcessedMixer via Volatile.Read
    // (no lock needed for the snapshot reference); mutators hold this lock
    // while assembling the new chain and Volatile-writing it.
    private readonly object _chainLock = new();

    // ── Deferred disposal infrastructure ───────────────────────────────
    //
    // Plugins removed from the audio chain must NOT be disposed
    // synchronously: the audio thread may still be inside Read holding a
    // reference to the removed provider. We tag each disposal with the
    // current "Read generation," and on the next Read cycle we hand
    // items whose generation is at least 1 behind off to a background
    // drainer thread.
    private readonly ConcurrentQueue<(long gen, IDisposable disposable)> _pendingDispose = new();
    private readonly Channel<IDisposable> _disposeChannel = Channel.CreateUnbounded<IDisposable>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _disposeDrainer;
    private readonly CancellationTokenSource _disposeCts = new();
    private long _readCount;

    public WaveFormat WaveFormat => _combiner.WaveFormat;

    // LocalVolume is written from the UI thread (volume slider) and read
    // from the audio thread inside Read. float reads/writes are atomic in
    // the CLR memory model on every supported architecture, but without a
    // memory barrier the audio thread can read a stale value for an
    // unbounded time on weakly-ordered CPUs (ARM). Volatile.Read/Write
    // gives us the barrier without the overhead of Interlocked. Backed
    // by an int field + BitConverter.SingleToInt32Bits because Volatile<T>
    // doesn't include a float overload.
    private int _localVolumeBits = BitConverter.SingleToInt32Bits(1.0f);
    public float LocalVolume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _localVolumeBits));
        set => Volatile.Write(ref _localVolumeBits, BitConverter.SingleToInt32Bits(value));
    }

    public event EventHandler<float[]>? AudioDataAvailable;

    public MasterMixer(int sampleRate = 48000, int channels = 2)
    {
        _combiner = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        _combiner.ReadFully = true; // Always output silence if nothing playing
        _postProcessedMixer = _combiner;

        // Spin up the disposal drainer.
        _disposeDrainer = Task.Run(DrainDisposeChannelAsync);
    }

    /// <summary>Get-or-create the <see cref="BusMixer"/> for
    /// <paramref name="busId"/>. Idempotent — repeated calls for the same
    /// id return the same instance. Newly-created buses are immediately
    /// wired into the combiner so any sources added to them participate
    /// in the master mix on the next audio buffer.</summary>
    public BusMixer EnsureBus(int busId)
    {
        lock (_busesLock)
        {
            if (!_buses.TryGetValue(busId, out var bus))
            {
                bus = new BusMixer(busId, _combiner.WaveFormat);
                _buses[busId] = bus;
                _combiner.AddMixerInput(bus);
                Log.Info("Audio", $"BusMixer created for bus id {busId}.");
            }
            return bus;
        }
    }

    /// <summary>Look up an existing <see cref="BusMixer"/>, or null if the
    /// bus has not yet been used. Does NOT create — call
    /// <see cref="EnsureBus"/> when you need a guaranteed instance.</summary>
    public BusMixer? GetBus(int busId)
    {
        lock (_busesLock)
            return _buses.TryGetValue(busId, out var bus) ? bus : null;
    }

    /// <summary>Snapshot of currently-active bus ids in unspecified order.
    /// Use for diagnostic / smoke-test queries; don't depend on ordering.</summary>
    public IReadOnlyList<int> BusIds
    {
        get { lock (_busesLock) return _buses.Keys.ToArray(); }
    }

    /// <summary>Pre-create bus mixers for every id in <paramref name="busIds"/>.
    /// Called at startup so the bus FX chain service can wire persistent
    /// effects without races. Idempotent.</summary>
    public void EnsureBuses(IEnumerable<int> busIds)
    {
        if (busIds == null) return;
        foreach (var id in busIds) EnsureBus(id);
    }

    /// <summary>Tear down a bus mixer — used when the user deletes a
    /// custom bus from the Settings → Buses page. Removes the bus from
    /// the combiner so its idle silence stops being summed, and drops
    /// the dictionary entry so a subsequent <see cref="EnsureBus"/> on
    /// the same id creates a fresh mixer. Caller is responsible for
    /// detaching any FX attachments first (via
    /// <see cref="ISamplerChainService.RemoveAttachmentsFor"/>); this
    /// method only handles the audio-chain teardown.
    /// <para>No-op when the bus doesn't exist. Built-in bus removal is
    /// allowed at this layer — the UI gates it at the Settings page so
    /// the engine stays policy-free.</para></summary>
    public void RemoveBus(int busId)
    {
        BusMixer? bus;
        lock (_busesLock)
        {
            if (!_buses.TryGetValue(busId, out bus)) return;
            _buses.Remove(busId);
        }
        // RemoveMixerInput on the combiner detaches the bus from the
        // master sum; the BusMixer object is then unreachable except
        // via any audio-thread snapshot still in flight, which the
        // combiner-level lock protects. No DeferDispose needed — the
        // BusMixer holds no plugin instances directly (those are
        // attached individually and were already deferred by the
        // chain-service cleanup).
        _combiner.RemoveMixerInput(bus);
        Log.Info("Audio", $"BusMixer removed for bus id {busId}.");
    }

    /// <summary>Attach a sampler instance to the master bus. The
    /// instance's <see cref="ISamplerInstance.CreateEffect"/> is called
    /// once and the result is appended to the post-mix chain.</summary>
    public void AddGlobalEffect(ISamplerInstance instance)
    {
        lock (_chainLock)
        {
            if (!_globalEffects.Contains(instance))
            {
                _globalEffects.Add(instance);
                RebuildProcessingChain();
                Log.Info("Audio", $"Global effect added. Chain length now {_globalEffects.Count}.");
            }
        }
    }

    /// <summary>Remove a sampler instance from the master bus.</summary>
    public void RemoveGlobalEffect(ISamplerInstance instance)
    {
        lock (_chainLock)
        {
            if (_globalEffects.Remove(instance))
            {
                RebuildProcessingChain();
                Log.Info("Audio", $"Global effect removed. Chain length now {_globalEffects.Count}.");
            }
        }
    }

    /// <summary>Snapshot of attached master-bus effects, in chain order.
    /// Used by the mixer UI to render badges. Returns a defensive copy.</summary>
    public IReadOnlyList<ISamplerInstance> GlobalEffects
    {
        get
        {
            lock (_chainLock)
                return _globalEffects.ToArray();
        }
    }

    /// <summary>Queue a <see cref="IDisposable"/> for safe deferred disposal.</summary>
    public void DeferDispose(IDisposable disposable)
    {
        if (disposable == null) return;
        _pendingDispose.Enqueue((Volatile.Read(ref _readCount), disposable));
    }

    internal void FlushPendingDisposalsForTest()
    {
        while (_pendingDispose.TryDequeue(out var pending))
            _disposeChannel.Writer.TryWrite(pending.disposable);
    }

    internal int PendingDisposalCount => _pendingDispose.Count;

    private void RebuildProcessingChain()
    {
        ISampleProvider chain = _combiner;
        foreach (var effect in _globalEffects)
        {
            var next = effect.CreateEffect(chain);
            if (next == null)
            {
                Log.Error("Audio", "An ISamplerInstance returned null from CreateEffect — skipping it in the chain.",
                    new InvalidOperationException("null effect chain"));
                continue;
            }
            chain = next;
        }
        Volatile.Write(ref _postProcessedMixer, chain);
    }

    /// <summary>Add an audio source to the default bus
    /// (<see cref="BuiltInBusIds.DefaultForNewTracks"/>). Kept for the few
    /// callers (tests, legacy startup hooks) that don't yet route per-bus.
    /// Production paths should call the bus-aware overload instead so the
    /// signal lands where its Track.BusId expects it.</summary>
    public void AddMixerInput(ISampleProvider provider) =>
        AddMixerInput(provider, BuiltInBusIds.DefaultForNewTracks);

    /// <summary>Route <paramref name="provider"/> into the given bus's
    /// internal mixer. The bus is lazily created if needed. Caller is
    /// responsible for sample-rate / channel conversion to
    /// <see cref="WaveFormat"/> before calling.</summary>
    public void AddMixerInput(ISampleProvider provider, int busId) =>
        EnsureBus(busId).AddMixerInput(provider);

    /// <summary>Remove a previously-added source. The provider may be on
    /// any bus — every bus's internal mixer is asked to remove it.
    /// MixingSampleProvider's RemoveMixerInput is a no-op on unknown
    /// providers so iterating every bus is safe.</summary>
    public void RemoveMixerInput(ISampleProvider provider)
    {
        BusMixer[] snapshot;
        lock (_busesLock) snapshot = _buses.Values.ToArray();
        foreach (var bus in snapshot) bus.RemoveMixerInput(provider);
    }

    /// <summary>Attach <paramref name="instance"/> to the named bus's FX
    /// chain (the chain that runs AFTER the bus's internal MixingSampleProvider
    /// but BEFORE the bus signal feeds into the master combine). The bus
    /// is created lazily if needed.</summary>
    public void AddBusEffect(int busId, ISamplerInstance instance) =>
        EnsureBus(busId).AddEffect(instance);

    /// <summary>Detach a previously-attached bus effect. No-op when the
    /// bus doesn't exist (e.g. removal racing with library switch).
    /// Disposal of the instance is the caller's job — pass it to
    /// <see cref="DeferDispose"/> after removing.</summary>
    public void RemoveBusEffect(int busId, ISamplerInstance instance)
    {
        BusMixer? bus;
        lock (_busesLock) _buses.TryGetValue(busId, out bus);
        bus?.RemoveEffect(instance);
    }

    /// <summary>Snapshot of the named bus's FX chain. Returns empty when
    /// the bus has not been created.</summary>
    public IReadOnlyList<ISamplerInstance> GetBusEffects(int busId)
    {
        BusMixer? bus;
        lock (_busesLock) _buses.TryGetValue(busId, out bus);
        return bus?.Effects ?? Array.Empty<ISamplerInstance>();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long gen = Interlocked.Increment(ref _readCount);

        // Drain disposals at least one Read cycle old.
        while (_pendingDispose.TryPeek(out var head) && head.gen < gen - 1)
        {
            if (_pendingDispose.TryDequeue(out var actual))
                _disposeChannel.Writer.TryWrite(actual.disposable);
        }

        var pool = ArrayPool<float>.Shared;
        float[] tempBuffer = pool.Rent(count);
        try
        {
            var chain = Volatile.Read(ref _postProcessedMixer);
            int read = chain.Read(tempBuffer, 0, count);

            float localVol = LocalVolume;

            // Branch 1: Broadcast fan-out to subscribers.
            // Fast-path skip via volatile count read — when no bridge is
            // connected we avoid both the lock and the chunk allocation.
            // When subscribers exist we take the lock to snapshot the
            // list, then build a separate volume-applied chunk per sub
            // so they don't race over a shared array.
            if (Volatile.Read(ref _subscriberCount) > 0)
            {
                BroadcastSubscription[] snapshot;
                lock (_subscribersLock) snapshot = _subscribers.ToArray();

                foreach (var sub in snapshot)
                {
                    if (sub.QueuedCount >= BroadcastQueueCap)
                    {
                        sub.NoteDrop();
                        continue;
                    }
                    float vol = sub.Volume;
                    float[] subChunk = new float[read];
                    for (int i = 0; i < read; i++)
                        subChunk[i] = tempBuffer[i] * vol;
                    sub.Enqueue(subChunk);
                }
            }

            // Branch 2: Local Output
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] = tempBuffer[i] * localVol;
            }

            // Branch 3: visualizer broadcast. Copies from tempBuffer (a
            // real float[]) — NOT from buffer (which is a WaveBuffer
            // alias over a byte[] and would corrupt the visualizer if we
            // tried Array.Copy).
            if (AudioDataAvailable != null && read > 0)
            {
                float[] visualizerChunk = new float[read];
                for (int i = 0; i < read; i++)
                    visualizerChunk[i] = tempBuffer[i] * localVol;
                AudioDataAvailable.Invoke(this, visualizerChunk);
            }

            return read;
        }
        finally
        {
            pool.Return(tempBuffer);
        }
    }

    private async Task DrainDisposeChannelAsync()
    {
        var reader = _disposeChannel.Reader;
        try
        {
            await foreach (var disposable in reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Log.Warn("Audio", $"Deferred dispose threw: {ex.Message}", ex); }
            }
        }
        catch (OperationCanceledException)
        {
            while (reader.TryRead(out var d))
            {
                try { d.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        // Drain anything still parked in _pendingDispose into the channel
        // before completing the writer. Without this, items enqueued by
        // ReplaceMixerInput/ReplaceGlobalChain that hadn't yet crossed the
        // two-generation safety boundary would be silently dropped — those
        // are real IDisposables (sampler instances with native or thread
        // handles) and skipping their Dispose leaks past app shutdown.
        while (_pendingDispose.TryDequeue(out var pending))
            _disposeChannel.Writer.TryWrite(pending.disposable);
        _disposeChannel.Writer.TryComplete();
        _disposeCts.Cancel();
        try { _disposeDrainer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _disposeCts.Dispose();
    }
}

/// <summary>
/// One broadcast subscriber's view of the mixer — its own bounded queue,
/// its own volume slider, its own drop-count diagnostic. Created via
/// <see cref="MasterMixer.Subscribe(string)"/>; the bridge plugin keeps
/// the returned handle for the lifetime of its connection and disposes
/// to detach.
///
/// <para><b>Why per-subscription queues:</b> a single shared queue across
/// multiple subscribers is broken — TryDequeue is destructive, so two
/// bridges race-consume the same chunks, each getting roughly 1/N of the
/// audio. With per-subscription queues, the audio thread fans out N
/// volume-applied copies per cycle and each bridge sees the full
/// stream.</para>
///
/// <para>Threading: <see cref="Volume"/> is written from the UI thread
/// (per-bridge slider) and read from the audio thread (every Read cycle).
/// Same Volatile.Int32 bit-pattern trick as <c>MasterMixer.LocalVolume</c>.
/// <see cref="TryDequeue"/> is called from the bridge's worker thread.</para>
/// </summary>
public interface IBroadcastSubscription : IDisposable
{
    /// <summary>Human-readable name shown on the mixer card / volume
    /// slider for this subscriber. Set at <see cref="MasterMixer.Subscribe(string)"/>
    /// time; not user-editable after.</summary>
    string DisplayName { get; }

    /// <summary>Linear gain applied to every chunk enqueued for this
    /// subscriber. 1.0 = unity. 0.0 = silent. Read on the audio thread
    /// once per buffer; safe to set from any thread.</summary>
    float Volume { get; set; }

    /// <summary>Pull the next ready chunk if any. Bridge workers call
    /// this on their own thread. Returns false (and chunk = null) when
    /// the queue is empty.</summary>
    bool TryDequeue(out float[]? chunk);

    /// <summary>Total chunks dropped because this subscriber's queue
    /// was full. Useful for surfacing "your bridge is too slow" in the
    /// mixer card.</summary>
    long DropCount { get; }

    /// <summary>Zero the drop counter — for a "reset diagnostic" button
    /// in the bridge's settings UI.</summary>
    void ResetDropCount();
}

/// <summary>Internal subscription implementation. Lives in this file so
/// it can access <see cref="MasterMixer"/>'s private removal hook.</summary>
internal sealed class BroadcastSubscription : IBroadcastSubscription
{
    private readonly MasterMixer _owner;
    private readonly ConcurrentQueue<float[]> _queue = new();
    private int _volumeBits = BitConverter.SingleToInt32Bits(1.0f);
    private long _dropCount;
    private int _queuedCount; // tracked separately so the audio thread doesn't pay for ConcurrentQueue.Count's full traversal
    private bool _disposed;

    public string DisplayName { get; }

    public float Volume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _volumeBits));
        set => Volatile.Write(ref _volumeBits, BitConverter.SingleToInt32Bits(value));
    }

    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>Lock-free count tracked alongside the queue. Read on the
    /// audio thread to short-circuit before enqueueing a full queue;
    /// without this we'd pay for ConcurrentQueue.Count, which walks the
    /// segment list. Updated with Interlocked on Enqueue / TryDequeue.</summary>
    public int QueuedCount => Volatile.Read(ref _queuedCount);

    public BroadcastSubscription(MasterMixer owner, string displayName)
    {
        _owner = owner;
        DisplayName = displayName;
    }

    public void Enqueue(float[] chunk)
    {
        if (_disposed) return;
        _queue.Enqueue(chunk);
        Interlocked.Increment(ref _queuedCount);
    }

    public bool TryDequeue(out float[]? chunk)
    {
        bool got = _queue.TryDequeue(out chunk);
        if (got) Interlocked.Decrement(ref _queuedCount);
        return got;
    }

    public void NoteDrop() => Interlocked.Increment(ref _dropCount);

    public void ResetDropCount() => Interlocked.Exchange(ref _dropCount, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.RemoveSubscription(this);
        // Drain so we don't hand back chunks after Dispose returns.
        while (_queue.TryDequeue(out _)) { }
        Volatile.Write(ref _queuedCount, 0);
    }
}
