using System;

namespace SoundBoard.PluginApi;

/// <summary>
/// Inverse of <see cref="IAudioFrameEncoder"/> — converts encoded packets
/// back to PCM. Used by bridge plugins that receive remote audio (e.g. a
/// Mumble bridge piping other channel members' voices INTO the local mix)
/// and need to decode them with whatever codec the remote chose, without
/// bundling decoders themselves.
///
/// <para>Same threading rules as the encoder side: one instance per caller,
/// frame-at-a-time, stateful across frames. Output is 48 kHz stereo
/// IEEE-float (matching the host's <c>MasterMixer</c> contract — that type
/// lives in <c>SoundBoard.Core</c>, which the SDK doesn't reference, hence
/// the bare-name reference rather than <c>&lt;see cref=...&gt;</c>) regardless
/// of the codec's native rate / channel count — the decoder does its own
/// resampling / upmixing.</para>
/// </summary>
public interface IAudioFrameDecoder : IDisposable
{
    /// <summary>Channel count of the decoder's PCM output. Always 2
    /// (stereo) in v1 — the host mixer is stereo and the bridges feed
    /// directly into it.</summary>
    int Channels { get; }

    /// <summary>Sample rate of the decoder's PCM output. Always 48000
    /// in v1 to match the host mixer.</summary>
    int SampleRate { get; }

    /// <summary>Maximum number of sample frames per packet, for sizing
    /// the caller's destination buffer. For Opus this is 5760
    /// (120 ms @ 48 kHz worst case).</summary>
    int MaxFrameSamples { get; }

    /// <summary>Decode one packet to interleaved float PCM. Returns the
    /// number of sample frames (per channel) written into
    /// <paramref name="pcm"/>. <paramref name="pcm"/> must be at least
    /// <c><see cref="MaxFrameSamples"/> * <see cref="Channels"/></c>
    /// floats. Decoders that support FEC / packet loss concealment may
    /// accept an empty <paramref name="packet"/> to synthesize a
    /// replacement frame; in v1 we don't require that.</summary>
    int Decode(ReadOnlySpan<byte> packet, Span<float> pcm);
}
