using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using SoundBoard.PluginApi;

namespace SoundBoard.Core.Plugins;

/// <summary>
/// Host-side concrete <see cref="IAudioCodecRegistry"/>. Built once after
/// <see cref="Services.PluginService.DiscoverAndLoad"/> finishes; the
/// snapshot is then injected into each plugin's <see cref="PluginContext"/>
/// so transport plugins (e.g. <c>codec.webstream</c>) can resolve format
/// codecs at <c>CreateStream</c> time.
///
/// <para><b>Immutable by construction.</b> The constructor takes a single
/// pass over the loaded codec list and copies it into immutable
/// dictionaries. Plugins receive a reference but cannot mutate the
/// underlying state — read-only by the interface, and the backing
/// collections are <see cref="ImmutableDictionary{TKey, TValue}"/>
/// anyway. Lookups are lock-free and thread-safe.</para>
///
/// <para><b>Collision handling.</b> If two codec plugins claim the same
/// extension or MIME, the FIRST one to register wins. The runner-up's
/// claim is silently dropped — this matches the codec-routing
/// precedence in <see cref="Audio.AudioFileReaderCrossPlatform"/>.
/// In practice collisions are rare and the right answer is "fix the
/// plugin manifests"; logging a warning would just spam at startup.</para>
/// </summary>
public sealed class AudioCodecRegistry : IAudioCodecRegistry
{
    private readonly ImmutableArray<IAudioCodecPlugin> _all;
    private readonly ImmutableDictionary<string, IAudioCodecPlugin> _byExtension;
    private readonly ImmutableDictionary<string, IAudioCodecPlugin> _byContentType;

    /// <summary>
    /// Build a registry from the codec plugins the host has currently
    /// loaded. Pass <c>AudioFileReaderCrossPlatform</c>'s plugin list.
    /// </summary>
    public AudioCodecRegistry(IEnumerable<IAudioCodecPlugin> codecs)
    {
        _all = codecs?.ToImmutableArray() ?? ImmutableArray<IAudioCodecPlugin>.Empty;

        // Walk patterns + MIME types once. First-claim-wins on collision.
        // Extensions are normalised to a leading-dot lower-case form
        // (so callers can pass either ".mp3" or "MP3" or ".MP3"). MIME
        // types are normalised to lower-case.
        var ext = ImmutableDictionary.CreateBuilder<string, IAudioCodecPlugin>(StringComparer.OrdinalIgnoreCase);
        var mime = ImmutableDictionary.CreateBuilder<string, IAudioCodecPlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _all)
        {
            foreach (var raw in plugin.SupportedPatterns ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(raw)) continue;
                // Only dot-prefixed extensions are indexed here. URL scheme
                // prefixes ("http://") aren't useful for GetByExtension —
                // the webstream-style transport plugins query by MIME or
                // by the file extension parsed from the URL path.
                if (raw[0] != '.') continue;
                ext.TryAdd(raw, plugin);
            }

            foreach (var ct in plugin.SupportedContentTypes ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(ct)) continue;
                mime.TryAdd(ct, plugin);
            }
        }

        _byExtension = ext.ToImmutable();
        _byContentType = mime.ToImmutable();
    }

    public IAudioCodecPlugin? GetByExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        // Accept callers that forget the leading dot.
        if (extension[0] != '.') extension = "." + extension;
        return _byExtension.TryGetValue(extension, out var p) ? p : null;
    }

    public IAudioCodecPlugin? GetByContentType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        // Strip any "; charset=..." parameter — Content-Type headers
        // routinely carry parameters we don't care about.
        var semi = mimeType.IndexOf(';');
        if (semi >= 0) mimeType = mimeType[..semi];
        mimeType = mimeType.Trim();
        return _byContentType.TryGetValue(mimeType, out var p) ? p : null;
    }

    public IEnumerable<IAudioCodecPlugin> All => _all;
}
