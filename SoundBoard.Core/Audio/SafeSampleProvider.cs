using NAudio.Wave;
using SoundBoard.Core.Logging;
using System;
using System.Threading;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Host-side wrapper that isolates an untrusted <see cref="ISampleProvider"/>
/// (typically the wet output of a plugin's <c>CreateEffect</c>) from the
/// audio thread. A throwing or hanging plugin <c>Read</c> would otherwise
/// tear down NAudio's playback thread and silence the whole app.
///
/// <para><b>Behavior on exception.</b></para>
/// <list type="bullet">
/// <item>The thrown exception is caught and never re-raised.</item>
/// <item>The caller's buffer is zeroed for the requested length (silence).</item>
/// <item>A one-shot <see cref="Log.Warn"/> is emitted on the first failure.
///   Subsequent failures bump an internal counter without re-logging the
///   stack trace (avoids log spam at ~100 Hz audio cycles).</item>
/// <item>After <see cref="ConsecutiveFailureThreshold"/> failures in a
///   row, the wrapper enters <b>permanent bypass mode</b>: it stops
///   calling the inner provider entirely and returns silence forever. A
///   final <see cref="Log.Error"/> records the lock-out.</item>
/// </list>
///
/// <para><b>Threading.</b> All counters are accessed only from the audio
/// thread (single reader of the underlying provider), so plain fields
/// suffice. The bypass flag is volatile for the rare cross-thread inspection
/// in tests or diagnostics.</para>
///
/// <para><b>Where it's used.</b> Wraps every plugin-produced wet provider
/// inside <see cref="Services.BypassableSamplerInstance.CreateEffect"/>.
/// That single insertion point covers all sampler attachments —
/// master-bus, per-target, and shortcut tiers.</para>
/// </summary>
public sealed class SafeSampleProvider : ISampleProvider
{
    /// <summary>Default consecutive-failure tolerance before permanent
    /// bypass. Three strikes balances "transient glitch shouldn't kill
    /// the effect" against "obviously broken plugin should stop trying."
    /// </summary>
    public const int ConsecutiveFailureThreshold = 3;

    private readonly ISampleProvider _inner;
    private readonly string _label;
    private readonly int _threshold;
    private int _consecutiveFailures;
    private long _totalFailures;
    private volatile bool _permanentlyBypassed;
    private bool _firstFailureLogged;

    public SafeSampleProvider(ISampleProvider inner, string label = "plugin",
                              int? consecutiveFailureThreshold = null)
    {
        _inner = inner;
        _label = label;
        _threshold = consecutiveFailureThreshold ?? ConsecutiveFailureThreshold;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    /// <summary>True once the wrapper has given up and is returning
    /// silence permanently. Diagnostic / test surface.</summary>
    public bool IsPermanentlyBypassed => _permanentlyBypassed;

    /// <summary>Total Read calls that threw. Counts the throws, not the
    /// samples skipped. Useful for telemetry.</summary>
    public long TotalFailures => Interlocked.Read(ref _totalFailures);

    public int Read(float[] buffer, int offset, int count)
    {
        if (_permanentlyBypassed)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int read;
        try
        {
            read = _inner.Read(buffer, offset, count);
            // Successful read: reset the consecutive-failure counter so a
            // transient hiccup doesn't compound toward permanent bypass.
            _consecutiveFailures = 0;
            // If the inner returned a short read, pad with silence so the
            // chain above still gets `count` samples (matches MasterMixer's
            // ReadFully convention).
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);
            return count;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Interlocked.Increment(ref _totalFailures);

            // First throw: log with full stack so the user can debug.
            if (!_firstFailureLogged)
            {
                _firstFailureLogged = true;
                Log.Warn("Audio", $"Plugin '{_label}' threw from Read — silencing this buffer.", ex);
            }

            if (_consecutiveFailures >= _threshold)
            {
                _permanentlyBypassed = true;
                Log.Error("Audio",
                    $"Plugin '{_label}' has thrown {_consecutiveFailures} times in a row — locking out for the rest of this session.",
                    ex);
            }

            // Silence the buffer so downstream hears a clean gap, not
            // whatever undefined state the throwing plugin left behind.
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
