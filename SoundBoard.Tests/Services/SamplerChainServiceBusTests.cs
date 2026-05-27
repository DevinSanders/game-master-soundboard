using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Phase B contract tests for the Bus tier of <see cref="SamplerChainService"/>.
/// Bus attachments are persistent like Master (one ISamplerInstance per
/// row, lives for the app's lifetime, edited live) but route to
/// <see cref="MasterMixer.AddBusEffect"/> instead of
/// <see cref="MasterMixer.AddGlobalEffect"/>. These tests pin the
/// add/remove/initialize/bypass/order behaviour at the bus tier and
/// confirm <see cref="SamplerChainService.BuildEphemeralChain"/> rejects
/// Bus owners (because bus chains are persistent, never ephemeral).
/// </summary>
public class SamplerChainServiceBusTests
{
    private const string PluginId = "test.recording";

    private static (SamplerChainService chain, MasterMixer mixer, RecordingSamplerPlugin plugin)
        BuildSut(SqliteInMemoryDbFixture fx)
    {
        var plugin = new RecordingSamplerPlugin(PluginId);
        var pluginService = Substitute.For<IPluginService>();
        pluginService.LoadedPlugins.Returns(new IPlugin[] { plugin });

        var mixer = new MasterMixer();
        var chain = new SamplerChainService(fx.Factory, pluginService, mixer);
        return (chain, mixer, plugin);
    }

    private static int SeedBusRow(SqliteInMemoryDbFixture fx, int busId, int order, bool bypassed = false)
    {
        using var db = fx.CreateContext();
        var row = new SamplerAttachment
        {
            PluginId = PluginId,
            OwnerType = SamplerOwnerType.Bus,
            OwnerId = busId,
            Order = order,
            IsBypassed = bypassed,
        };
        db.SamplerAttachments.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    /// <summary>Seed the three built-in buses so SamplerChainService.Initialize's
    /// pre-EnsureBuses pass has rows to read.</summary>
    private static void SeedBuses(SqliteInMemoryDbFixture fx)
    {
        using var db = fx.CreateContext();
        db.Buses.AddRange(
            new Bus { Id = BuiltInBusIds.Music, Name = "Music", Order = 0, IsBuiltIn = true },
            new Bus { Id = BuiltInBusIds.Ambient, Name = "Ambient", Order = 10, IsBuiltIn = true },
            new Bus { Id = BuiltInBusIds.Sfx, Name = "SFX", Order = 20, IsBuiltIn = true }
        );
        db.SaveChanges();
    }

    [Fact]
    public void Initialize_SeedsBusMixerVolumeFromPersistedRow()
    {
        // Bus.Volume persists across launches. SamplerChainService.Initialize
        // reads each row and writes the value back into the live BusMixer
        // so the GM's previous level balance is restored before any track
        // plays. Without this, every app start would reset every bus to
        // unity even if the user had pulled SFX down to 30 %.
        using var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            db.Buses.AddRange(
                new Bus { Id = BuiltInBusIds.Music, Name = "Music", Order = 0, IsBuiltIn = true, Volume = 0.4f },
                new Bus { Id = BuiltInBusIds.Sfx,   Name = "SFX",   Order = 20, IsBuiltIn = true, Volume = 1.5f }
            );
            db.SaveChanges();
        }
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.GetBus(BuiltInBusIds.Music)!.Volume.Should().BeApproximately(0.4f, 1e-5f);
        mixer.GetBus(BuiltInBusIds.Sfx)!.Volume.Should().BeApproximately(1.5f, 1e-5f);
    }

    [Fact]
    public void Initialize_PreCreatesAllConfiguredBuses()
    {
        // Bus mixers must exist BEFORE Bus FX attempt to attach so the
        // first AddBusEffect doesn't race a lazy bus creation with an
        // in-flight track AddMixerInput on the audio thread.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.BusIds.Should().BeEquivalentTo(new[] {
            BuiltInBusIds.Music, BuiltInBusIds.Ambient, BuiltInBusIds.Sfx });
    }

    [Fact]
    public void Initialize_WiresBusAttachmentsToCorrectBus()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 0);
        SeedBusRow(fx, busId: BuiltInBusIds.Sfx, order: 0);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.GetBusEffects(BuiltInBusIds.Music).Should().HaveCount(1);
        mixer.GetBusEffects(BuiltInBusIds.Sfx).Should().HaveCount(1);
        mixer.GetBusEffects(BuiltInBusIds.Ambient).Should().BeEmpty();
        mixer.GlobalEffects.Should().BeEmpty(
            "Bus attachments must NOT land on the master global chain");
    }

    [Fact]
    public void Initialize_IncludesBypassedBusRows()
    {
        // Same contract Master has: bypassed rows stay in the chain so
        // SetBypass can re-engage them by flipping a flag rather than
        // rebuilding the audio path.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 0, bypassed: true);
        SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 1, bypassed: false);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.GetBusEffects(BuiltInBusIds.Music).Should().HaveCount(2);
    }

    [Fact]
    public void AddAttachment_BusOwner_WiresIntoMixerImmediately()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();

        var row = chain.AddAttachment(SamplerOwnerType.Bus, ownerId: BuiltInBusIds.Sfx, PluginId);

        row.OwnerType.Should().Be(SamplerOwnerType.Bus);
        row.OwnerId.Should().Be(BuiltInBusIds.Sfx);
        mixer.GetBusEffects(BuiltInBusIds.Sfx).Should().HaveCount(1);
        mixer.GetBusEffects(BuiltInBusIds.Music).Should().BeEmpty();
    }

    [Fact]
    public void RemoveAttachment_BusOwner_DetachesFromCorrectBus()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var id = SeedBusRow(fx, busId: BuiltInBusIds.Ambient, order: 0);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();
        mixer.GetBusEffects(BuiltInBusIds.Ambient).Should().HaveCount(1);

        chain.RemoveAttachment(id);

        mixer.GetBusEffects(BuiltInBusIds.Ambient).Should().BeEmpty();
        chain.GetAttachments(SamplerOwnerType.Bus, BuiltInBusIds.Ambient).Should().BeEmpty();
    }

    [Fact]
    public void SetBypass_BusRow_FlipsWrapperFlag()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var id = SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 0);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();

        chain.SetBypass(id, bypassed: true);

        var effects = mixer.GetBusEffects(BuiltInBusIds.Music);
        effects.Should().HaveCount(1, "bypass must not rebuild the chain");
        ((BypassableSamplerInstance)effects[0]).IsBypassed.Should().BeTrue();
    }

    [Fact]
    public void SetOrder_BusRow_RebuildsBusChainInNewOrder()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var a = SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 0);
        var b = SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 1);
        var (chain, _, _) = BuildSut(fx);
        chain.Initialize();

        chain.SetOrder(b, newOrder: 0);
        chain.SetOrder(a, newOrder: 1);

        chain.GetAttachments(SamplerOwnerType.Bus, BuiltInBusIds.Music)
             .Select(r => r.Id).Should().Equal(b, a);
    }

    [Fact]
    public void BuildEphemeralChain_OnBus_Throws()
    {
        // Bus attachments are persistent, never ephemeral — calling
        // BuildEphemeralChain on them is a programmer error and must
        // surface loudly so a future engine refactor doesn't silently
        // start materialising duplicate per-spawn bus instances.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var (chain, _, _) = BuildSut(fx);

        var act = () => chain.BuildEphemeralChain(SamplerOwnerType.Bus, BuiltInBusIds.Music);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OwnerExists_BusOwner_ChecksBusesTable()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        var (chain, _, _) = BuildSut(fx);

        chain.OwnerExists(SamplerOwnerType.Bus, BuiltInBusIds.Music).Should().BeTrue();
        chain.OwnerExists(SamplerOwnerType.Bus, 9999).Should().BeFalse();
    }

    [Fact]
    public void RemoveAttachmentsFor_BusOwner_UnwiresAllRowsForThatBus()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx);
        SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 0);
        SeedBusRow(fx, busId: BuiltInBusIds.Music, order: 1);
        SeedBusRow(fx, busId: BuiltInBusIds.Sfx, order: 0);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();

        chain.RemoveAttachmentsFor(SamplerOwnerType.Bus, BuiltInBusIds.Music);

        chain.GetAttachments(SamplerOwnerType.Bus, BuiltInBusIds.Music).Should().BeEmpty();
        mixer.GetBusEffects(BuiltInBusIds.Music).Should().BeEmpty();
        chain.GetAttachments(SamplerOwnerType.Bus, BuiltInBusIds.Sfx).Should().HaveCount(1,
            "other buses must keep their rows when one bus is reset");
        mixer.GetBusEffects(BuiltInBusIds.Sfx).Should().HaveCount(1);
    }
}
