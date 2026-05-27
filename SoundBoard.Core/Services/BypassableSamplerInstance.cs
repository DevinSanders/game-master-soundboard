using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.PluginApi;
using System;
using System.Threading;

namespace SoundBoard.Core.Services;

/// <summary>
/// Host-side wrapper around an <see cref="ISamplerInstance"/> that lets the
/// host toggle bypass at runtime without re-building the audio chain. The
/// wrapped instance is always inserted into the chain; on each audio buffer
/// the wrapper either passes the dry source through or invokes the effect,
/// based on its <see cref="IsBypassed"/> flag.
///
/// <para>Why this exists: bypass is a row-level setting in the database,
/// not part of the plugin's serialised config. If we relied on
/// rebuild-on-bypass for per-target chains, the user would have to stop
/// and replay a preset/playlist after every toggle. Wrapping at the host
/// layer means the same chain stays valid; only one volatile bool moves.</para>
///
/// <para><b>State-freeze caveat.</b> While bypassed the plugin's wet
/// chain is NOT clocked (the audio thread only reads from <c>_dry</c>),
/// so any internal state — reverb tails, delay lines, IIR filter memory
/// — freezes at the moment bypass began. On un-bypass the user hears
/// a brief discontinuity as the plugin's stale state replays. This is
/// inaudible for stateless effects (gain, EQ without lookahead) and
/// acceptable for stateful ones in v1; if a plugin needs glitch-free
/// bypass it can implement its own dry/wet inside
/// <see cref="ISamplerInstance.CreateEffect"/>. An earlier design used
/// a tee + always-pull-wet to keep state warm, but the per-cycle
/// Array.Copy and scratch rental on the audio thread caused audible
/// jitter; the simpler design here trades that artifact for a tiny
/// transition glitch nobody hears for the common stateless case.</para>
/// </summary>
public sealed class BypassableSamplerInstance : ISamplerInstance
{
    private readonly ISamplerInstance _inner;
    private int _bypassed; // bool stored as int for Interlocked

    public BypassableSamplerInstance(ISamplerInstance inner, bool initialBypassed)
    {
        _inner = inner;
        _bypassed = initialBypassed ? 1 : 0;
    }

    /// <summary>The wrapped plugin instance, exposed so the editor can host
    /// its <see cref="ISamplerInstance.CreateControl"/> directly.</summary>
    public ISamplerInstance Inner => _inner;

    /// <summary>Runtime bypass flag. Audio-thread read; UI-thread write.</summary>
    public bool IsBypassed
    {
        get => Volatile.Read(ref _bypassed) != 0;
        set => Volatile.Write(ref _bypassed, value ? 1 : 0);
    }

    public string SerializeConfig() => _inner.SerializeConfig();
    public void DeserializeConfig(string json) => _inner.DeserializeConfig(json);
    public object? CreateControl() => _inner.CreateControl();

    public ISampleProvider CreateEffect(ISampleProvider source)
    {
        // Direct wrap — no tee. Source is the dry; source-through-plugin
        // is the wet. Only one of the two is read per cycle, so source is
        // pulled exactly once. (See state-freeze caveat in the class doc.)
        ISampleProvider wet;
        try
        {
            wet = _inner.CreateEffect(source);
            if (wet == null)
            {
                Log.Warn("Audio", $"Plugin '{_inner.GetType().Name}' returned null from CreateEffect — falling back to passthrough.");
                wet = source;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio", $"Plugin '{_inner.GetType().Name}' CreateEffect threw — wet chain replaced with passthrough.", ex);
            wet = source;
        }

        // Wrap the wet output in a SafeSampleProvider so any throw from
        // the plugin's Read is caught on the audio thread and converted
        // to silence rather than killing the playback thread.
        var safeWet = new SafeSampleProvider(wet, label: _inner.GetType().Name);

        return new BypassSwitchSampleProvider(source, safeWet, this);
    }

    public void Dispose() => _inner.Dispose();

    /// <summary>The actual audio-thread switch. <see cref="Read"/> picks
    /// between the dry source and the wet effect output based on the
    /// wrapper's current <see cref="IsBypassed"/> state. The choice is per
    /// buffer, not per sample, so a long buffer can't half-bypass — the
    /// flag is sampled once at the top of <c>Read</c>.
    ///
    /// <para>Zero overhead at unity / non-bypassed: a single Volatile read
    /// of the bypass flag plus the inactive branch call. No allocations,
    /// no Array.Copy on the audio thread.</para></summary>
    private sealed class BypassSwitchSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _dry;
        private readonly ISampleProvider _wet;
        private readonly BypassableSamplerInstance _owner;

        public BypassSwitchSampleProvider(ISampleProvider dry, ISampleProvider wet, BypassableSamplerInstance owner)
        {
            _dry = dry;
            _wet = wet;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _wet.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
            => _owner.IsBypassed ? _dry.Read(buffer, offset, count)
                                 : _wet.Read(buffer, offset, count);
    }
}
