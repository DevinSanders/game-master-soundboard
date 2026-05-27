using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Phase R3 tests for <see cref="SidechainRegistry.Refresh"/>: rename
/// propagation. Pre-fix the change-detection only compared source ids,
/// so a bus rename updated the source's DisplayName in place but never
/// fired <see cref="SidechainRegistry.SourcesChanged"/>. Plugin
/// source-picker UIs kept showing the old name until some other
/// action invalidated them.
/// </summary>
public class SidechainRegistryR3Tests
{
    private static void SeedBuses(SqliteInMemoryDbFixture fx, params (int id, string name)[] buses)
    {
        using var db = fx.CreateContext();
        int order = 0;
        foreach (var (id, name) in buses)
            db.Buses.Add(new Bus { Id = id, Name = name, Order = order++, IsBuiltIn = false });
        db.SaveChanges();
    }

    [Fact]
    public void Refresh_FiresSourcesChanged_OnRename()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (1, "Music"));
        using var mixer = new MasterMixer();
        mixer.EnsureBus(1);

        var registry = new SidechainRegistry(mixer, fx.Factory);
        int events = 0;
        registry.SourcesChanged += (_, _) => events++;

        // Rename the bus.
        using (var db = fx.CreateContext())
        {
            var b = db.Buses.Find(1);
            b!.Name = "Background";
            db.SaveChanges();
        }
        registry.Refresh();

        events.Should().Be(1, "rename must fire SourcesChanged so plugin UIs re-bind");
        registry.GetSourceById("bus:1")!.DisplayName.Should().Be("Background");
    }

    [Fact]
    public void Refresh_NoEvent_WhenNothingChanged()
    {
        // Sanity check: calling Refresh twice in a row with no DB
        // change should NOT spuriously fire the event. Plugin UIs
        // would over-render if it did.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (1, "Music"));
        using var mixer = new MasterMixer();
        mixer.EnsureBus(1);

        var registry = new SidechainRegistry(mixer, fx.Factory);
        int events = 0;
        registry.SourcesChanged += (_, _) => events++;

        registry.Refresh();
        registry.Refresh();
        registry.Refresh();

        events.Should().Be(0);
    }
}
