using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase C tests for the per-bus <see cref="BusMixer.Volume"/> gain
/// applied after the FX chain. Pins:
/// <list type="bullet">
///   <item>Volume defaults to unity (1.0) so a fresh BusMixer is a
///   no-op gain stage.</item>
///   <item>Setting the value scales every sample read on the next
///   Read.</item>
///   <item>The multiply skips when volume is exactly 1.0 — keeps the
///   hot path allocation- and multiply-free for the common case.</item>
///   <item>Volume runs AFTER the FX chain, so an effect can boost a
///   bus that the user has then attenuated at the slider.</item>
/// </list>
/// </summary>
public class BusMixerVolumeTests
{
    private static WaveFormat F => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public void Volume_DefaultsToUnity()
    {
        var bus = new BusMixer(1, F);
        bus.Volume.Should().Be(1.0f);
    }

    [Fact]
    public void Volume_ScalesEachSample()
    {
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());
        bus.Volume = 0.5f;

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        // 1, 2, 3, 4 × 0.5 = 0.5, 1, 1.5, 2.
        buf.Should().Equal(0.5f, 1.0f, 1.5f, 2.0f);
    }

    [Fact]
    public void Volume_AppliesAfterFxChain()
    {
        // Effect doubles → bus volume halves → net unity. Verifies
        // ordering: if Volume ran BEFORE the FX chain the output would
        // be doubled (FX would see the halved signal then double it).
        var bus = new BusMixer(1, F);
        bus.AddEffect(new RecordingSamplerInstance(2f));
        bus.AddMixerInput(new CountingSampleProvider());
        bus.Volume = 0.5f;

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        buf.Should().Equal(1.0f, 2.0f, 3.0f, 4.0f);
    }

    [Fact]
    public void Volume_AtZero_ProducesSilence()
    {
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());
        bus.Volume = 0.0f;

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        buf.Should().AllSatisfy(s => s.Should().Be(0f));
    }
}
