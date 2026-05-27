using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.PluginApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoundBoard.Core.Services;

/// <summary>
/// Host-side dispatcher for <see cref="IAudioBridgePlugin"/> instances.
/// The interface contract lives in <c>SoundBoard.PluginApi</c>; this is
/// the missing other half that owns the per-bridge worker threads and
/// the mapping between a bridge plugin and its
/// <see cref="IBroadcastSubscription"/> on the master mixer.
///
/// <para><b>Subscription lifecycle.</b> Each bridge transitions between
/// <see cref="BridgeStatus.Disconnected"/>, <see cref="BridgeStatus.Connecting"/>,
/// <see cref="BridgeStatus.Connected"/>, and <see cref="BridgeStatus.Failed"/>
/// as it talks to its remote. When a bridge reaches
/// <see cref="BridgeStatus.Connected"/> we create an
/// <see cref="IBroadcastSubscription"/> for it on the master mixer — the
/// mixer will start fanning audio chunks into that subscription's queue
/// from the next Read cycle onwards. When the bridge leaves Connected
/// (Disconnected / Failed) we dispose the subscription so the mixer
/// stops allocating audio for it. Two bridges connected simultaneously
/// means two subscriptions and two parallel audio streams; one bridge's
/// network stall has no effect on the other.</para>
///
/// <para><b>Per-bridge worker threads.</b> Each registered bridge gets a
/// background thread that polls its subscription. The pull model + bounded
/// queue means a slow bridge cannot block the audio thread or starve
/// faster bridges — the mixer just notes a drop on the slow bridge's
/// counter and keeps going.</para>
/// </summary>
public sealed class AudioBridgeHost : IDisposable
{
    private readonly MasterMixer _masterMixer;
    private readonly List<BridgeWorker> _workers = new();
    private readonly object _gate = new();
    private bool _disposed;

    public AudioBridgeHost(MasterMixer masterMixer)
    {
        _masterMixer = masterMixer;
    }

    /// <summary>One-shot registration after every bridge plugin has
    /// finished <see cref="IPlugin.Initialize"/>. Each bridge gets a
    /// dedicated worker thread; subscriptions are created lazily when
    /// the bridge reports <see cref="BridgeStatus.Connected"/>.</summary>
    public void RegisterBridges(IEnumerable<IAudioBridgePlugin> bridges)
    {
        lock (_gate)
        {
            foreach (var bridge in bridges)
            {
                var worker = new BridgeWorker(bridge, _masterMixer, this);
                _workers.Add(worker);
                bridge.StatusChanged += OnBridgeStatusChanged;
                worker.Start();
                Log.Info("Bridge", $"Registered '{bridge.Name}' ({bridge.Id}) — worker started");
            }
        }
    }

    /// <summary>The <see cref="IBridgeHost"/> handle a bridge should use
    /// for pushing received audio into the mixer. One handle per
    /// registered bridge; returns null for bridges that aren't registered
    /// (test paths, race during shutdown).</summary>
    public IBridgeHost? GetHostFor(IAudioBridgePlugin bridge)
    {
        lock (_gate)
            return _workers.FirstOrDefault(w => ReferenceEquals(w.Bridge, bridge))?.HostHandle;
    }

    /// <summary>Snapshot of the workers currently registered. Used by the
    /// mixer view to surface per-bridge volume sliders for connected
    /// bridges. The returned tuples are immutable snapshots — query
    /// <see cref="IAudioBridgePlugin.Status"/> for live state.</summary>
    public IReadOnlyList<IAudioBridgePlugin> RegisteredBridges
    {
        get
        {
            lock (_gate) return _workers.Select(w => w.Bridge).ToArray();
        }
    }

    private void OnBridgeStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not IAudioBridgePlugin bridge)
        {
            Log.Debug("Bridge", $"StatusChanged from non-IAudioBridgePlugin sender ({sender?.GetType().FullName ?? "null"}) — ignoring.");
            return;
        }

        BridgeWorker? worker;
        lock (_gate)
            worker = _workers.FirstOrDefault(w => ReferenceEquals(w.Bridge, bridge));
        if (worker == null)
        {
            Log.Debug("Bridge", $"StatusChanged for unregistered bridge '{bridge.Id}' — ignoring.");
            return;
        }

        Log.Debug("Bridge",
            $"Bridge '{bridge.Name}' status → {bridge.Status} (wants outbound = {bridge.WantsOutboundAudio})");

        // The transitions that matter for subscription lifecycle:
        //
        //   anywhere → Connected:  create a fresh subscription
        //   Connected → anything:  dispose the subscription
        //
        // Failed and Disconnected both mean "stop sending audio." We
        // also tolerate WantsOutboundAudio being false even while
        // Connected (a bridge that's connected to the gateway but not
        // yet ready to receive PCM) by gating on that flag in the worker.
        if (bridge.Status == BridgeStatus.Connected && bridge.WantsOutboundAudio)
        {
            worker.EnsureSubscribed();
        }
        else
        {
            worker.EnsureUnsubscribed();
        }
    }

    /// <summary>Synchronously call <see cref="IAudioBridgePlugin.DisconnectAsync"/>
    /// on every currently-connected bridge, blocking up to
    /// <paramref name="timeoutPerBridge"/> per bridge. Intended for the
    /// app-close shutdown path: we want Discord / Zoom / Mumble to see a
    /// proper "leaving" message before the process terminates, otherwise
    /// the bot lingers in the remote channel until the platform's session
    /// timeout (usually 30–60s for Discord voice).
    ///
    /// <para>Bridges that hang or throw are abandoned after the timeout
    /// fires — the warning lands in the log, but the rest of shutdown
    /// continues. Bridges that report <see cref="BridgeStatus.Disconnected"/>
    /// are skipped (no work to do).</para>
    ///
    /// <para>Call this BEFORE <see cref="Dispose"/>: dispose kills the
    /// per-bridge worker threads but does NOT itself initiate a clean
    /// network disconnect.</para>
    /// </summary>
    public void DisconnectAllBridges(TimeSpan timeoutPerBridge)
    {
        BridgeWorker[] snapshot;
        lock (_gate) snapshot = _workers.ToArray();

        foreach (var w in snapshot)
        {
            if (w.Bridge.Status == BridgeStatus.Disconnected) continue;

            try
            {
                Log.Info("Bridge", $"Disconnecting '{w.Bridge.Name}' on app close…");
                var task = w.Bridge.DisconnectAsync();
                if (!task.Wait(timeoutPerBridge))
                {
                    Log.Warn("Bridge",
                        $"Bridge '{w.Bridge.Name}' did not disconnect within {timeoutPerBridge.TotalSeconds:F0}s — abandoning. " +
                        "The remote may show the bot as connected until its session timeout fires.");
                }
                else
                {
                    Log.Info("Bridge", $"Bridge '{w.Bridge.Name}' disconnected cleanly.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Bridge", $"Bridge '{w.Bridge.Name}' threw from DisconnectAsync during shutdown", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var w in _workers)
            {
                try { w.Bridge.StatusChanged -= OnBridgeStatusChanged; } catch { }
                w.Dispose();
            }
            _workers.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Per-bridge worker.
    // ──────────────────────────────────────────────────────────────────

    private sealed class BridgeWorker : IDisposable
    {
        public IAudioBridgePlugin Bridge { get; }
        public BridgeHostHandle HostHandle { get; }

        private readonly MasterMixer _mixer;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _subLock = new();
        private IBroadcastSubscription? _subscription;
        private Thread? _thread;

        public BridgeWorker(IAudioBridgePlugin bridge, MasterMixer mixer, AudioBridgeHost host)
        {
            Bridge = bridge;
            _mixer = mixer;
            HostHandle = new BridgeHostHandle(mixer);
        }

        public void Start()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = $"BridgeWorker:{Bridge.Id}",
            };
            _thread.Start();
        }

        /// <summary>Attach a fresh subscription to the master mixer for
        /// this bridge, if not already attached. Idempotent. Called from
        /// the bridge's StatusChanged handler when the bridge reaches
        /// Connected, and also from the worker loop as a defensive
        /// re-check (handles the StatusChanged event arriving before
        /// the worker has noticed WantsOutboundAudio).</summary>
        public void EnsureSubscribed()
        {
            lock (_subLock)
            {
                if (_subscription != null) return;
                _subscription = _mixer.Subscribe(Bridge.Name);
            }
        }

        /// <summary>Detach the subscription so the master mixer stops
        /// allocating audio for this bridge. Idempotent.</summary>
        public void EnsureUnsubscribed()
        {
            IBroadcastSubscription? toDispose = null;
            lock (_subLock)
            {
                if (_subscription != null)
                {
                    toDispose = _subscription;
                    _subscription = null;
                }
            }
            toDispose?.Dispose();
        }

        private void RunLoop()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!Bridge.WantsOutboundAudio)
                    {
                        // Bridge isn't connected (or temporarily paused).
                        // Make sure we're not holding a stale subscription.
                        EnsureUnsubscribed();
                        Thread.Sleep(50);
                        continue;
                    }

                    // Bridge wants audio. Defensive: if the StatusChanged
                    // handler missed us (could happen on the first
                    // Connected event when the handler fires before
                    // WantsOutboundAudio is flipped), self-subscribe.
                    EnsureSubscribed();

                    IBroadcastSubscription? sub;
                    lock (_subLock) sub = _subscription;
                    if (sub == null) { Thread.Sleep(2); continue; }

                    bool any = false;
                    while (sub.TryDequeue(out var chunk) && chunk != null)
                    {
                        any = true;
                        try
                        {
                            Bridge.SendOutboundPcm(chunk);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Bridge",
                                $"Bridge '{Bridge.Id}' threw from SendOutboundPcm; chunk dropped",
                                ex);
                        }
                    }
                    if (!any) Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Bridge", $"BridgeWorker:{Bridge.Id} loop crashed", ex);
            }
            finally
            {
                EnsureUnsubscribed();
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
            EnsureUnsubscribed();
            HostHandle.Dispose();
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Concrete <see cref="IBridgeHost"/> for one bridge. Wraps inbound-PCM
/// pushes as ad-hoc <see cref="ISampleProvider"/> sources added to the
/// mixer. One handle per registered bridge.
/// </summary>
internal sealed class BridgeHostHandle : IBridgeHost, IDisposable
{
    private readonly MasterMixer _mixer;
    private readonly List<HostedInboundSink> _sinks = new();
    private InboundQueueProvider? _singleAnonymousSink;
    private readonly object _gate = new();
    private bool _disposed;

    public BridgeHostHandle(MasterMixer mixer) { _mixer = mixer; }

    public void PushInboundPcm(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return;
        InboundQueueProvider provider;
        lock (_gate)
        {
            // Reject pushes after Dispose so a worker thread that
            // races a bridge disconnect doesn't enqueue into a queue
            // no one's reading. Pre-fix the worker would have built
            // a fresh _singleAnonymousSink, added it to the mixer,
            // and then leaked it when Dispose's earlier RemoveMixerInput
            // had already run with a null reference.
            if (_disposed) return;
            if (_singleAnonymousSink == null)
            {
                _singleAnonymousSink = new InboundQueueProvider("Bridge In");
                _mixer.AddMixerInput(_singleAnonymousSink);
            }
            provider = _singleAnonymousSink;
        }
        provider.Enqueue(pcm);
    }

    public IInboundAudioSink OpenInboundStream(string displayName)
    {
        var sink = new HostedInboundSink(displayName, _mixer, this);
        lock (_gate) _sinks.Add(sink);
        _mixer.AddMixerInput(sink.Provider);
        return sink;
    }

    internal void OnSinkClosed(HostedInboundSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
        _mixer.RemoveMixerInput(sink.Provider);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_singleAnonymousSink != null)
            {
                _mixer.RemoveMixerInput(_singleAnonymousSink);
                _singleAnonymousSink = null;
            }
            foreach (var s in _sinks)
                _mixer.RemoveMixerInput(s.Provider);
            _sinks.Clear();
        }
    }
}

internal sealed class HostedInboundSink : IInboundAudioSink
{
    public string DisplayName { get; }
    public InboundQueueProvider Provider { get; }
    private readonly BridgeHostHandle _owner;
    private bool _disposed;

    public HostedInboundSink(string displayName, MasterMixer mixer, BridgeHostHandle owner)
    {
        DisplayName = displayName;
        Provider = new InboundQueueProvider(displayName);
        _owner = owner;
    }

    public void Push(ReadOnlySpan<float> pcm)
    {
        if (_disposed) return;
        Provider.Enqueue(pcm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.OnSinkClosed(this);
    }
}

internal sealed class InboundQueueProvider : ISampleProvider
{
    private readonly ConcurrentQueue<float[]> _queue = new();
    private float[]? _currentChunk;
    private int _currentChunkOffset;

    public string DisplayName { get; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public InboundQueueProvider(string displayName)
    {
        DisplayName = displayName;
    }

    public void Enqueue(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return;
        var copy = new float[pcm.Length];
        pcm.CopyTo(copy);
        _queue.Enqueue(copy);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_currentChunk == null)
            {
                if (!_queue.TryDequeue(out _currentChunk))
                {
                    Array.Clear(buffer, offset + written, count - written);
                    return count;
                }
                _currentChunkOffset = 0;
            }

            int available = _currentChunk.Length - _currentChunkOffset;
            int take = Math.Min(available, count - written);
            Array.Copy(_currentChunk, _currentChunkOffset, buffer, offset + written, take);
            written += take;
            _currentChunkOffset += take;
            if (_currentChunkOffset >= _currentChunk.Length)
                _currentChunk = null;
        }
        return count;
    }
}
