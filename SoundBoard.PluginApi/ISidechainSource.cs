using System;
using System.Collections.Generic;

namespace SoundBoard.PluginApi;

/// <summary>
/// One observable audio stream a plugin can attach to as a sidechain
/// trigger. The host publishes one source per audio bus (Music, Ambient,
/// SFX, plus any custom buses the GM has added). A ducker plugin
/// attached to the Music bus subscribes to the SFX source, accumulates
/// an envelope from its sample stream, and uses that envelope to drive
/// gain reduction on its own audio output.
///
/// <para><b>Subscription model.</b> Sidechain consumers receive
/// PUSH-style callbacks from the host's audio thread — calling
/// <see cref="Subscribe"/> returns an <see cref="IDisposable"/> that
/// unsubscribes when disposed. Disposing during a callback is safe;
/// the host queues the unsubscribe and applies it before the next
/// invocation.</para>
///
/// <para><b>Threading.</b> The callback runs ON THE AUDIO THREAD,
/// once per Read cycle (every ~10 ms at the default buffer size).
/// The buffer the callback receives is a fresh copy — the plugin may
/// read it freely but MUST NOT mutate, store the reference, or block.
/// All sidechain detection (envelope follower, RMS, peak) should be
/// done in-place within the callback's stack frame; the resulting
/// envelope state is then read by the plugin's own
/// <see cref="ISamplerInstance.CreateEffect"/> chain via the usual
/// thread-safe primitives (<see cref="System.Threading.Volatile"/>,
/// <see cref="System.Threading.Interlocked"/>).</para>
///
/// <para><b>Why push, not pull.</b> A pull model (give the plugin an
/// <see cref="NAudio.Wave.ISampleProvider"/> to read on demand) would
/// destructively dequeue samples from the underlying bus, leaving the
/// real audio path one buffer behind. A push fan-out lets the host
/// emit identical copies to every subscriber without affecting the
/// main mix — the same pattern <c>MasterMixer</c> uses for outbound
/// bridge fan-out.</para>
/// </summary>
public interface ISidechainSource
{
    /// <summary>Stable identifier for this source. Persist this when a
    /// plugin's config records "which bus am I listening to" — display
    /// names can be renamed by the user, but the id is stable across
    /// app launches. Format is host-defined; for bus-backed sources
    /// the host uses <c>"bus:&lt;id&gt;"</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in plugin UIs (e.g. "SFX",
    /// "Music"). Reflects the current bus name — if the user renames
    /// a bus in the Settings → Buses page, this property updates.
    /// Plugin UIs should re-read it after a source-changed event.</summary>
    string DisplayName { get; }

    /// <summary>Sample rate of the audio the callback delivers. Matches
    /// the host master rate (48 000 Hz at the time of writing).</summary>
    int SampleRate { get; }

    /// <summary>Channel count (1 = mono, 2 = stereo) of the audio the
    /// callback delivers. Matches the host master format.</summary>
    int Channels { get; }

    /// <summary>Subscribe to per-buffer samples. The callback is invoked
    /// on the audio thread with <c>(buffer, count)</c>; the plugin must
    /// read at most <c>count</c> interleaved samples from
    /// <paramref name="onSamples"/>'s buffer argument and not retain
    /// the reference. The returned <see cref="IDisposable"/> detaches
    /// the subscription when disposed.</summary>
    IDisposable Subscribe(Action<float[], int> onSamples);
}

/// <summary>
/// Host registry of every <see cref="ISidechainSource"/> the plugin can
/// attach to. Plugins receive this via
/// <see cref="IPluginContext.Sidechain"/> at
/// <see cref="IPlugin.Initialize"/> time. The list is dynamic — buses
/// added or removed at runtime fire <see cref="SourcesChanged"/> so the
/// plugin can refresh its source-picker UI.
///
/// <para>Reading <see cref="GetSources"/> from inside
/// <see cref="IPlugin.Initialize"/> may return a partial list (the
/// codec / bridge / sampler plugins all initialize before the bus
/// service finishes spinning up); plugins should query lazily, from
/// inside their UI's source picker rather than at construction
/// time.</para>
/// </summary>
public interface ISidechainRegistry
{
    /// <summary>Current snapshot of available sidechain sources. The list
    /// is in user-visible order (matching the bus order in the Bus Mixer
    /// page) so a dropdown bound to this list reads naturally.</summary>
    IReadOnlyList<ISidechainSource> GetSources();

    /// <summary>Look up a source by its <see cref="ISidechainSource.Id"/>.
    /// Returns null when the source no longer exists (e.g. the user
    /// deleted the bus). Plugins persisting a source id should fall
    /// back to "(none)" on a null lookup rather than crashing.</summary>
    ISidechainSource? GetSourceById(string id);

    /// <summary>Fired when the source list changes — bus added /
    /// removed / renamed. Source-picker UIs should re-read
    /// <see cref="GetSources"/> on this event. Raised on an
    /// arbitrary thread; UI consumers must marshal to their dispatcher
    /// before mutating bindings.</summary>
    event EventHandler? SourcesChanged;
}
