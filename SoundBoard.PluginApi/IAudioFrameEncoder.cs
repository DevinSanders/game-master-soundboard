using System;

namespace SoundBoard.PluginApi;

/// <summary>
/// A streaming PCM-to-encoded-bytes frame encoder, vended by a codec plugin
/// that opts into <see cref="IAudioCodecPlugin.SupportsEncoding"/>.
///
/// <para><b>What this exists for.</b> Bridge plugins (Discord / Zoom /
/// Mumble / …) consume the host's master mix as 48 kHz stereo IEEE-float
/// PCM and need to push it to a remote endpoint that expects compressed
/// frames (Opus packets, AAC ADTS, etc.). Rather than each bridge plugin
/// bundling its own copy of every encoder it might need, the host's
/// installed codec plugins expose their encoders here and the bridge
/// borrows them. One Opus implementation, one codec.opus install, every
/// bridge that needs Opus shares it.</para>
///
/// <para><b>Frame-oriented, not byte-oriented.</b> Audio codecs almost
/// always operate on fixed-size PCM frames (Opus: 2.5 / 5 / 10 / 20 / 40 /
/// 60 ms). <see cref="FrameSamples"/> tells the caller how many sample
/// frames per <see cref="Encode"/> call the encoder wants. PCM input
/// shorter than that is the caller's bug; longer is the caller's bug.
/// Bridges should buffer the host's mixer chunks until they have exactly
/// <see cref="FrameSamples"/> samples per channel.</para>
///
/// <para><b>Threading.</b> One <see cref="IAudioFrameEncoder"/> instance
/// is owned by exactly one caller and only used from one thread at a
/// time. The encoder MAY hold mutable internal state across frames
/// (Opus does — its lookahead and predictor benefit from a continuous
/// frame sequence). Bridge plugins create one encoder per outbound
/// stream and dispose it on disconnect.</para>
/// </summary>
public interface IAudioFrameEncoder : IDisposable
{
    /// <summary>Number of PCM samples per channel the encoder consumes
    /// per <see cref="Encode"/> call. Codec-dependent; Opus typically
    /// returns 960 (20 ms @ 48 kHz).</summary>
    int FrameSamples { get; }

    /// <summary>Number of channels the encoder was configured for.
    /// Caller's PCM buffer length must equal
    /// <c><see cref="FrameSamples"/> * <see cref="Channels"/></c>.</summary>
    int Channels { get; }

    /// <summary>Sample rate the encoder was configured for, in Hz.
    /// Codecs operating at a different internal rate (Opus is always
    /// 48 kHz; AAC has its own native rates) should expose the rate the
    /// PCM input must be supplied at — the caller does any required
    /// resampling on its side.</summary>
    int SampleRate { get; }

    /// <summary>Encode one frame of interleaved PCM into compressed bytes.
    /// PCM is float in <c>[-1.0, 1.0]</c>, interleaved by channel
    /// (LRLRLR for stereo). <paramref name="pcm"/> length must be
    /// exactly <c><see cref="FrameSamples"/> * <see cref="Channels"/></c>.
    /// <paramref name="packet"/> is a destination buffer the caller owns;
    /// implementations should size it generously
    /// (4000 bytes is a safe upper bound for a single Opus packet).
    /// Returns the number of bytes actually written.</summary>
    int Encode(ReadOnlySpan<float> pcm, Span<byte> packet);
}
