using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Phase D tests for the host-side <see cref="SidechainRegistry"/> —
/// the registry that hands plugins one <see cref="PluginApi.ISidechainSource"/>
/// per audio bus. Pins the live-refresh contract (bus add / rename /
/// delete updates plugin-visible state).
/// </summary>
public class SidechainRegistryTests
{
    private static void SeedBuses(SqliteInMemoryDbFixture fx, params (int id, string name, int order)[] buses)
    {
        using var db = fx.CreateContext();
        foreach (var (id, name, order) in buses)
            db.Buses.Add(new Bus { Id = id, Name = name, Order = order, IsBuiltIn = true });
        db.SaveChanges();
    }

    [Fact]
    public void GetSources_ReturnsOneSourcePerBus_InBusOrder()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx,
            (BuiltInBusIds.Music, "Music", 0),
            (BuiltInBusIds.Ambient, "Ambient", 10),
            (BuiltInBusIds.Sfx, "SFX", 20));
        using var mixer = new MasterMixer();
        mixer.EnsureBuses(new[] { 1, 2, 3 });

        var registry = new SidechainRegistry(mixer, fx.Factory);

        var sources = registry.GetSources();
        sources.Should().HaveCount(3);
        sources.Select(s => s.Id).Should().Equal("bus:1", "bus:2", "bus:3");
        sources.Select(s => s.DisplayName).Should().Equal("Music", "Ambient", "SFX");
    }

    [Fact]
    public void GetSourceById_FindsByStableId()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (BuiltInBusIds.Music, "Music", 0));
        using var mixer = new MasterMixer();
        mixer.EnsureBus(BuiltInBusIds.Music);

        var registry = new SidechainRegistry(mixer, fx.Factory);

        registry.GetSourceById("bus:1").Should().NotBeNull();
        registry.GetSourceById("bus:999").Should().BeNull();
        registry.GetSourceById("").Should().BeNull();
    }

    [Fact]
    public void Source_Subscribe_FansBusAudioToPlugin()
    {
        // End-to-end check: a plugin subscribed via the SDK contract
        // sees the bus's audio when the bus is Read on the audio thread.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (BuiltInBusIds.Music, "Music", 0));
        using var mixer = new MasterMixer();
        var busMixer = mixer.EnsureBus(BuiltInBusIds.Music);
        busMixer.AddMixerInput(new CountingSampleProvider());

        var registry = new SidechainRegistry(mixer, fx.Factory);
        var source = registry.GetSourceById("bus:1")!;

        float[]? received = null;
        using var sub = source.Subscribe((buffer, count) =>
        {
            received = new float[count];
            System.Array.Copy(buffer, received, count);
        });

        var buf = new float[4];
        busMixer.Read(buf, 0, 4);

        received.Should().Equal(1f, 2f, 3f, 4f);
    }

    [Fact]
    public void Refresh_FiresEventWhenBusesChange()
    {
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (BuiltInBusIds.Music, "Music", 0));
        using var mixer = new MasterMixer();
        mixer.EnsureBus(BuiltInBusIds.Music);

        var registry = new SidechainRegistry(mixer, fx.Factory);
        int events = 0;
        registry.SourcesChanged += (_, _) => events++;

        // Add a new bus + the corresponding bus mixer, then refresh.
        using (var db = fx.CreateContext())
        {
            db.Buses.Add(new Bus { Id = 42, Name = "Voiceover", Order = 100, IsBuiltIn = false });
            db.SaveChanges();
        }
        mixer.EnsureBus(42);
        registry.Refresh();

        events.Should().Be(1);
        registry.GetSources().Should().HaveCount(2);
        registry.GetSourceById("bus:42")!.DisplayName.Should().Be("Voiceover");
    }

    [Fact]
    public void Refresh_PreservesExistingSourceReference_WhenBusUnchanged()
    {
        // If the source list is functionally the same (just a rename),
        // a subscriber holding the ISidechainSource reference should
        // continue to work. SidechainRegistry reuses the existing
        // BusSidechainSource instance and just updates its display
        // name when the bus id is unchanged.
        using var fx = new SqliteInMemoryDbFixture();
        SeedBuses(fx, (BuiltInBusIds.Music, "Music", 0));
        using var mixer = new MasterMixer();
        mixer.EnsureBus(BuiltInBusIds.Music);

        var registry = new SidechainRegistry(mixer, fx.Factory);
        var firstRef = registry.GetSourceById("bus:1");

        // Rename via DB + refresh.
        using (var db = fx.CreateContext())
        {
            var b = db.Buses.Find(BuiltInBusIds.Music);
            b!.Name = "Background";
            db.SaveChanges();
        }
        registry.Refresh();

        var secondRef = registry.GetSourceById("bus:1");
        secondRef.Should().BeSameAs(firstRef, "subscribers holding the reference must keep receiving samples across a rename");
        secondRef!.DisplayName.Should().Be("Background");
    }
}
