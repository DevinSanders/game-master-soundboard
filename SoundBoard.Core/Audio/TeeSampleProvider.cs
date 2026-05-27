using NAudio.Wave;
using System;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Pulls from a single upstream <see cref="ISampleProvider"/> once and
/// fans out the same sample stream to two independent consumers. The
/// canonical use is the bypass switch in
/// <see cref="Services.BypassableSamplerInstance"/>: both the dry tap
/// (output during bypass) and the wet chain (input to the plugin's
/// effect) need to read identical samples from a shared upstream, but
/// only one of them is heard at a time. Without the tee they'd either
/// double-pull the upstream (causing skip) or one of them would freeze
/// (causing pop on un-bypass).
///
/// <para><b>Lifecycle.</b> Each call to <see cref="DryTap"/>.Read or
/// <see cref="WetTap"/>.Read advances <i>that</i> tap's position
/// independently. The tee pulls more from upstream whenever the
/// currently-reading tap runs past <c>_upstreamPos</c>. The other tap
/// reads from the cached ring; no upstream pull happens twice for the
/// same sample.</para>
///
/// <para><b>Lockstep convention.</b> Callers must drain both taps at
/// roughly the same rate. If one tap stays at position 0 while the other
/// advances by <c>RingSize</c>, the ring is full and the lagging tap's
/// samples can't be discarded — the leading tap will start returning
/// short reads. The <see cref="Services.BypassableSamplerInstance"/>
/// switch enforces lockstep by draining the inactive tap into a scratch
/// buffer every cycle.</para>
///
/// <para><b>Threading.</b> Single-threaded. The audio thread reads both
/// taps sequentially within one buffer cycle; no concurrent access is
/// expected or guarded against. Don't share a tee across audio threads.</para>
/// </summary>
public sealed class TeeSampleProvider
{
    /// <summary>Default ring size: 4096 samples (~85 ms at 48 kHz stereo),
    /// comfortably larger than any typical NAudio buffer cycle. Rounded
    /// to a power of two so the modulo-by-mask is a single AND.</summary>
    public const int DefaultRingSize = 4096;

    private readonly ISampleProvider _upstream;
    private readonly float[] _ring;
    private readonly int _ringMask;

    private long _upstreamPos;   // total samples pulled from upstream so far
    private long _dryPos;        // total samples consumed via DryTap
    private long _wetPos;        // total samples consumed via WetTap
    private bool _upstreamEof;   // upstream returned 0 — stop pulling

    public TeeSampleProvider(ISampleProvider upstream, int ringSize = DefaultRingSize)
    {
        _upstream = upstream;
        // Round up to a power of two so we can do `pos & mask` instead
        // of `pos % size`. Tiny hot-loop win; the audio thread runs Read
        // every ~10 ms and copies count samples — modulo per sample adds up.
        int actual = 1;
        while (actual < ringSize) actual <<= 1;
        _ring = new float[actual];
        _ringMask = actual - 1;
        DryTap = new Tap(this, isDry: true);
        WetTap = new Tap(this, isDry: false);
    }

    /// <summary>Total samples available in the ring (rounded to next pow2).</summary>
    public int RingSize => _ring.Length;

    /// <summary>Tap that returns the upstream's samples to the bypass
    /// switch's "dry" output. Independent read position from
    /// <see cref="WetTap"/>.</summary>
    public ISampleProvider DryTap { get; }

    /// <summary>Tap that returns the upstream's samples to the wet chain
    /// (i.e. the plugin's <c>CreateEffect</c> result). Independent read
    /// position from <see cref="DryTap"/>.</summary>
    public ISampleProvider WetTap { get; }

    /// <summary>Total samples consumed via the dry tap.</summary>
    public long DryPosition => _dryPos;

    /// <summary>Total samples consumed via the wet tap.</summary>
    public long WetPosition => _wetPos;

    /// <summary>Total samples pulled from the upstream so far.</summary>
    public long UpstreamPosition => _upstreamPos;

    /// <summary>Advance the dry tap by <paramref name="count"/> samples
    /// WITHOUT reading or copying — just bump <c>_dryPos</c> so the ring
    /// doesn't fill up. Used by the bypass switch's fast path: when the
    /// listener is hearing the wet output, the dry samples aren't needed,
    /// only the position bookkeeping. Clamps to <c>_upstreamPos</c> so we
    /// can't claim to have read more than upstream has produced.
    ///
    /// <para>Caller pre-condition: the wet tap has already been pulled
    /// by <c>count</c> samples this cycle, so <c>_upstreamPos &gt;= _dryPos + count</c>.
    /// If a caller advances past <c>_upstreamPos</c> the call is a no-op
    /// past that limit — defensive rather than a hard crash.</para></summary>
    public void AdvanceDryTap(int count)
    {
        long target = _dryPos + count;
        _dryPos = Math.Min(target, _upstreamPos);
    }

    /// <summary>Symmetric counterpart of <see cref="AdvanceDryTap"/>.
    /// Currently unused in the production chain (the bypass switch never
    /// outputs wet while skipping wet's actual <c>Read</c>) but exposed
    /// for symmetry and possible future use.</summary>
    public void AdvanceWetTap(int count)
    {
        long target = _wetPos + count;
        _wetPos = Math.Min(target, _upstreamPos);
    }

    private int ReadInternal(bool isDry, float[] buf, int offset, int count)
    {
        long myPos = isDry ? _dryPos : _wetPos;
        long otherPos = isDry ? _wetPos : _dryPos;

        // Upstream may pull as far as (otherPos + ring length) — anything
        // past that would overwrite samples the other tap hasn't seen.
        long pullCeiling = otherPos + _ring.Length;
        long needed = myPos + count;
        long pullTarget = Math.Min(needed, pullCeiling);

        // Top up the ring until our tap has `count` samples ahead of it,
        // or upstream is exhausted, or we'd overrun the other tap.
        while (!_upstreamEof && _upstreamPos < pullTarget)
        {
            int writeIdx = (int)(_upstreamPos & _ringMask);
            int contiguous = Math.Min((int)(pullTarget - _upstreamPos), _ring.Length - writeIdx);
            int pulled = _upstream.Read(_ring, writeIdx, contiguous);
            if (pulled == 0)
            {
                _upstreamEof = true;
                break;
            }
            _upstreamPos += pulled;
            // If pulled < contiguous, upstream returned a short read; try once
            // more on the next loop iteration (it may produce more on the
            // next call, but if it returns 0 we mark EOF).
        }

        // Copy from ring to caller's buffer, accounting for ring wrap.
        int available = (int)Math.Min(count, _upstreamPos - myPos);
        if (available > 0)
        {
            int readIdx = (int)(myPos & _ringMask);
            int firstChunk = Math.Min(available, _ring.Length - readIdx);
            Array.Copy(_ring, readIdx, buf, offset, firstChunk);
            if (firstChunk < available)
                Array.Copy(_ring, 0, buf, offset + firstChunk, available - firstChunk);
        }

        if (isDry) _dryPos += available;
        else _wetPos += available;
        return available;
    }

    private sealed class Tap : ISampleProvider
    {
        private readonly TeeSampleProvider _tee;
        private readonly bool _isDry;

        public Tap(TeeSampleProvider tee, bool isDry)
        {
            _tee = tee;
            _isDry = isDry;
        }

        public WaveFormat WaveFormat => _tee._upstream.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
            => _tee.ReadInternal(_isDry, buffer, offset, count);
    }
}
