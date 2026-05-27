using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Contract tests for <see cref="BusMixer"/> — pins the per-bus mixing
/// surface that Phase B added underneath <see cref="MasterMixer"/>. The
/// audio thread invariants (Volatile read of the chain pointer, fan-out
/// to the combiner) are exercised indirectly via the MasterMixer routing
/// tests; this file focuses on the BusMixer itself in isolation.
/// </summary>
public class BusMixerTests
{
    private static WaveFormat F => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public void Defaults_AreReadFully_AndCarryProvidedFormat()
    {
        var bus = new BusMixer(busId: 7, F);

        bus.WaveFormat.SampleRate.Should().Be(48000);
        bus.WaveFormat.Channels.Should().Be(2);
        bus.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
        bus.BusId.Should().Be(7);
    }

    [Fact]
    public void Read_WithNoInputs_ReturnsSilenceFullCount()
    {
        // Same ReadFully contract MasterMixer has: a bus that's idle must
        // still return `count` samples (silence) so the parent combiner
        // never sees a short read.
        var bus = new BusMixer(1, F);
        var buf = new float[64];

        var read = bus.Read(buf, 0, 64);

        read.Should().Be(64);
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
    }

    [Fact]
    public void Read_PassesSourceThroughWhenChainIsEmpty()
    {
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        // Sequence: 1, 2, 3, 4 — no FX applied.
        buf.Should().Equal(1f, 2f, 3f, 4f);
    }

    [Fact]
    public void AddEffect_AppendsToChain_InOrder()
    {
        var bus = new BusMixer(1, F);
        var a = new RecordingSamplerInstance(2f);
        var b = new RecordingSamplerInstance(3f);

        bus.AddEffect(a);
        bus.AddEffect(b);

        bus.Effects.Should().Equal(a, b);
    }

    [Fact]
    public void AddEffect_IsIdempotent()
    {
        var bus = new BusMixer(1, F);
        var a = new RecordingSamplerInstance(2f);

        bus.AddEffect(a);
        bus.AddEffect(a);

        bus.Effects.Should().Equal(a);
    }

    [Fact]
    public void RemoveEffect_DropsFromChain()
    {
        var bus = new BusMixer(1, F);
        var a = new RecordingSamplerInstance(2f);
        var b = new RecordingSamplerInstance(3f);
        bus.AddEffect(a);
        bus.AddEffect(b);

        bus.RemoveEffect(a);

        bus.Effects.Should().Equal(b);
    }

    [Fact]
    public void Read_AppliesEffectsToSourceInChainOrder()
    {
        // Verifies bus FX run BEFORE the bus's output crosses into the
        // master combiner — the entire point of bus FX is that they
        // affect ONE bus, not every other one mixed in.
        var bus = new BusMixer(1, F);
        bus.AddEffect(new RecordingSamplerInstance(2f));
        bus.AddMixerInput(new CountingSampleProvider());

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        // 1, 2, 3, 4 each doubled.
        buf.Should().Equal(2f, 4f, 6f, 8f);
    }

    [Fact]
    public void Effects_ReturnsDefensiveCopy()
    {
        // Same snapshot contract MasterMixer.GlobalEffects pins — without
        // it, enumerating Effects while another thread mutates the chain
        // throws InvalidOperationException.
        var bus = new BusMixer(1, F);
        var inst = new RecordingSamplerInstance(1f);
        bus.AddEffect(inst);

        var snap = bus.Effects;
        bus.RemoveEffect(inst);

        snap.Should().HaveCount(1);
        bus.Effects.Should().BeEmpty();
    }
}
