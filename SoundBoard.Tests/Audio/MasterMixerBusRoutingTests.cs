using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase B routing tests for <see cref="MasterMixer"/>'s per-bus
/// dispatch. Pins:
/// <list type="bullet">
///   <item><see cref="MasterMixer.EnsureBus"/> is idempotent — repeated
///   ids hand back the same instance instead of churning the audio
///   chain.</item>
///   <item><see cref="MasterMixer.AddMixerInput(NAudio.Wave.ISampleProvider, int)"/>
///   lands the source on the named bus, not the default — verified by
///   reading the bus directly.</item>
///   <item>The default-arg <see cref="MasterMixer.AddMixerInput(NAudio.Wave.ISampleProvider)"/>
///   still routes somewhere — pinned to the
///   <see cref="BuiltInBusIds.DefaultForNewTracks"/> bus so the existing
///   single-bus callers / tests keep working.</item>
///   <item><see cref="MasterMixer.AddBusEffect"/> applies ONLY to the
///   targeted bus — a second bus reading in parallel sees the dry
///   signal, not the wet one.</item>
///   <item>Master-tier FX (the existing <c>_globalEffects</c> chain)
///   still runs AFTER the bus combine, so it affects every bus equally.</item>
/// </list>
/// </summary>
public class MasterMixerBusRoutingTests
{
    [Fact]
    public void EnsureBus_IsIdempotent()
    {
        using var mixer = new MasterMixer();

        var a = mixer.EnsureBus(BuiltInBusIds.Music);
        var b = mixer.EnsureBus(BuiltInBusIds.Music);

        a.Should().BeSameAs(b);
        mixer.BusIds.Should().BeEquivalentTo(new[] { BuiltInBusIds.Music });
    }

    [Fact]
    public void EnsureBuses_PreCreatesAllRequestedBuses()
    {
        using var mixer = new MasterMixer();

        mixer.EnsureBuses(new[] { BuiltInBusIds.Music, BuiltInBusIds.Ambient, BuiltInBusIds.Sfx });

        mixer.BusIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        mixer.GetBus(BuiltInBusIds.Music).Should().NotBeNull();
        mixer.GetBus(BuiltInBusIds.Ambient).Should().NotBeNull();
        mixer.GetBus(BuiltInBusIds.Sfx).Should().NotBeNull();
    }

    [Fact]
    public void GetBus_ReturnsNullForUnknownBus()
    {
        using var mixer = new MasterMixer();
        mixer.GetBus(999).Should().BeNull();
    }

    [Fact]
    public void DefaultArgAddMixerInput_RoutesToMusicBus()
    {
        // Backward compatibility pin — the no-bus overload must still
        // route somewhere predictable so existing tests / startup hooks
        // that don't yet pass an explicit bus id keep working.
        using var mixer = new MasterMixer();
        mixer.AddMixerInput(new CountingSampleProvider());

        mixer.BusIds.Should().Contain(BuiltInBusIds.Music);
    }

    [Fact]
    public void AddMixerInputWithBus_LandsSourceOnNamedBus()
    {
        using var mixer = new MasterMixer();
        var src = new CountingSampleProvider();

        mixer.AddMixerInput(src, BuiltInBusIds.Sfx);

        // Read directly from the SFX bus — the source's sequence (1, 2, ..)
        // should flow through unchanged because the master combine isn't
        // involved in this read path.
        var sfx = mixer.GetBus(BuiltInBusIds.Sfx);
        sfx.Should().NotBeNull();
        var buf = new float[4];
        sfx!.Read(buf, 0, 4);
        buf.Should().Equal(1f, 2f, 3f, 4f);

        // Music bus was lazily created neither by AddMixerInput nor by
        // EnsureBus, so it should remain absent.
        mixer.GetBus(BuiltInBusIds.Music).Should().BeNull();
    }

    [Fact]
    public void AddBusEffect_AppliesOnlyToTargetedBus()
    {
        // The whole reason buses exist — a sidechain plugin that ducks
        // Music when SFX plays needs to be able to attach its gain stage
        // to Music WITHOUT affecting the SFX signal it's listening on.
        using var mixer = new MasterMixer();

        mixer.AddMixerInput(new CountingSampleProvider(), BuiltInBusIds.Music);
        mixer.AddMixerInput(new CountingSampleProvider(), BuiltInBusIds.Sfx);

        // Doubling effect on Music only.
        mixer.AddBusEffect(BuiltInBusIds.Music, new RecordingSamplerInstance(2f));

        var music = mixer.GetBus(BuiltInBusIds.Music)!;
        var sfx = mixer.GetBus(BuiltInBusIds.Sfx)!;

        var mBuf = new float[4];
        var sBuf = new float[4];
        music.Read(mBuf, 0, 4);
        sfx.Read(sBuf, 0, 4);

        mBuf.Should().Equal(2f, 4f, 6f, 8f);  // 1,2,3,4 doubled
        sBuf.Should().Equal(1f, 2f, 3f, 4f);  // dry
    }

    [Fact]
    public void GlobalEffect_RunsAfterBusCombine_AffectsEveryBusEqually()
    {
        // Master-tier FX must still post-process the combined output.
        // The classic example: a master limiter that catches the entire
        // mix before it hits the speakers regardless of which bus
        // contributed.
        using var mixer = new MasterMixer();

        // Single source on Music; SFX bus stays silent.
        mixer.AddMixerInput(new CountingSampleProvider(), BuiltInBusIds.Music);
        mixer.AddGlobalEffect(new RecordingSamplerInstance(3f));

        var buf = new float[4];
        mixer.Read(buf, 0, 4);

        // Source emits 1,2,3,4 → master multiplies by 3 → 3,6,9,12.
        // (Local volume defaults to 1.0.)
        buf.Should().Equal(3f, 6f, 9f, 12f);
    }

    [Fact]
    public void RemoveMixerInput_DetachesFromAnyBus()
    {
        // RemoveMixerInput doesn't know which bus the source landed on,
        // so it sweeps every bus. Pin the "found and removed" contract
        // so a refactor that drops the sweep silently leaks providers.
        using var mixer = new MasterMixer();
        var src = new CountingSampleProvider();
        mixer.AddMixerInput(src, BuiltInBusIds.Ambient);

        mixer.RemoveMixerInput(src);

        // Ambient bus should now read silence — the source is gone.
        var ambient = mixer.GetBus(BuiltInBusIds.Ambient)!;
        var buf = new float[4];
        ambient.Read(buf, 0, 4);
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
    }

    [Fact]
    public void RemoveBusEffect_OnUnknownBus_IsNoop()
    {
        // Defensive contract — the chain service may race a library
        // switch where buses get torn down before all attachments do.
        using var mixer = new MasterMixer();
        var inst = new RecordingSamplerInstance(1f);

        var act = () => mixer.RemoveBusEffect(busId: 999, inst);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetBusEffects_OnUnknownBus_ReturnsEmpty()
    {
        using var mixer = new MasterMixer();
        mixer.GetBusEffects(busId: 999).Should().BeEmpty();
    }
}
