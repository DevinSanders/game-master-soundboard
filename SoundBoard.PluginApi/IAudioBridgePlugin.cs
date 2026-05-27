using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoundBoard.PluginApi;

/// <summary>Lifecycle state of an <see cref="IAudioBridgePlugin"/>, surfaced
/// to the host's Settings UI so the user can see at a glance whether the
/// bridge is live, mid-handshake, or has just failed.</summary>
public enum BridgeStatus
{
    /// <summary>Bridge is idle; no connection has been attempted yet, or
    /// the most recent <see cref="IAudioBridgePlugin.DisconnectAsync"/>
    /// completed cleanly.</summary>
    Disconnected = 0,

    /// <summary>Bridge is mid-connect — i.e. <c>ConnectAsync</c> is in
    /// flight but hasn't reached steady state. UIs should show a spinner.</summary>
    Connecting,

    /// <summary>Bridge is connected and audio is flowing in at least
    /// one direction.</summary>
    Connected,

    /// <summary>The most recent connect attempt failed, or a previously-
    /// connected bridge was dropped by the remote. The bridge exposes
    /// <see cref="IAudioBridgePlugin.LastError"/> for user-readable
    /// detail.</summary>
    Failed,
}

/// <summary>
/// Bridges the host's master mix to (and optionally from) an external
/// real-time audio destination — Discord voice, Zoom, Google Meet, Mumble,
/// Teamspeak, etc.
///
/// <para><b>Why this exists.</b> Voice-meeting integrations have huge,
/// platform-specific dependency footprints (Discord.Net + libdave; the
/// Zoom RTMS SDK; native opus / libsodium binaries; …) that no user
/// who doesn't need them should pay for. Each bridge ships as a separate
/// plugin in its own sibling repo (<c>gmsb-bridge-discord</c>, <c>gmsb-bridge-zoom</c>,
/// <c>gmsb-bridge-mumble</c>, …) so the user installs only the ones they
/// actually use. The host runs without any of them installed.</para>
///
/// <para><b>Audio transport (pull model).</b> The host exposes
/// <c>MasterMixer.TryDequeueBroadcastChunk</c> via the bridge worker
/// thread it runs internally; bridges DO NOT pull from the mixer directly.
/// Instead, the host runs one worker per loaded bridge and calls
/// <see cref="SendOutboundPcm"/> with each chunk. The bridge encodes /
/// uploads / pushes the bytes on its own schedule. The pull design means
/// a slow bridge (network congestion, encoder stall) cannot block the
/// audio thread — the bounded queue inside the mixer drops chunks when
/// the bridge falls behind.</para>
///
/// <para><b>Audio receive (push model, optional).</b> A bridge that wants
/// to pipe remote audio INTO the local mix (e.g. a Mumble bridge that
/// surfaces other channel members as another mixer strip) calls
/// <see cref="IBridgeHost.PushInboundPcm"/> on the
/// <see cref="IBridgeHost"/> handed to it at
/// <see cref="IPlugin.Initialize"/>. The host wraps that callback as a virtual
/// <c>ISampleProvider</c> added to the mixer.</para>
///
/// <para><b>Borrowed codecs.</b> A bridge that needs Opus / AAC / …
/// asks for them via <see cref="IPluginContext.CodecRegistry"/> rather
/// than bundling its own copy. See
/// <see cref="IAudioCodecPlugin.SupportsEncoding"/> and
/// <see cref="IAudioCodecPlugin.CreateEncoder"/>.</para>
///
/// <para><b>UI ownership.</b> The bridge owns its own settings UI —
/// token fields, channel pickers, status readouts, connect/disconnect
/// buttons. The host renders <see cref="CreateSettingsControl"/> in a
/// "Bridges" section of the Settings page. The host does NOT remember
/// per-bridge configuration — bridges persist their own config under
/// <see cref="IPluginContext.PluginDataPath"/>.</para>
/// </summary>
public interface IAudioBridgePlugin : IPlugin
{
    /// <summary>Current connection state. Bridges should publish this
    /// via a private field and raise <see cref="StatusChanged"/> from
    /// the same thread that mutated it.</summary>
    BridgeStatus Status { get; }

    /// <summary>User-readable text shown in the host's Bridges section
    /// next to the status badge. Free-form — bridges set this to
    /// "Connected to #Game Nights" or "Token rejected" as appropriate.
    /// Null/empty means "no detail to show."</summary>
    string? StatusDetail { get; }

    /// <summary>The exception (if any) from the most recent failure.
    /// Surfaced as a tooltip on the Failed badge. Bridges should clear
    /// this when entering <see cref="BridgeStatus.Connecting"/> or
    /// <see cref="BridgeStatus.Connected"/>.</summary>
    Exception? LastError { get; }

    /// <summary>Fired whenever <see cref="Status"/> or
    /// <see cref="StatusDetail"/> changes. The host marshals to the UI
    /// thread before re-reading the properties, so handlers can be raised
    /// from any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>True if this bridge is currently consuming outbound PCM.
    /// Bridges flip this to true when they reach
    /// <see cref="BridgeStatus.Connected"/> and back to false on disconnect.
    /// The host queries this once per audio cycle to decide whether to
    /// enqueue a chunk into the broadcast queue at all — saves an alloc
    /// + copy when no bridges are subscribed.</summary>
    bool WantsOutboundAudio { get; }

    /// <summary>
    /// Begin connecting to the remote endpoint. Implementations should:
    /// 1) Synchronously transition to <see cref="BridgeStatus.Connecting"/>
    ///    and fire <see cref="StatusChanged"/>;
    /// 2) Do the actual handshake on a background task;
    /// 3) Transition to <see cref="BridgeStatus.Connected"/> /
    ///    <see cref="BridgeStatus.Failed"/> when that completes;
    /// 4) Return a Task that completes when the steady-state connection
    ///    is established (or surfaces the failure).
    ///
    /// Bridges that talk to remotes which require gateway-handshake-then-
    /// voice-handshake (Discord) should treat the WHOLE flow as one
    /// ConnectAsync — the user clicked one button.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Tear down the connection. Idempotent — calling on an
    /// already-disconnected bridge is a no-op. Always transitions to
    /// <see cref="BridgeStatus.Disconnected"/> on return, even if the
    /// underlying remote was unreachable.</summary>
    Task DisconnectAsync();

    /// <summary>One frame of stereo IEEE-float PCM at 48 kHz, interleaved
    /// (LRLRLR…), handed in by the host's broadcast worker. The bridge
    /// is responsible for any required re-buffering / re-framing / encoding
    /// before sending to the remote. <paramref name="pcm"/> is a snapshot
    /// the host owns — copy whatever you need before returning.
    /// Called on the host's per-bridge worker thread, NEVER the audio
    /// thread.</summary>
    void SendOutboundPcm(ReadOnlySpan<float> pcm);

    /// <summary>Render the bridge's settings UI. Called by the host on
    /// the UI thread to compose the Bridges section. Returns an Avalonia
    /// <c>Control</c> — declared as <c>object</c> here so the SDK doesn't
    /// drag Avalonia into its public surface. (Same trick the FX-chain
    /// SDK uses for <c>CreateEditor</c>.)</summary>
    object CreateSettingsControl(IBridgeHost host, IPluginContext context);
}

/// <summary>
/// Services the host hands to a bridge at <see cref="IPlugin.Initialize"/>
/// — in addition to the base <see cref="IPluginContext"/> — and at
/// <see cref="IAudioBridgePlugin.CreateSettingsControl"/> time. Lets the
/// bridge push received audio into the host mixer and look up codecs
/// without having to thread the host's services through itself.
/// </summary>
public interface IBridgeHost
{
    /// <summary>Push one frame of received audio INTO the host's mixer
    /// as an additional input. Pass interleaved 48 kHz stereo IEEE-float;
    /// the host treats it like any other mixer strip. Call as often as
    /// the bridge has audio to push — a few ms worth at a time is the
    /// expected cadence. Pass an empty span to flush silence (for codecs
    /// that expect continuous frames).</summary>
    void PushInboundPcm(ReadOnlySpan<float> pcm);

    /// <summary>Open a virtual mixer input that stays alive until disposed.
    /// Returns an opaque handle the bridge can call repeatedly via the
    /// returned <see cref="IInboundAudioSink"/>. Use this rather than
    /// <see cref="PushInboundPcm"/> when you want the host's mixer card
    /// UI to show a separate strip per remote speaker (e.g. one strip per
    /// Mumble user). Each open strip shows up as a card the user can
    /// volume / pan independently.</summary>
    IInboundAudioSink OpenInboundStream(string displayName);
}

/// <summary>A virtual mixer input owned by a bridge. Dispose to remove
/// the input from the mixer. Each push must be 48 kHz stereo float
/// interleaved; the host buffers and drains on its own clock.</summary>
public interface IInboundAudioSink : IDisposable
{
    /// <summary>Display name shown on the mixer card. Free-form — bridges
    /// set this to the remote user's nickname or the channel name.</summary>
    string DisplayName { get; }

    /// <summary>Push one chunk of received PCM. Same format contract as
    /// <see cref="IBridgeHost.PushInboundPcm"/>: 48 kHz stereo float
    /// interleaved. Safe to call from any thread.</summary>
    void Push(ReadOnlySpan<float> pcm);
}
