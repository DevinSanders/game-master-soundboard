using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Cross-platform audio source factory. Routes file extensions and URL
/// scheme prefixes to the appropriate decoder.
///
/// <para><b>Built-in rule:</b> only file types that NAudio's own
/// libraries can decode stay built-in here. Today that's <c>.wav</c>
/// via <c>WaveFileReader</c> — full stop. Anything that needs a
/// third-party decoder (MP3 / OGG / FLAC / Opus / AAC / ...) ships as
/// a separate <see cref="IAudioCodecPlugin"/> in its own sibling repo
/// (e.g. <c>gmsb-codec-mp3</c>, <c>gmsb-codec-ogg</c>,
/// <c>gmsb-codec-flac</c>, etc. — see <c>docs/PLUGINS.md</c> for the
/// catalog). The rule keeps Core's dependency footprint minimal and
/// treats the codec-plugin extension point as the canonical way to add
/// new formats.</para>
///
/// <para>Plugin codecs are consulted first; the built-in WAV fallback
/// kicks in only when no plugin claims the source.</para>
///
/// <para>Pattern dispatch: input strings are matched against each
/// plugin's <see cref="IAudioCodecPlugin.SupportedPatterns"/> in two
/// passes — extensions first (matched against
/// <c>Path.GetExtension</c>), then scheme prefixes (matched against the
/// leading characters of the input).</para>
/// </summary>
public static class AudioFileReaderCrossPlatform
{
    private static readonly List<IAudioCodecPlugin> _codecPlugins = new();

    public static void RegisterPlugin(IAudioCodecPlugin plugin)
    {
        if (!_codecPlugins.Contains(plugin))
            _codecPlugins.Add(plugin);
    }

    /// <summary>Drop every registered codec plugin. Called before each
    /// <c>PluginService.DiscoverAndLoad</c> so a hypothetical
    /// plugin-reload-without-restart flow doesn't leak old plugin codecs
    /// in this process-global registry. Built-in codecs (mp3 / ogg / wav)
    /// aren't here — they live in the <see cref="Create"/> switch and
    /// aren't affected.</summary>
    public static void ClearPlugins()
    {
        _codecPlugins.Clear();
    }

    /// <summary>Read-only snapshot of the currently-registered codec
    /// plugins, in registration order. Used to seed
    /// <see cref="Plugins.AudioCodecRegistry"/> after
    /// <see cref="Services.PluginService.DiscoverAndLoad"/> finishes so
    /// transport plugins can dispatch to other codecs.</summary>
    public static IReadOnlyList<IAudioCodecPlugin> SnapshotPlugins()
        => _codecPlugins.ToArray();

    public static ISeekableSampleProvider Create(string source)
    {
        if (string.IsNullOrEmpty(source))
            throw new ArgumentException("Source is null or empty.", nameof(source));

        // Plugins win over built-ins. Check by extension first (most common),
        // then by URL scheme prefix.
        var ext = Path.GetExtension(source).ToLowerInvariant();

        foreach (var plugin in _codecPlugins)
        {
            if (PluginMatches(plugin, source, ext))
            {
                var stream = plugin.CreateStream(source);
                if (stream != null)
                    return new GenericSeekableSampleProvider(stream);
            }
        }

        // Built-in fallback — only formats NAudio's own libraries can
        // decode natively. Currently that's just .wav. Anything else
        // (.mp3, .ogg, .flac, .opus, ...) requires a codec plugin.
        return ext switch
        {
            ".wav" => new SeekableWaveSampleProvider(source),
            _      => throw new NotSupportedException(
                          $"Audio format '{ext}' (source '{source}') is not supported. " +
                          $"Install a codec plugin that claims '{ext}' to add support.")
        };
    }

    private static bool PluginMatches(IAudioCodecPlugin plugin, string source, string lowerExt)
    {
        foreach (var raw in plugin.SupportedPatterns)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var pattern = raw.ToLowerInvariant();

            // Extension: dot-prefixed (e.g. ".flac").
            if (pattern.StartsWith("."))
            {
                if (pattern == lowerExt) return true;
                continue;
            }

            // URL scheme prefix: ends in "://" (e.g. "http://").
            if (pattern.EndsWith("://"))
            {
                if (source.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            // Anything else — silently ignored. Could log a warning but the
            // SDK contract docs the two valid shapes.
        }
        return false;
    }
}

/// <summary>
/// Wraps a standard <see cref="WaveStream"/> as an
/// <see cref="ISeekableSampleProvider"/> for plugins. Surfaces the
/// underlying stream's <c>CanSeek</c> so the UI can hide scrub
/// controls on live streams.
/// </summary>
public class GenericSeekableSampleProvider : ISeekableSampleProvider
{
    private readonly WaveStream _stream;
    private readonly ISampleProvider _sampleProvider;

    public WaveFormat WaveFormat => _sampleProvider.WaveFormat;
    public bool IsSeekable => _stream.CanSeek;

    public TimeSpan TotalTime => _stream.CanSeek ? _stream.TotalTime : TimeSpan.MaxValue;

    public TimeSpan Position
    {
        get => _stream.CanSeek ? _stream.CurrentTime : TimeSpan.Zero;
        set
        {
            // Silently no-op on non-seekable streams. The host's UI is
            // expected to disable scrubbing when IsSeekable=false, but a
            // stray write from playlist-engine cleanup shouldn't throw.
            if (_stream.CanSeek) _stream.CurrentTime = value;
        }
    }

    public GenericSeekableSampleProvider(WaveStream stream)
    {
        _stream = stream;
        _sampleProvider = stream.ToSampleProvider();
    }

    public int Read(float[] buffer, int offset, int count) =>
        _sampleProvider.Read(buffer, offset, count);

    public void Dispose() => _stream.Dispose();
}
