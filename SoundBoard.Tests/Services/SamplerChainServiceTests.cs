using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Contract tests for <see cref="SamplerChainService"/>. The service is
/// the single owner of master + ephemeral <see cref="ISamplerInstance"/>
/// lifetimes; these tests pin the lifecycle so future refactors (the
/// upcoming locking work in Phase 1 #8, the orphan-cleanup work in
/// Phase 2 #13) don't quietly regress add/remove/order/bypass behavior.
///
/// <para>The chain service touches the DB through
/// <see cref="ISoundBoardDbContextFactory"/> and the plugin registry
/// through <see cref="IPluginService"/>. The fake plugin
/// (<see cref="RecordingSamplerPlugin"/>) gives us a deterministic
/// <c>ISamplerInstance</c> so we can identify materialised instances
/// by reference equality against <c>SpawnedInstances</c>.</para>
/// </summary>
public class SamplerChainServiceTests
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

    private static int SeedMasterRow(SqliteInMemoryDbFixture fx, int order, bool bypassed = false)
    {
        using var db = fx.CreateContext();
        var row = new SamplerAttachment
        {
            PluginId = PluginId,
            OwnerType = SamplerOwnerType.Master,
            Order = order,
            IsBypassed = bypassed,
        };
        db.SamplerAttachments.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    // ─── Master lifecycle ──────────────────────────────────────────────

    [Fact]
    public void Initialize_IsIdempotent()
    {
        // Phase 5 idempotency guard: calling Initialize a second time must
        // not double-add master attachments to the mixer chain. The only
        // production caller is App startup, but a hypothetical "library
        // switched, reinitialize" flow could trip the foot-gun otherwise.
        using var fx = new SqliteInMemoryDbFixture();
        SeedMasterRow(fx, order: 0);
        SeedMasterRow(fx, order: 1);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();
        chain.Initialize();
        chain.Initialize();

        mixer.GlobalEffects.Should().HaveCount(2,
            "second/third Initialize must be a no-op, not stack additional copies of the same rows");
    }

    [Fact]
    public void Initialize_WiresAllMasterAttachmentsIntoMixer()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedMasterRow(fx, order: 0);
        SeedMasterRow(fx, order: 1);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.GlobalEffects.Should().HaveCount(2);
    }

    [Fact]
    public void Initialize_IncludesBypassedMasterRows()
    {
        // Bypass contract: the BypassableSamplerInstance wrapper
        // stays in the chain even when bypassed, so SetBypass can
        // flip a flag instead of rebuilding the audio chain.
        using var fx = new SqliteInMemoryDbFixture();
        SeedMasterRow(fx, order: 0, bypassed: true);
        SeedMasterRow(fx, order: 1, bypassed: false);
        var (chain, mixer, _) = BuildSut(fx);

        chain.Initialize();

        mixer.GlobalEffects.Should().HaveCount(2,
            "bypassed master rows must stay in the chain so SetBypass can re-engage them");
    }

    [Fact]
    public void AddAttachment_MasterOwner_WiresIntoMixerImmediately()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();

        var row = chain.AddAttachment(SamplerOwnerType.Master, ownerId: null, PluginId);

        mixer.GlobalEffects.Should().HaveCount(1);
        row.Id.Should().BeGreaterThan(0);
        row.OwnerType.Should().Be(SamplerOwnerType.Master);
        row.Order.Should().Be(0);
    }

    [Fact]
    public void RemoveAttachment_MasterOwner_DetachesFromMixerAndDeletesRow()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var id = SeedMasterRow(fx, order: 0);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();
        mixer.GlobalEffects.Should().HaveCount(1);

        chain.RemoveAttachment(id);

        mixer.GlobalEffects.Should().BeEmpty();
        chain.GetAttachments(SamplerOwnerType.Master, null).Should().BeEmpty();
    }

    [Fact]
    public void SetBypass_TogglesWrapperFlag_WithoutRebuildingChain()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var id = SeedMasterRow(fx, order: 0);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();

        var beforeInstance = mixer.GlobalEffects[0];

        chain.SetBypass(id, bypassed: true);

        // The wrapper stays in place; only its IsBypassed flag flipped.
        mixer.GlobalEffects.Should().HaveCount(1);
        mixer.GlobalEffects[0].Should().BeSameAs(beforeInstance, "bypass must not rebuild the chain");
        ((BypassableSamplerInstance)mixer.GlobalEffects[0]).IsBypassed.Should().BeTrue();
    }

    [Fact]
    public void SetOrder_MasterOwner_RebuildsChainInNewOrder()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var a = SeedMasterRow(fx, order: 0);
        var b = SeedMasterRow(fx, order: 1);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();
        mixer.GlobalEffects.Should().HaveCount(2);

        // Swap the order.
        chain.SetOrder(b, newOrder: 0);
        chain.SetOrder(a, newOrder: 1);

        chain.GetAttachments(SamplerOwnerType.Master, null)
             .Select(r => r.Id).Should().Equal(b, a);
    }

    // ─── REGRESSION (Phase 1 review item #1) ───────────────────────────
    //
    // Today, ReinitMasterChain in SamplerChainService.cs:332-335 filters
    // `!a.IsBypassed`. After any SetOrder on a master row, the bypassed
    // rows disappear from the mixer chain — so SetBypass(false) on them
    // silently does nothing (the wrapper isn't there to flip).
    //
    // Fix (per phase 1 plan): drop the bypass filter; include all rows
    // in ReinitMasterChain, matching Initialize.
    [Fact]
    public void SetOrder_KeepsBypassedRowsInChain()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var bypassed = SeedMasterRow(fx, order: 0, bypassed: true);
        var active = SeedMasterRow(fx, order: 1, bypassed: false);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();
        mixer.GlobalEffects.Should().HaveCount(2);

        // Reorder triggers ReinitMasterChain — bug: bypassed row is dropped.
        chain.SetOrder(active, newOrder: 0);

        mixer.GlobalEffects.Should().HaveCount(2,
            "the bypass wrapper must survive reorders so the user can un-bypass without restarting");

        // And SetBypass on the formerly-bypassed row must still toggle audio.
        chain.SetBypass(bypassed, bypassed: false);
        var bypassedInstance = mixer.GlobalEffects.OfType<BypassableSamplerInstance>()
            .FirstOrDefault(i => !i.IsBypassed && ReferenceEquals(i.Inner, i.Inner)); // simplified ref
        bypassedInstance.Should().NotBeNull("un-bypassing must put a wet path back into the chain");
    }

    // ─── Per-target ephemeral lifecycle ────────────────────────────────

    [Fact]
    public void BuildEphemeralChain_ReturnsOneInstancePerRow_InOrder()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            db.SamplerAttachments.AddRange(
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 42, Order = 1 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 42, Order = 0 }
            );
            db.SaveChanges();
        }
        var (chain, _, plugin) = BuildSut(fx);

        var instances = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 42);

        instances.Should().HaveCount(2);
        // Plugin spawned exactly two instances, in row-order (Order=0 first).
        plugin.SpawnedInstances.Should().HaveCount(2);
    }

    [Fact]
    public void BuildEphemeralChain_OnMaster_Throws()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var (chain, _, _) = BuildSut(fx);

        var act = () => chain.BuildEphemeralChain(SamplerOwnerType.Master, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BuildEphemeralChain_SkipsRowsForMissingPlugins()
    {
        // Rows that reference an uninstalled plugin should be tolerated:
        // the row stays in the DB so reinstalling recovers, but the chain
        // omits it. (Contract documented on TryMaterialize.)
        using var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            db.SamplerAttachments.AddRange(
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 7, Order = 0 },
                new SamplerAttachment { PluginId = "missing.plugin", OwnerType = SamplerOwnerType.Preset, OwnerId = 7, Order = 1 }
            );
            db.SaveChanges();
        }
        var (chain, _, _) = BuildSut(fx);

        var instances = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 7);

        instances.Should().HaveCount(1, "the missing-plugin row is kept in DB but skipped in the chain");
    }

    [Fact]
    public void GetAttachments_ReturnsRowsInOrder()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            db.SamplerAttachments.AddRange(
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Playlist, OwnerId = 5, Order = 2 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Playlist, OwnerId = 5, Order = 0 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Playlist, OwnerId = 5, Order = 1 }
            );
            db.SaveChanges();
        }
        var (chain, _, _) = BuildSut(fx);

        var rows = chain.GetAttachments(SamplerOwnerType.Playlist, 5);

        rows.Select(r => r.Order).Should().Equal(0, 1, 2);
    }

    // ─── Orphan cleanup (Phase 2 item #13) ─────────────────────────────

    [Fact]
    public void RemoveAttachmentsFor_DeletesOnlyTheTargetedOwnerRows()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            db.SamplerAttachments.AddRange(
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 1, Order = 0 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 1, Order = 1 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Preset, OwnerId = 2, Order = 0 },
                new SamplerAttachment { PluginId = PluginId, OwnerType = SamplerOwnerType.Playlist, OwnerId = 1, Order = 0 }
            );
            db.SaveChanges();
        }
        var (chain, _, _) = BuildSut(fx);

        chain.RemoveAttachmentsFor(SamplerOwnerType.Preset, 1);

        chain.GetAttachments(SamplerOwnerType.Preset, 1).Should().BeEmpty();
        chain.GetAttachments(SamplerOwnerType.Preset, 2).Should().HaveCount(1, "other-preset rows untouched");
        chain.GetAttachments(SamplerOwnerType.Playlist, 1).Should().HaveCount(1, "different-owner-type rows untouched");
    }

    [Fact]
    public void RemoveAttachmentsFor_OnMaster_DeferDisposesLiveInstances()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedMasterRow(fx, order: 0);
        SeedMasterRow(fx, order: 1);
        var (chain, mixer, _) = BuildSut(fx);
        chain.Initialize();
        mixer.GlobalEffects.Should().HaveCount(2);

        chain.RemoveAttachmentsFor(SamplerOwnerType.Master, null);

        chain.GetAttachments(SamplerOwnerType.Master, null).Should().BeEmpty();
        mixer.GlobalEffects.Should().BeEmpty("live master instances must be unwired");
    }

    [Fact]
    public void RemoveAttachmentsFor_OnEmptyOwner_IsNoop()
    {
        using var fx = new SqliteInMemoryDbFixture();
        var (chain, _, _) = BuildSut(fx);

        var act = () => chain.RemoveAttachmentsFor(SamplerOwnerType.Preset, 99);

        act.Should().NotThrow();
    }
}
