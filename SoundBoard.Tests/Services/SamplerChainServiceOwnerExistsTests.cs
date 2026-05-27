using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Phase 2 #20: <see cref="ISamplerChainService.OwnerExists"/> tells the
/// FX Chain editor whether the owning preset/playlist/shortcut still
/// exists. Drives the "owner deleted while editor was open" empty state.
/// </summary>
public class SamplerChainServiceOwnerExistsTests
{
    private static SamplerChainService BuildSut(SqliteInMemoryDbFixture fx)
    {
        var pluginService = Substitute.For<IPluginService>();
        pluginService.LoadedPlugins.Returns(System.Array.Empty<IPlugin>());
        return new SamplerChainService(fx.Factory, pluginService, new SoundBoard.Core.Audio.MasterMixer());
    }

    [Fact]
    public void Master_AlwaysExists()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var chain = BuildSut(fx);

        chain.OwnerExists(SamplerOwnerType.Master, null).Should().BeTrue();
        chain.OwnerExists(SamplerOwnerType.Master, ownerId: 999).Should().BeTrue("Master ignores ownerId");
    }

    [Fact]
    public void Preset_ReturnsTrueWhenRowExists()
    {
        using var fx = new SqliteInMemoryDbFixture();
        int id;
        using (var db = fx.CreateContext())
        {
            var p = new Preset { Name = "Tavern" };
            db.Presets.Add(p);
            db.SaveChanges();
            id = p.Id;
        }
        var chain = BuildSut(fx);

        chain.OwnerExists(SamplerOwnerType.Preset, id).Should().BeTrue();
        chain.OwnerExists(SamplerOwnerType.Preset, id + 999).Should().BeFalse("non-existent id");
    }

    [Fact]
    public void Playlist_ReturnsTrueWhenRowExists()
    {
        using var fx = new SqliteInMemoryDbFixture();
        int id;
        using (var db = fx.CreateContext())
        {
            var p = new Playlist { Name = "Combat" };
            db.Playlists.Add(p);
            db.SaveChanges();
            id = p.Id;
        }
        var chain = BuildSut(fx);

        chain.OwnerExists(SamplerOwnerType.Playlist, id).Should().BeTrue();
    }

    [Fact]
    public void NullOwnerId_ForNonMaster_ReturnsFalse()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var chain = BuildSut(fx);

        chain.OwnerExists(SamplerOwnerType.Preset, null).Should().BeFalse();
        chain.OwnerExists(SamplerOwnerType.Playlist, null).Should().BeFalse();
        chain.OwnerExists(SamplerOwnerType.Shortcut, null).Should().BeFalse();
    }
}
