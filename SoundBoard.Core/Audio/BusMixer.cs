using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoard.Core.Logging;
using SoundBoard.PluginApi;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SoundBoard.Core.Audio;

/// <summary>
/// One audio bus — an isolated <see cref="MixingSampleProvider"/> with its
/// own per-bus FX chain. Lives inside <see cref="MasterMixer"/>; the master
/// owns a <c>BusMixer</c> per <see cref="Models.Bus"/> row and combines
/// every bus into the post-mix master output.
///
/// <para><b>Why separate from MasterMixer.</b> The whole reason buses
/// exist is so a sidechain plugin (a ducker, say) can detect on the SFX
/// stream and apply gain to the Music stream — that requires the two
/// signals to be observable independently before they hit the master
/// combine. A bus is the cheapest unit of "an isolated mix the host can
/// hand to a plugin." Each BusMixer is itself an <see cref="ISampleProvider"/>
/// so the combiner inside MasterMixer just <c>AddMixerInput</c>s every
/// bus — no special-case plumbing.</para>
///
/// <para><b>Threading.</b> Two locks:
/// <list type="bullet">
///   <item><c>_chainLock</c> guards the effect list. <c>Rebuild()</c> may
///   call into plugin code (a plugin's <see cref="ISamplerInstance.CreateEffect"/>
///   could allocate or stall), so the lock must NOT be the one the audio
///   thread takes per-cycle.</item>
///   <item><c>_subscribersLock</c> guards the sidechain subscriber list,
///   which IS read by the audio thread. The audio thread reads a
///   Volatile-published snapshot built at mutation time, so the per-cycle
///   path doesn't take the lock at all.</item>
/// </list>
/// Pre-split there was one combined <c>_lock</c>; a slow plugin
/// <c>CreateEffect</c> could stall the audio thread the next time it
/// went to snapshot subscribers.</para>
///
/// <para><b>Sidechain buffer allocation.</b> Each subscriber receives a
/// pooled <c>float[]</c> rented from <see cref="ArrayPool{T}"/>. Pre-fix
/// every Read cycle allocated <c>new float[read]</c> per subscriber
/// (~6 MB/s Gen0 garbage with one ducker on one bus). The pooled
/// version hands out the array to the callback (read-only contract: do
/// not retain), returns it after, so the steady state is allocation-free
/// on the audio thread.</para>
/// </summary>
public class BusMixer : ISampleProvider
{
    /// <summary>The <see cref="Models.Bus.Id"/> this mixer is bound to.
    /// Stable for the lifetime of the BusMixer; used by callers (engine,
    /// chain service) to look the mixer up.</summary>
    public int BusId { get; }

    private readonly MixingSampleProvider _internal;

    // ── Effect chain state — guarded by _chainLock ─────────────────────
    // _chainLock can be taken on the UI thread (AddEffect / RemoveEffect)
    // and the audio-thread Read does NOT touch it — Read reads _chain
    // through Volatile.Read instead. So a plugin's CreateEffect that
    // blocks (file I/O, allocation stall) can't stall the audio thread.
    private readonly List<ISamplerInstance> _effects = new();
    private readonly object _chainLock = new();
    private ISampleProvider _chain;

    // ── Sidechain push subscribers — guarded by _subscribersLock ───────
    // The list itself is mutated only under the lock. The audio thread
    // reads a snapshot reference published via Volatile, so the per-cycle
    // path is lock-free.
    private readonly object _subscribersLock = new();
    private readonly List<Action<float[], int>> _sidechainSubscribers = new();
    // Empty-array singleton avoids alloc when nobody is subscribed.
    private static readonly Action<float[], int>[] _noSubscribers = Array.Empty<Action<float[], int>>();
    // The snapshot the audio thread reads. Volatile-published on every
    // mutation; the audio thread never takes _subscribersLock.
    private Action<float[], int>[] _subscribersSnapshot = _noSubscribers;

    /// <summary>Snapshot count of sidechain subscribers. Provided for
    /// diagnostics + tests; matches what the audio thread sees.</summary>
    public int SidechainSubscriberCount => Volatile.Read(ref _subscribersSnapshot).Length;

    /// <summary>Subscribe a push callback to this bus's post-FX audio.
    /// The callback runs on the audio thread once per Read; it receives
    /// a fresh array (do not retain the reference) and a sample-count
    /// argument. Dispose the returned handle to detach. Safe to call
    /// from any thread; safe to dispose the handle from inside the
    /// callback (the unsubscribe is queued and applied at the next
    /// Read boundary).</summary>
    public IDisposable SubscribeSidechain(Action<float[], int> onSamples)
    {
        if (onSamples == null) throw new ArgumentNullException(nameof(onSamples));
        lock (_subscribersLock)
        {
            _sidechainSubscribers.Add(onSamples);
            // Republish snapshot. The audio thread reads via Volatile on
            // the snapshot field; the new array is then visible on the
            // very next Read cycle.
            Volatile.Write(ref _subscribersSnapshot, _sidechainSubscribers.ToArray());
        }
        return new SidechainSubscription(this, onSamples);
    }

    private void RemoveSidechainSubscriber(Action<float[], int> callback)
    {
        lock (_subscribersLock)
        {
            if (_sidechainSubscribers.Remove(callback))
                Volatile.Write(ref _subscribersSnapshot, _sidechainSubscribers.ToArray());
        }
    }

    private sealed class SidechainSubscription : IDisposable
    {
        private readonly BusMixer _owner;
        private readonly Action<float[], int> _callback;
        private bool _disposed;
        public SidechainSubscription(BusMixer owner, Action<float[], int> callback)
        {
            _owner = owner;
            _callback = callback;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.RemoveSidechainSubscriber(_callback);
        }
    }

    // Per-bus linear gain. Same Volatile.Int32 bit-pattern trick
    // MasterMixer.LocalVolume uses — the audio thread reads it once per
    // buffer with a memory barrier, the UI thread writes it from the
    // bus mixer slider. float reads/writes are atomic on every supported
    // arch but on weakly-ordered CPUs (ARM) a non-barriered write can
    // remain invisible to the audio thread for an unbounded time.
    private int _volumeBits = BitConverter.SingleToInt32Bits(1.0f);

    /// <summary>Per-bus linear gain applied AFTER the FX chain runs but
    /// BEFORE the bus's signal feeds into the master combine. 1.0 = unity.
    /// Safe to read/write from any thread.</summary>
    public float Volume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _volumeBits));
        set => Volatile.Write(ref _volumeBits, BitConverter.SingleToInt32Bits(value));
    }

    public WaveFormat WaveFormat => _internal.WaveFormat;

    /// <summary>Build a new bus that reads at <paramref name="format"/>.
    /// The internal <see cref="MixingSampleProvider"/> has
    /// <c>ReadFully = true</c> so the bus always returns silence when
    /// idle — letting the parent combiner sum it without short reads.</summary>
    public BusMixer(int busId, WaveFormat format)
    {
        BusId = busId;
        _internal = new MixingSampleProvider(format);
        _internal.ReadFully = true;
        _chain = _internal;
    }

    /// <summary>Add a source that mixes into this bus. Caller is responsible
    /// for sample-rate / channel conversion to <see cref="WaveFormat"/>
    /// before adding — same contract as <see cref="MasterMixer.AddMixerInput(ISampleProvider)"/>.</summary>
    public void AddMixerInput(ISampleProvider provider) => _internal.AddMixerInput(provider);

    /// <summary>Remove a previously-added source. Idempotent.</summary>
    public void RemoveMixerInput(ISampleProvider provider) => _internal.RemoveMixerInput(provider);

    /// <summary>Append <paramref name="instance"/> to this bus's FX chain.
    /// The plugin's <see cref="ISamplerInstance.CreateEffect"/> is called
    /// once and the result becomes the new tail of the chain. Idempotent
    /// — a duplicate add returns silently.</summary>
    public void AddEffect(ISamplerInstance instance)
    {
        lock (_chainLock)
        {
            if (_effects.Contains(instance)) return;
            _effects.Add(instance);
            Rebuild();
        }
        Log.Info("Audio", $"Bus {BusId}: effect added (chain length {_effects.Count}).");
    }

    /// <summary>Remove a previously-attached effect from this bus's chain.
    /// Caller still owns disposal — pass the instance to
    /// <see cref="MasterMixer.DeferDispose"/> AFTER removing so the audio
    /// thread can finish whatever Read cycle still references it.</summary>
    public void RemoveEffect(ISamplerInstance instance)
    {
        bool removed;
        int count;
        lock (_chainLock)
        {
            removed = _effects.Remove(instance);
            count = _effects.Count;
            if (removed) Rebuild();
        }
        if (removed) Log.Info("Audio", $"Bus {BusId}: effect removed (chain length {count}).");
    }

    /// <summary>Snapshot of currently-attached effects in chain order. Same
    /// defensive-copy contract as <see cref="MasterMixer.GlobalEffects"/>:
    /// safe to enumerate from any thread without holding a lock.</summary>
    public IReadOnlyList<ISamplerInstance> Effects
    {
        get { lock (_chainLock) return _effects.ToArray(); }
    }

    private void Rebuild()
    {
        // Caller holds _chainLock.
        ISampleProvider chain = _internal;
        foreach (var effect in _effects)
        {
            ISampleProvider? next;
            try { next = effect.CreateEffect(chain); }
            catch (Exception ex)
            {
                Log.Error("Audio", $"Bus {BusId}: ISamplerInstance.CreateEffect threw — skipping.", ex);
                continue;
            }
            if (next == null)
            {
                Log.Error("Audio", $"Bus {BusId}: an ISamplerInstance returned null from CreateEffect — skipping.",
                    new InvalidOperationException("null effect chain"));
                continue;
            }
            chain = next;
        }
        Volatile.Write(ref _chain, chain);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var chain = Volatile.Read(ref _chain);
        int read = chain.Read(buffer, offset, count);

        // Sidechain fan-out runs BEFORE the volume multiply so trigger
        // sources see the bus's actual signal level coming out of the
        // FX chain, not the user-attenuated output. Without this a GM
        // who pulled their SFX bus down to 30% for monitoring would
        // accidentally also dial back the ducker's sensitivity by 70%.
        //
        // Lock-free path:
        //   1. Volatile.Read the published subscriber snapshot. Length
        //      zero == nobody listening, skip the whole branch.
        //   2. For each subscriber, rent a float[] from the shared pool,
        //      copy the post-FX buffer in with an alias-safe loop (NOT
        //      Buffer.BlockCopy — see the WaveBuffer aliasing note in
        //      MasterMixer.cs), invoke, return.
        // The callback contract is "do not retain the reference"; the
        // pool reuses the array on the next cycle.
        var subscribers = Volatile.Read(ref _subscribersSnapshot);
        if (subscribers.Length > 0 && read > 0)
        {
            var pool = ArrayPool<float>.Shared;
            foreach (var cb in subscribers)
            {
                float[] copy = pool.Rent(read);
                try
                {
                    // Alias-safe copy: the indexer is JIT-compiled
                    // against float[] and works correctly even when
                    // `buffer` is a WaveBuffer alias over a byte[].
                    // Array.Copy / Buffer.BlockCopy inspect the runtime
                    // element type and silently produce garbage in that
                    // case — don't use them on the input buffer.
                    for (int i = 0; i < read; i++)
                        copy[i] = buffer[offset + i];

                    cb(copy, read);
                }
                catch (Exception ex)
                {
                    // Sidechain callbacks must not throw — log and
                    // continue. A faulty plugin shouldn't stall the
                    // audio thread or block other subscribers.
                    Log.Warn("Audio", $"Bus {BusId}: sidechain callback threw: {ex.Message}");
                }
                finally
                {
                    pool.Return(copy);
                }
            }
        }

        // Apply per-bus gain. Unity (1.0) is the common case so skip the
        // multiply when we can. Sub-unity attenuation is the typical
        // user gesture (ducking a too-loud SFX bus); super-unity is also
        // supported up to whatever the slider exposes (the UI caps at
        // 200%, matching MasterMixer.LocalVolume).
        float vol = Volume;
        if (vol != 1.0f)
        {
            for (int i = 0; i < read; i++)
                buffer[offset + i] *= vol;
        }
        return read;
    }
}
