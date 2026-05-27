using NAudio.Wave;
using Silk.NET.OpenAL;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Linux audio backend backed by OpenAL Soft via Silk.NET.OpenAL.
///
/// <para><b>Why OpenAL on Linux.</b> The miniaudio bundled native we use on
/// macOS heap-corrupts on bleeding-edge Linux userspace — confirmed via a
/// core dump on Ubuntu 25.10 (Mesa 26.0.3 / libpulsecommon-17). The
/// corruption is inside miniaudio's PulseAudio backend integration; the
/// bundled .so was built against older glibc and the ABI no longer holds.
/// Rather than vendoring our own miniaudio build with
/// <c>MA_NO_PULSEAUDIO</c>, we use OpenAL Soft on Linux because:
/// <list type="bullet">
///   <item>OpenAL Soft has hardened backend selection (PipeWire →
///   PulseAudio → ALSA → JACK) developed over 20 years of game-engine
///   use. Each backend has its own <c>dlopen</c> and is sandboxed against
///   failures in the others.</item>
///   <item>The <c>Silk.NET.OpenAL.Soft.Native</c> NuGet ships per-RID
///   natives built against current glibc.</item>
///   <item>Runtime escape hatch: <c>ALSOFT_DRIVERS=alsa</c> env var
///   forces ALSA-only if a future Linux distro breaks libpulse for
///   OpenAL too — same shape as the workaround we'd write ourselves.</item>
/// </list></para>
///
/// <para><b>How streaming works.</b> OpenAL is buffer-push, not pull. Each
/// playing sound has a source, the source has a queue of buffers, the OS
/// audio thread drains the queue. To bridge that with our pull-model
/// <see cref="ISampleProvider"/>, we run a small background thread that:
/// <list type="number">
///   <item>Pre-fills <see cref="QueueDepth"/> buffers at startup and
///   queues them on the source.</item>
///   <item>Calls <c>alSourcePlay</c>.</item>
///   <item>Loops: poll <c>AL_BUFFERS_PROCESSED</c>. For each processed
///   buffer, unqueue, pull a fresh chunk from the source, re-queue.</item>
///   <item>Re-issues <c>alSourcePlay</c> if the source falls out of
///   <c>AL_PLAYING</c> state (happens when the queue ran dry — rare in
///   steady state but possible on system stalls).</item>
/// </list></para>
///
/// <para><b>Threading model.</b> One streaming thread per backend instance.
/// The thread owns the OpenAL device + context + source + buffer ring.
/// <see cref="Init"/> / <see cref="Play"/> / <see cref="Stop"/> /
/// <see cref="Dispose"/> serialise through <c>_gate</c> and signal the
/// streaming thread via a <see cref="CancellationTokenSource"/>. No
/// <c>GCHandle</c> dance needed (unlike miniaudio's
/// <c>[UnmanagedCallersOnly]</c> callback) because OpenAL doesn't call
/// back into managed code — we drive it from the streaming loop.</para>
///
/// <para><b>Format.</b> Uses the integer constants from the
/// <c>AL_EXT_float32</c> extension cast into <see cref="BufferFormat"/>:
/// <c>AL_FORMAT_MONO_FLOAT32 = 0x10010</c>,
/// <c>AL_FORMAT_STEREO_FLOAT32 = 0x10011</c>. The extension is universally
/// supported in OpenAL Soft (every version since 2008). Format matches
/// the MasterMixer's 48 kHz IEEE-float stereo output directly — no
/// conversion needed.</para>
/// </summary>
public sealed unsafe class OpenALBackend : IAudioOutputBackend
{
    /// <summary>Buffers in the streaming ring. 4 × <see cref="BufferFrames"/>
    /// = ~80 ms of buffered audio at 48 kHz — deep enough to ride out a
    /// moderate scheduler hiccup without underrunning, shallow enough that
    /// per-buffer latency stays under 100 ms (below the threshold where
    /// the GM perceives slack between Play click and sound).</summary>
    private const int QueueDepth = 4;

    /// <summary>Frames per buffer. 960 frames at 48 kHz = 20 ms each —
    /// matches typical OS audio-callback granularity.</summary>
    private const int BufferFrames = 960;

    // AL_FORMAT_*_FLOAT32 constants from the AL_EXT_float32 extension.
    // OpenAL Soft has supported this since forever; we cast into the
    // BufferFormat enum (it's an int-backed enum, so cast is legal even
    // for non-member values).
    private const int AL_FORMAT_MONO_FLOAT32   = 0x10010;
    private const int AL_FORMAT_STEREO_FLOAT32 = 0x10011;

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _source;
    // Filled by AL.GenBuffers(int) which RETURNS a new uint[] rather than
    // populating in-place (Silk.NET's managed overload — the unmanaged
    // GenBuffers(int, uint*) is the in-place variant). We hold the
    // returned array verbatim so DeleteBuffers gets the same handles back.
    private uint[] _buffers = Array.Empty<uint>();
    private bool _buffersGenerated;

    private ISampleProvider? _sourceProvider;
    private int _channels;
    private int _sampleRate;
    private BufferFormat _alFormat;

    // Reusable per-iteration sample buffer for the streaming loop. Sized
    // once at Init and held for the backend's lifetime so the streaming
    // thread doesn't churn the GC every buffer-refill cycle.
    private float[]? _scratchSamples;

    private Thread? _streamThread;
    private CancellationTokenSource? _streamCts;
    private volatile bool _isPlaying;
    private bool _disposed;
    private readonly object _gate = new();

    // Rate-limit AL error reporting from the streaming loop. OpenAL is a
    // state machine — a failed BufferData (driver OOM, lost context, etc.)
    // just sets alGetError and audio plays silence. Without rate-limiting
    // we'd spam every 5 ms tick once a failure mode latches.
    private int _alErrorCount;
    private const int AlErrorLogStride = 100;

    public bool IsPlaying => _isPlaying;

    public void Init(ISampleProvider source, string? preferredDeviceId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenALBackend));
        lock (_gate)
        {
            bool wasPlaying = _isPlaying;
            StopAndUninit();

            _sourceProvider = source ?? throw new ArgumentNullException(nameof(source));
            var fmt = source.WaveFormat;
            if (fmt.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                throw new NotSupportedException(
                    $"OpenALBackend requires an IEEE-float source, got {fmt.Encoding}.");
            }
            if (fmt.Channels < 1 || fmt.Channels > 2)
            {
                throw new NotSupportedException(
                    $"OpenALBackend supports 1 or 2 channels, got {fmt.Channels}.");
            }
            _channels = fmt.Channels;
            _sampleRate = fmt.SampleRate;
            _alFormat = (BufferFormat)(_channels == 1
                ? AL_FORMAT_MONO_FLOAT32
                : AL_FORMAT_STEREO_FLOAT32);

            _scratchSamples = new float[BufferFrames * _channels];

            _al = AL.GetApi();
            _alc = ALContext.GetApi();

            _device = _alc.OpenDevice(preferredDeviceId ?? "");
            if (_device == null)
            {
                Log.Error("Audio", "OpenAL: alcOpenDevice returned null. " +
                                   "No audio device available (system has no default output, or libopenal failed to load).");
                _al = null;
                _alc = null;
                return;
            }

            _context = _alc.CreateContext(_device, null);
            if (_context == null)
            {
                _alc.CloseDevice(_device);
                _device = null;
                _al = null;
                _alc = null;
                Log.Error("Audio", "OpenAL: alcCreateContext returned null.");
                return;
            }
            _alc.MakeContextCurrent(_context);

            // GenSource / GenBuffers can throw on driver failure (rare —
            // out-of-memory, broken driver state). If either escapes here
            // we'd leak the device + context allocated above; tear down
            // everything and rethrow so the caller sees a clean failure
            // instead of a backend half-initialized into a no-op state.
            try
            {
                _source = _al.GenSource();
                // GenBuffers(int) returns a fresh array (the Silk.NET managed
                // overload). Hold the same array reference for the lifetime
                // of this backend instance so DeleteBuffers in StopAndUninit
                // hands the exact same handles back.
                _buffers = _al.GenBuffers(QueueDepth);
                // If a future Silk.NET refactor swaps the managed overload
                // for an in-place populate, _buffers would be unchanged
                // (still Array.Empty<uint>) and DeleteBuffers would silently
                // leak QueueDepth handles. Catch that at the contract edge.
                Debug.Assert(_buffers.Length == QueueDepth,
                    $"AL.GenBuffers contract changed: expected length {QueueDepth}, got {_buffers.Length}.");
                _buffersGenerated = true;
            }
            catch
            {
                StopAndUninit();
                throw;
            }

            Log.Info("Audio",
                $"OpenAL backend initialized ({_sampleRate} Hz, {_channels} ch, " +
                $"{QueueDepth}×{BufferFrames} frame ring).");

            if (wasPlaying) Play();
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_al == null || _device == null || _source == 0)
            {
                Log.Warn("Audio", "OpenAL Play() called but device/source not initialized — Init() must run first.");
                return;
            }
            if (_isPlaying) return;

            _streamCts = new CancellationTokenSource();
            var token = _streamCts.Token;
            _streamThread = new Thread(() => RunStreamLoop(token))
            {
                IsBackground = true,
                Name = "OpenALStream",
            };
            _isPlaying = true;
            _streamThread.Start();
            Log.Info("Audio", "OpenAL streaming thread started.");
        }
    }

    public void Pause()
    {
        // Tear down the streaming thread (cheap to recreate on Play)
        // rather than trying to keep OpenAL in a paused-but-queued state.
        // The next Play() rebuilds the ring from scratch by pulling fresh
        // samples — a natural resume point.
        lock (_gate)
        {
            if (!_isPlaying) return;
            StopStreamThread();
            try { _al?.SourceStop(_source); } catch { }
            _isPlaying = false;
        }
    }

    public void Stop()
    {
        lock (_gate) StopAndUninit();
    }

    public IEnumerable<AudioDevice> GetDevices()
    {
        // Device enumeration via ALC_ENUMERATION_EXT exists in Silk.NET via
        // an extension lookup, but the type surface is fiddly and we don't
        // need it for v1 — a Linux/macOS device picker can land later.
        // Return a single "Default Output" sentinel for now so the UI
        // dropdown stays populated.
        return new[] { new AudioDevice { Id = "-1", Name = "Default Output" } };
    }

    private void StopStreamThread()
    {
        var cts = _streamCts;
        var t = _streamThread;
        _streamCts = null;
        _streamThread = null;
        try { cts?.Cancel(); } catch { }
        // Don't join from the streaming thread itself (would deadlock if
        // RunStreamLoop ever called StopStreamThread re-entrantly — it
        // doesn't today, but defensive).
        if (t != null && t.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            try { t.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
        cts?.Dispose();
    }

    private void StopAndUninit()
    {
        StopStreamThread();
        _isPlaying = false;

        if (_al != null && _source != 0)
        {
            try { _al.SourceStop(_source); } catch { }
            try
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                if (queued > 0)
                {
                    var drained = new uint[queued];
                    _al.SourceUnqueueBuffers(_source, drained);
                }
            }
            catch { }
            try { _al.DeleteSource(_source); } catch { }
            _source = 0;
        }

        if (_al != null && _buffersGenerated)
        {
            try { _al.DeleteBuffers(_buffers); } catch { }
            _buffersGenerated = false;
        }

        if (_alc != null && _context != null)
        {
            try { _alc.MakeContextCurrent(null); } catch { }
            try { _alc.DestroyContext(_context); } catch { }
            _context = null;
        }

        if (_alc != null && _device != null)
        {
            try { _alc.CloseDevice(_device); } catch { }
            _device = null;
        }

        _al?.Dispose();
        _alc?.Dispose();
        _al = null;
        _alc = null;
    }

    /// <summary>The streaming loop. Owns the OpenAL source's buffer queue
    /// for the lifetime of one Play() → Stop() cycle.</summary>
    private void RunStreamLoop(CancellationToken token)
    {
        if (_al == null || _sourceProvider == null || _scratchSamples == null) return;

        try
        {
            // Pre-fill the ring before starting playback. The source
            // provider (typically MasterMixer) returns silence when no
            // tracks are active thanks to ReadFully=true — see MasterMixer.
            for (int i = 0; i < QueueDepth; i++)
            {
                if (token.IsCancellationRequested) return;
                PullAndQueueBuffer(_buffers[i]);
            }

            _al.SourcePlay(_source);

            // Steady-state loop: drain processed buffers, refill, re-queue.
            while (!token.IsCancellationRequested)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
                if (processed > 0)
                {
                    var unqueued = new uint[processed];
                    _al.SourceUnqueueBuffers(_source, unqueued);
                    for (int i = 0; i < processed && !token.IsCancellationRequested; i++)
                    {
                        PullAndQueueBuffer(unqueued[i]);
                    }
                }

                // If we ran the queue dry (network stall, GC pause, etc.)
                // OpenAL transitions to AL_STOPPED. Detect and re-issue
                // SourcePlay so audio resumes after the stall instead of
                // staying silent until the next user action.
                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                if (state == (int)SourceState.Stopped)
                {
                    Log.Warn("Audio", "OpenAL source underran (queue dry) — re-issuing SourcePlay.");
                    _al.SourcePlay(_source);
                }

                // 5 ms sleep — sub-buffer poll cadence, ~4× per buffer
                // consume event. Wait on the cancellation token's wait
                // handle so Cancel() wakes us immediately instead of
                // waiting for the next tick.
                token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio", "OpenAL streaming loop crashed", ex);
        }
        finally
        {
            try { _al?.SourceStop(_source); } catch { }
        }
    }

    /// <summary>Pull one buffer's worth of samples from the source provider,
    /// upload to OpenAL, queue on the playing source. Silence-fills if the
    /// source returned a short read (shouldn't happen with MasterMixer's
    /// ReadFully=true contract, but defended against for future
    /// non-mixer callers).</summary>
    private void PullAndQueueBuffer(uint bufferId)
    {
        if (_al == null || _sourceProvider == null || _scratchSamples == null) return;

        int sampleCount = BufferFrames * _channels;
        int read = _sourceProvider.Read(_scratchSamples, 0, sampleCount);
        if (read <= 0)
        {
            Array.Clear(_scratchSamples, 0, sampleCount);
            read = sampleCount;
        }
        else if (read < sampleCount)
        {
            // Pad short reads with silence rather than uploading a smaller
            // buffer. Mixing buffer sizes within the queue confuses
            // OpenAL Soft's internal timing on some drivers.
            Array.Clear(_scratchSamples, read, sampleCount - read);
            read = sampleCount;
        }

        // BufferData expects bytes; float32 = 4 bytes/sample. Use the
        // void* overload so the cast-from-int BufferFormat takes effect
        // without a generic-element type lookup.
        fixed (float* p = _scratchSamples)
        {
            _al.BufferData(bufferId, _alFormat, p, read * sizeof(float), _sampleRate);
        }
        CheckAlError("BufferData");

        _al.SourceQueueBuffers(_source, new[] { bufferId });
        CheckAlError("SourceQueueBuffers");
    }

    /// <summary>Drain the AL error state and log if non-zero. Rate-limited
    /// to one line per <see cref="AlErrorLogStride"/> failures so a latched
    /// failure mode doesn't flood the log. OpenAL is a state machine — a
    /// failed call sets the error and returns silently; without this, audio
    /// goes quiet with no diagnostic trail.</summary>
    private void CheckAlError(string context)
    {
        if (_al == null) return;
        var err = _al.GetError();
        if (err == AudioError.NoError) return;
        var n = Interlocked.Increment(ref _alErrorCount);
        if (n == 1 || n % AlErrorLogStride == 0)
        {
            Log.Warn("Audio", $"OpenAL error after {context}: {err} (total {n} since Init).");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate) StopAndUninit();
        _disposed = true;
    }
}
