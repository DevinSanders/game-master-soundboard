using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Smoke + contract tests for <see cref="MasterMixer"/>. Two of the
/// Phase 1 audio-thread issues (allocation pressure in <c>Read</c> and
/// non-thread-safe <c>_globalEffects</c> list) can't be asserted directly
/// without micro-benchmarking or stress harnesses, so this file focuses
/// on the observable contract — what the chain emits, how
/// <c>AddGlobalEffect</c> / <c>RemoveGlobalEffect</c> mutate the chain,
/// and that <c>ReadFully</c> keeps the output device fed when no inputs
/// are present.
/// </summary>
public class MasterMixerTests
{
    [Fact]
    public void Defaults_AreIeeeFloat48KhzStereo()
    {
        var mixer = new MasterMixer();

        mixer.WaveFormat.SampleRate.Should().Be(48000);
        mixer.WaveFormat.Channels.Should().Be(2);
        mixer.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
    }

    [Fact]
    public void Read_WithNoInputs_ReturnsSilenceButHonorsReadFully()
    {
        // ReadFully = true means the mixer must always return `count`
        // samples (silence when idle). Critical contract — the output
        // device starves if Read ever returns less than requested.
        var mixer = new MasterMixer();
        var buf = new float[128];

        var read = mixer.Read(buf, 0, 128);

        read.Should().Be(128);
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
    }

    [Fact]
    public void AddGlobalEffect_AppendsToChain_InOrder()
    {
        var mixer = new MasterMixer();
        var a = new RecordingSamplerInstance(2f);
        var b = new RecordingSamplerInstance(3f);

        mixer.AddGlobalEffect(a);
        mixer.AddGlobalEffect(b);

        mixer.GlobalEffects.Should().Equal(a, b);
    }

    [Fact]
    public void AddGlobalEffect_IsIdempotent()
    {
        var mixer = new MasterMixer();
        var a = new RecordingSamplerInstance(2f);

        mixer.AddGlobalEffect(a);
        mixer.AddGlobalEffect(a);

        mixer.GlobalEffects.Should().Equal(a);
    }

    [Fact]
    public void RemoveGlobalEffect_DropsFromChain()
    {
        var mixer = new MasterMixer();
        var a = new RecordingSamplerInstance(2f);
        var b = new RecordingSamplerInstance(3f);
        mixer.AddGlobalEffect(a);
        mixer.AddGlobalEffect(b);

        mixer.RemoveGlobalEffect(a);

        mixer.GlobalEffects.Should().Equal(b);
    }

    [Fact]
    public void RemoveGlobalEffect_UnknownInstance_IsNoop()
    {
        var mixer = new MasterMixer();
        var a = new RecordingSamplerInstance(2f);
        mixer.AddGlobalEffect(a);

        mixer.RemoveGlobalEffect(new RecordingSamplerInstance(99f));

        mixer.GlobalEffects.Should().Equal(a);
    }

    [Fact]
    public void Read_AppliesGlobalEffectsToMixerOutput()
    {
        var mixer = new MasterMixer();
        var gain = new RecordingSamplerInstance(2f);
        mixer.AddGlobalEffect(gain);
        mixer.AddMixerInput(new CountingSampleProvider());

        var buf = new float[4];
        mixer.Read(buf, 0, 4);

        // Mixer output is dry source × 2 (post-effect) × LocalVolume=1.
        // CountingSampleProvider emits 1, 2, 3, 4.
        buf.Should().Equal(2f, 4f, 6f, 8f);
        gain.WetReadCount.Should().BeGreaterThan(0);
    }
}
