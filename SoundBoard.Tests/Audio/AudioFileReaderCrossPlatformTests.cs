using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.IO;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Plugin codec routing in <see cref="AudioFileReaderCrossPlatform"/>:
/// dot-prefixed patterns match by extension, scheme-prefix patterns
/// match leading characters. Plugin codecs are consulted before the
/// built-in fallback. The built-in set is intentionally minimal —
/// just <c>.wav</c>, the only format NAudio's own libraries decode
/// natively. Anything else (<c>.mp3</c>, <c>.ogg</c>, <c>.flac</c>, …)
/// must come from a sibling codec-plugin repo (<c>gmsb-codec-mp3</c>,
/// <c>gmsb-codec-ogg</c>, <c>gmsb-codec-flac</c>, etc. — see
/// <c>docs/PLUGINS.md</c> for the catalog).
/// </summary>
public class AudioFileReaderCrossPlatformTests : IDisposable
{
    public AudioFileReaderCrossPlatformTests()
    {
        // The codec registry is process-global static state. Clear it
        // before each test so registrations don't leak across tests.
        AudioFileReaderCrossPlatform.ClearPlugins();
    }

    public void Dispose() => AudioFileReaderCrossPlatform.ClearPlugins();

    private sealed class FakeCodecPlugin : IAudioCodecPlugin
    {
        public FakeCodecPlugin(IEnumerable<string> patterns)
        {
            SupportedPatterns = patterns;
        }

        public string Id => "test.codec";
        public string Name => "Test Codec";
        public string Description => "test";
        public string Version => "1";
        public string Author => "tests";

        public IEnumerable<string> SupportedPatterns { get; }

        public int CreateStreamCallCount;
        public string? LastSource;

        public NAudio.Wave.WaveStream CreateStream(string source)
        {
            CreateStreamCallCount++;
            LastSource = source;
            // Return a minimal WaveStream — GenericSeekableSampleProvider
            // wraps it. We just need to verify routing reaches this point.
            return new FakeWaveStream();
        }

        public void Initialize(IPluginContext context) { }
        public void Shutdown() { }
    }

    private sealed class FakeWaveStream : NAudio.Wave.WaveStream
    {
        public override WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public override long Length => 0;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => 0;
    }

    [Fact]
    public void ExtensionPattern_RoutesToPlugin()
    {
        var plugin = new FakeCodecPlugin(new[] { ".flac" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        // Use a path that doesn't actually exist; the plugin's CreateStream
        // is a fake so file I/O isn't attempted.
        AudioFileReaderCrossPlatform.Create("/nonexistent.flac");

        plugin.CreateStreamCallCount.Should().Be(1);
        plugin.LastSource.Should().Be("/nonexistent.flac");
    }

    [Fact]
    public void ExtensionPattern_IsCaseInsensitive()
    {
        var plugin = new FakeCodecPlugin(new[] { ".flac" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.Create("/CAPS.FLAC");

        plugin.CreateStreamCallCount.Should().Be(1);
    }

    [Fact]
    public void SchemePrefixPattern_RoutesToPlugin()
    {
        var plugin = new FakeCodecPlugin(new[] { "http://" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.Create("http://example.com/stream.mp3");

        plugin.CreateStreamCallCount.Should().Be(1);
    }

    [Fact]
    public void SchemePrefix_IsCaseInsensitive()
    {
        var plugin = new FakeCodecPlugin(new[] { "http://" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.Create("HTTP://example.com/stream.mp3");

        plugin.CreateStreamCallCount.Should().Be(1);
    }

    [Fact]
    public void PluginCodecs_WinOverBuiltIns_ForOverlappingExtensions()
    {
        // A plugin that claims .wav should intercept before the built-in
        // WaveFileReader fallback. Plugin authors need to be able to
        // replace any built-in with their own implementation.
        var plugin = new FakeCodecPlugin(new[] { ".wav" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.Create("/song.wav");

        plugin.CreateStreamCallCount.Should().Be(1, "the plugin must beat the built-in WaveFileReader routing");
    }

    [Fact]
    public void NoPlugin_NoBuiltIn_Throws()
    {
        // Only .wav has a built-in fallback today. .xyz has neither
        // built-in nor plugin support, so Create must throw.
        var act = () => AudioFileReaderCrossPlatform.Create("/file.xyz");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void NoPlugin_Mp3Extension_Throws_BecauseMp3IsPluginOnly()
    {
        // Pinned regression: NLayer / MP3 used to live in Core's built-in
        // switch. It now ships as the sibling gmsb-codec-mp3 repo per the rule
        // that only formats NAudio's own libraries can decode natively
        // (.wav today) stay built-in. Without a registered MP3 plugin
        // .mp3 must throw.
        var act = () => AudioFileReaderCrossPlatform.Create("/song.mp3");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void NoPlugin_OggExtension_Throws_BecauseOggIsPluginOnly()
    {
        // Pinned regression: NVorbis used to live in Core's built-in
        // switch. It now ships as the sibling gmsb-codec-ogg repo, so without
        // a registered codec plugin .ogg must throw. If a future
        // refactor accidentally re-introduces a built-in .ogg branch
        // this test catches it.
        var act = () => AudioFileReaderCrossPlatform.Create("/song.ogg");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void NoPlugin_FlacExtension_Throws_BecauseFlacIsPluginOnly()
    {
        // Sibling of the .ogg test — FLAC is also plugin-only.
        var act = () => AudioFileReaderCrossPlatform.Create("/song.flac");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void EmptySource_Throws()
    {
        var act = () => AudioFileReaderCrossPlatform.Create("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ClearPlugins_RemovesRegistrations()
    {
        var plugin = new FakeCodecPlugin(new[] { ".flac" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.ClearPlugins();

        var act = () => AudioFileReaderCrossPlatform.Create("/file.flac");
        act.Should().Throw<NotSupportedException>(
            "after Clear, the unsupported extension falls through to the built-in switch");
    }

    [Fact]
    public void RegisterSamePlugin_IsIdempotent()
    {
        var plugin = new FakeCodecPlugin(new[] { ".flac" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        AudioFileReaderCrossPlatform.Create("/song.flac");

        plugin.CreateStreamCallCount.Should().Be(1,
            "registering the same instance multiple times shouldn't cause multi-dispatch");
    }

    [Fact]
    public void InvalidPattern_IsIgnoredQuietly()
    {
        // Patterns without "." prefix or "://" suffix are documented as
        // silently ignored. A plugin with only invalid patterns should
        // never match.
        var plugin = new FakeCodecPlugin(new[] { "garbage", "no-dot-or-slashes" });
        AudioFileReaderCrossPlatform.RegisterPlugin(plugin);

        var act = () => AudioFileReaderCrossPlatform.Create("/song.flac");
        act.Should().Throw<NotSupportedException>();
        plugin.CreateStreamCallCount.Should().Be(0);
    }
}
