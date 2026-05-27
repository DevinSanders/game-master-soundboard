using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;

namespace SoundBoard.PluginApi;

/// <summary>
/// Adds support for additional audio sources to the host's audio pipeline.
/// Registered into <c>AudioFileReaderCrossPlatform</c> on load; plugin
/// codecs match before the built-in handlers, so a plugin can shadow a
/// built-in if it claims the same pattern.
///
/// <para>Pattern matching: each entry in <see cref="SupportedPatterns"/>
/// is either:</para>
/// <list type="bullet">
///   <item><description>A <b>file extension</b> with a leading dot, e.g.
///     <c>".flac"</c>, <c>".aac"</c>. Matched case-insensitively against
///     <c>Path.GetExtension(input)</c>.</description></item>
///   <item><description>A <b>URL scheme prefix</b> ending in <c>"://"</c>,
///     e.g. <c>"http://"</c>, <c>"https://"</c>, <c>"rtmp://"</c>. Matched
///     case-insensitively against the start of the input string.</description></item>
/// </list>
///
/// <para>The host dispatches by inspecting the input and routing to the
/// first plugin whose patterns match.</para>
///
/// <para><b>Two input modes.</b> A codec may accept either a string
/// (file path or URL — codec opens it itself) or a pre-opened
/// <see cref="Stream"/> (handed in by another plugin — typically a
/// transport plugin like <c>codec.webstream</c>). The string overload
/// is mandatory; the Stream overload is opt-in. See
/// <see cref="SupportsStreamInput"/>.</para>
/// </summary>
public interface IAudioCodecPlugin : IPlugin
{
    /// <summary>
    /// Patterns this codec handles. See the interface remarks for the
    /// pattern grammar. Required.
    /// </summary>
    IEnumerable<string> SupportedPatterns { get; }

    /// <summary>
    /// MIME content-types this codec accepts when invoked via
    /// <see cref="CreateStream(Stream, string)"/>. Used by
    /// <see cref="IAudioCodecRegistry.GetByContentType"/> to dispatch
    /// streams whose origin advertised a Content-Type header.
    ///
    /// <para>Examples: <c>"audio/mpeg"</c> for MP3, <c>"audio/ogg"</c> for
    /// Ogg-Vorbis, <c>"audio/flac"</c> + <c>"audio/x-flac"</c> for FLAC,
    /// <c>"audio/aac"</c> for raw AAC. Case-insensitive.</para>
    ///
    /// <para>Defaults to an empty enumerable. Codecs that don't expose
    /// the Stream overload can leave this empty — they won't be
    /// reachable via <c>GetByContentType</c>.</para>
    /// </summary>
    IEnumerable<string> SupportedContentTypes => Array.Empty<string>();

    /// <summary>
    /// <c>true</c> when this codec implements the
    /// <see cref="CreateStream(Stream, string)"/> overload — i.e. it can
    /// decode from an arbitrary <see cref="Stream"/> handed in by another
    /// plugin. Defaults to <c>false</c>; codecs that wire up the Stream
    /// overload must override this getter to return <c>true</c>.
    ///
    /// <para>Used as a fast filter by
    /// <see cref="IAudioCodecRegistry.GetByContentType"/> and as a sanity
    /// check before transport plugins attempt dispatch.</para>
    /// </summary>
    bool SupportsStreamInput => false;

    /// <summary>
    /// Open <paramref name="source"/> and return a <see cref="WaveStream"/>.
    /// <paramref name="source"/> may be a local file path or a URL — the
    /// plugin chose to register for whatever scheme/extension was passed.
    ///
    /// <para>The returned stream should report <c>CanSeek = true</c> when
    /// the underlying source is seekable (any local file, a static asset
    /// served over HTTP with <c>Accept-Ranges: bytes</c>, etc.) and
    /// <c>CanSeek = false</c> for live streams (ICY/Shoutcast/Icecast,
    /// live HLS). The host hides the scrub slider and disables looping
    /// when <c>CanSeek</c> is false.</para>
    ///
    /// <para>Host owns the lifetime of the returned stream and disposes
    /// it when playback ends.</para>
    /// </summary>
    WaveStream CreateStream(string source);

    /// <summary>
    /// Decode an already-open <see cref="Stream"/> into a
    /// <see cref="WaveStream"/>. Used by transport plugins (e.g.
    /// <c>codec.webstream</c>) that opened the bytes themselves and need
    /// to hand them off to a format-specific decoder.
    ///
    /// <para><b>Ownership transfers.</b> The codec takes ownership of
    /// <paramref name="source"/>: the returned <see cref="WaveStream"/>'s
    /// <c>Dispose</c> MUST dispose <paramref name="source"/>. Callers
    /// hand the Stream over and stop tracking it.</para>
    ///
    /// <para><b><paramref name="formatHint"/> is advisory.</b> Common
    /// values: an extension like <c>".mp3"</c>, a MIME like
    /// <c>"audio/mpeg"</c>. Implementations may inspect the hint to
    /// pick between container variants, but MUST still validate the
    /// bytes themselves — never trust the hint as a security check.
    /// (NLayer / NVorbis / FlacReader / Concentus already throw on
    /// malformed input; preserve that behaviour.)</para>
    ///
    /// <para><b>Seekability follows the input Stream.</b> If
    /// <paramref name="source"/>.<c>CanSeek</c> is <c>true</c>, the
    /// returned <see cref="WaveStream"/> should also report
    /// <c>CanSeek = true</c> (delegate seeks down to the input).
    /// Otherwise <c>CanSeek = false</c> and the host shows the live-stream
    /// UI.</para>
    ///
    /// <para><b>Audio-thread safety.</b> The returned <see cref="WaveStream"/>'s
    /// <c>Read</c> will be called on the audio thread at 48 kHz. The
    /// caller must guarantee that the input Stream's <c>Read</c> doesn't
    /// block indefinitely — a network-backed Stream must buffer on a
    /// background thread and serve from an in-memory buffer.</para>
    ///
    /// <para>Default implementation throws
    /// <see cref="NotSupportedException"/>. Codecs that opt in must
    /// override both this method AND <see cref="SupportsStreamInput"/>
    /// (and ideally <see cref="SupportedContentTypes"/> so they're
    /// findable in the registry).</para>
    /// </summary>
    WaveStream CreateStream(Stream source, string formatHint)
        => throw new NotSupportedException(
            "This codec does not implement Stream input. Check SupportsStreamInput before calling.");

    /// <summary>
    /// <c>true</c> when this codec can also encode PCM (not just decode) —
    /// see <see cref="CreateEncoder"/>. Defaults to <c>false</c>; codecs
    /// that want to be borrow-able by bridge plugins (Discord / Zoom / …)
    /// must override and implement the matching factory.
    ///
    /// <para><b>Why it's opt-in.</b> Most decoder libraries don't have an
    /// encoder counterpart, or only have one in a separate native package
    /// the host doesn't need to pay for. Pure-managed Opus (Concentus),
    /// pure-managed Vorbis (NVorbis can decode only), pure-managed FLAC,
    /// AAC via FFmpeg.AutoGen — each codec plugin makes its own decision.</para>
    /// </summary>
    bool SupportsEncoding => false;

    /// <summary>
    /// Build an encoder configured for the supplied PCM input format.
    /// Returns <c>null</c> when this codec doesn't support encoding (the
    /// default), or when the requested
    /// <paramref name="sampleRate"/>/<paramref name="channels"/>/<paramref name="bitrate"/>
    /// combo isn't supported by the underlying library.
    ///
    /// <para>Caller owns the lifetime of the returned encoder and disposes
    /// it when the stream ends. The encoder may hold significant state
    /// (Opus's predictor / lookahead) — bridges should never re-use one
    /// across reconnects.</para>
    ///
    /// <para>Default returns <c>null</c>. Codecs that opt in via
    /// <see cref="SupportsEncoding"/> must also override this.</para>
    /// </summary>
    IAudioFrameEncoder? CreateEncoder(int sampleRate, int channels, int bitrate) => null;

    /// <summary>
    /// Build a decoder for compressed packets of this codec's format.
    /// Used by bridge plugins receiving remote audio. Returns <c>null</c>
    /// for codecs that don't support packet-level decode (most don't —
    /// they only know how to decode whole files / streams via
    /// <see cref="CreateStream(string)"/>).
    ///
    /// <para>Default returns <c>null</c>.</para>
    /// </summary>
    IAudioFrameDecoder? CreateDecoder() => null;
}
