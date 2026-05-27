using System.Collections.Generic;

namespace SoundBoard.PluginApi;

/// <summary>
/// Read-only snapshot of the codec plugins the host has loaded.
/// Exposed to plugins through <see cref="IPluginContext.CodecRegistry"/>
/// so a plugin can dispatch to another codec's decoder without bundling
/// its own copy of every decoder library.
///
/// <para><b>Why this exists.</b> Plugins like <c>codec.webstream</c> are
/// fundamentally transport layers — they open an HTTP / ICY stream — and
/// then need to hand the bytes to whatever codec matches the
/// <c>Content-Type</c>. Without this registry the transport plugin would
/// have to bundle NLayer + NVorbis + BunLabs.NAudio.Flac + … and route
/// internally, duplicating decoders the user already has installed as
/// separate codec plugins. The registry lets the transport plugin ask
/// "give me the codec that handles <c>audio/mpeg</c>" at the moment the
/// HEAD probe finishes.</para>
///
/// <para><b>Lifetime and threading.</b> The registry is a <b>one-shot
/// snapshot</b>, built once after all plugins finish loading at app
/// startup. It is never mutated after that — plugins installed later in
/// the session don't appear until the next launch. All lookup methods
/// are safe to call from any thread, including the audio thread,
/// without locking.</para>
///
/// <para><b>Boundaries (read these — they're the safety contract).</b></para>
/// <list type="bullet">
///   <item><b>No recursive dispatch.</b> A codec plugin MUST NOT call
///     the registry from within its own <see cref="IAudioCodecPlugin.CreateStream(System.IO.Stream, string)"/>.
///     Routing is the responsibility of the calling (transport) plugin,
///     not the receiving codec. This keeps the plugin dependency graph
///     a tree, not a cycle.</item>
///   <item><b>No mutation.</b> Plugins can only read. The host owns the
///     codec list; a plugin cannot inject, remove, or reorder codecs.</item>
///   <item><b>No identity guarantees beyond the manifest contract.</b>
///     If two plugins claim the same extension or MIME, the host returns
///     whichever loaded first. Don't rely on a specific plugin's
///     implementation — only on its declared contract.</item>
/// </list>
/// </summary>
public interface IAudioCodecRegistry
{
    /// <summary>Look up the codec that handles <paramref name="extension"/>.
    /// Pass the leading-dot form (<c>".mp3"</c>). Case-insensitive. Returns
    /// <c>null</c> when no installed codec claims the extension.</summary>
    IAudioCodecPlugin? GetByExtension(string extension);

    /// <summary>Look up the codec that handles <paramref name="mimeType"/>
    /// (<c>"audio/mpeg"</c>, <c>"audio/ogg"</c>, …). Case-insensitive.
    /// Returns <c>null</c> when no installed codec claims the type.
    /// MIME → codec mapping comes from each codec's
    /// <see cref="IAudioCodecPlugin.SupportedContentTypes"/> property.</summary>
    IAudioCodecPlugin? GetByContentType(string mimeType);

    /// <summary>Every codec the host has loaded. Snapshot at construction
    /// time. Useful for diagnostics and for transport plugins that want
    /// to log "found N codecs that accept Stream input" at startup.</summary>
    IEnumerable<IAudioCodecPlugin> All { get; }
}
