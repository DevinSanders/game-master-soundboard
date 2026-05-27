using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NSubstitute;
using SoundBoard.Core.Plugins;
using SoundBoard.PluginApi;

namespace SoundBoard.Tests.Plugins;

/// <summary>
/// Pins the lookup behaviour of <see cref="AudioCodecRegistry"/>:
/// extension lookup, MIME lookup with parameter stripping, first-claim-wins
/// on collision, leading-dot normalisation, and the empty-registry case.
///
/// <para>The registry is the safety boundary for inter-plugin codec
/// dispatch. Tests live close to it, not behind the host plumbing, so
/// regressions surface at the lookup layer rather than as mysterious
/// runtime failures inside the webstream plugin.</para>
/// </summary>
public class AudioCodecRegistryTests
{
    private static IAudioCodecPlugin FakeCodec(string id, IEnumerable<string> exts, IEnumerable<string>? mimes = null)
    {
        var codec = Substitute.For<IAudioCodecPlugin>();
        codec.Id.Returns(id);
        codec.SupportedPatterns.Returns(exts);
        codec.SupportedContentTypes.Returns(mimes ?? new string[0]);
        codec.SupportsStreamInput.Returns(mimes != null);
        return codec;
    }

    [Fact]
    public void GetByExtension_MatchesRegisteredCodec()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByExtension(".mp3").Should().Be(mp3);
    }

    [Fact]
    public void GetByExtension_IsCaseInsensitive()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByExtension(".MP3").Should().Be(mp3);
        registry.GetByExtension(".Mp3").Should().Be(mp3);
    }

    [Fact]
    public void GetByExtension_AcceptsLeadingDotOmission()
    {
        // Callers routinely forget the leading dot. Accept both forms —
        // it's a tiny convenience but a common bug otherwise.
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByExtension("mp3").Should().Be(mp3);
    }

    [Fact]
    public void GetByExtension_UnknownReturnsNull()
    {
        var registry = new AudioCodecRegistry(new[]
        {
            FakeCodec("codec.mp3", new[] { ".mp3" }),
        });
        registry.GetByExtension(".xyz").Should().BeNull();
    }

    [Fact]
    public void GetByExtension_IgnoresUrlSchemePatterns()
    {
        // codec.webstream registers "http://" / "https://" as
        // SupportedPatterns but those aren't extensions — they shouldn't
        // appear in the extension index. (If they did, GetByExtension
        // would match URLs by mistake.)
        var web = FakeCodec("codec.webstream", new[] { "http://", "https://" });
        var registry = new AudioCodecRegistry(new[] { web });

        registry.GetByExtension(".http").Should().BeNull();
        registry.GetByExtension("http://").Should().BeNull();
    }

    [Fact]
    public void GetByContentType_MatchesRegisteredCodec()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg", "audio/mp3" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByContentType("audio/mpeg").Should().Be(mp3);
        registry.GetByContentType("audio/mp3").Should().Be(mp3);
    }

    [Fact]
    public void GetByContentType_StripsParameters()
    {
        // Content-Type headers routinely carry parameters we don't care
        // about. The registry should ignore them.
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByContentType("audio/mpeg; charset=utf-8").Should().Be(mp3);
        registry.GetByContentType("audio/mpeg;rate=44100").Should().Be(mp3);
    }

    [Fact]
    public void GetByContentType_IsCaseInsensitive()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByContentType("AUDIO/MPEG").Should().Be(mp3);
    }

    [Fact]
    public void GetByContentType_UnknownReturnsNull()
    {
        var registry = new AudioCodecRegistry(new[]
        {
            FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" }),
        });
        registry.GetByContentType("audio/xyz").Should().BeNull();
    }

    [Fact]
    public void FirstClaimWins_OnExtensionCollision()
    {
        // Two plugins both claim ".mp3". The registry preserves
        // registration order: the first registered wins. This matches
        // AudioFileReaderCrossPlatform's runtime routing.
        var first = FakeCodec("codec.mp3.builtin", new[] { ".mp3" });
        var second = FakeCodec("codec.mp3.fancy", new[] { ".mp3" });
        var registry = new AudioCodecRegistry(new[] { first, second });

        registry.GetByExtension(".mp3").Should().Be(first);
    }

    [Fact]
    public void FirstClaimWins_OnMimeCollision()
    {
        var first  = FakeCodec("codec.mp3.builtin", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var second = FakeCodec("codec.mp3.fancy",   new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { first, second });

        registry.GetByContentType("audio/mpeg").Should().Be(first);
    }

    [Fact]
    public void All_ReturnsEveryCodec()
    {
        var mp3  = FakeCodec("codec.mp3",  new[] { ".mp3" });
        var ogg  = FakeCodec("codec.ogg",  new[] { ".ogg" });
        var flac = FakeCodec("codec.flac", new[] { ".flac" });
        var registry = new AudioCodecRegistry(new[] { mp3, ogg, flac });

        registry.All.Should().BeEquivalentTo(new[] { mp3, ogg, flac });
    }

    [Fact]
    public void EmptyRegistry_AllLookupsReturnNull()
    {
        var registry = new AudioCodecRegistry(new IAudioCodecPlugin[0]);

        registry.GetByExtension(".mp3").Should().BeNull();
        registry.GetByContentType("audio/mpeg").Should().BeNull();
        registry.All.Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmptyExtension_ReturnsNull()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByExtension(null!).Should().BeNull();
        registry.GetByExtension("").Should().BeNull();
    }

    [Fact]
    public void NullOrEmptyMime_ReturnsNull()
    {
        var mp3 = FakeCodec("codec.mp3", new[] { ".mp3" }, new[] { "audio/mpeg" });
        var registry = new AudioCodecRegistry(new[] { mp3 });

        registry.GetByContentType(null!).Should().BeNull();
        registry.GetByContentType("").Should().BeNull();
        registry.GetByContentType("   ").Should().BeNull();
    }
}
