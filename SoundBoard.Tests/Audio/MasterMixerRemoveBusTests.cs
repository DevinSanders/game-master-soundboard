using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase C tests for <see cref="MasterMixer.RemoveBus"/> — used when
/// the Settings → Buses page deletes a custom bus. The chain-service
/// detaches FX rows first; this layer's job is to drop the BusMixer
/// itself from the combine.
/// </summary>
public class MasterMixerRemoveBusTests
{
    [Fact]
    public void RemoveBus_DropsBusFromBusIds()
    {
        using var mixer = new MasterMixer();
        mixer.EnsureBus(BuiltInBusIds.Music);
        mixer.EnsureBus(99);

        mixer.RemoveBus(99);

        mixer.BusIds.Should().BeEquivalentTo(new[] { BuiltInBusIds.Music });
        mixer.GetBus(99).Should().BeNull();
    }

    [Fact]
    public void RemoveBus_UnknownBus_IsNoop()
    {
        // The settings deletion flow runs DB ops first then calls
        // RemoveBus — if the BusMixer was never materialised (e.g. the
        // user added the bus and immediately deleted it without any
        // track activity), the mixer side should silently no-op.
        using var mixer = new MasterMixer();

        var act = () => mixer.RemoveBus(busId: 999);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBus_AfterRemove_CreatesFreshMixer()
    {
        // The remove-then-reuse case isn't expected in practice (bus ids
        // are AUTOINCREMENT so a deleted id isn't reissued), but the
        // contract should hold defensively — a future re-EnsureBus with
        // the same id produces a fresh mixer, not a resurrected stale one.
        using var mixer = new MasterMixer();
        var first = mixer.EnsureBus(42);
        mixer.RemoveBus(42);
        var second = mixer.EnsureBus(42);

        second.Should().NotBeSameAs(first);
    }
}
