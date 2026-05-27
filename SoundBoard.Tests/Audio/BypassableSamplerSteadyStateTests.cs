using SoundBoard.Core.Services;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Regression tests for the user-reported "attenuator at 100% sounds jittery"
/// issue. The not-bypassed audio path must:
/// <list type="bullet">
/// <item>Be sample-accurate at audio-realistic buffer sizes (960+ samples,
///   not just the 8-sample smoke tests).</item>
/// <item>Allocate zero bytes per buffer after warmup (no ArrayPool churn,
///   no per-cycle scratch).</item>
/// <item>Produce continuous samples across many cycles without
///   accumulating drift in the tee's ring buffer math.</item>
/// </list>
///
/// <para>These pin the fix that replaces the per-cycle dry-drain
/// Array.Copy + ArrayPool.Rent with <see cref="Core.Audio.TeeSampleProvider.AdvanceDryTap"/>.</para>
/// </summary>
public class BypassableSamplerSteadyStateTests
{
    [Fact]
    public void Unity_NotBypassed_ProducesIdenticalSamplesToBareSource()
    {
        // At gain 1.0 (effective no-op), the wrapped chain MUST be
        // sample-equivalent to the bare source. Any drift indicates a
        // ring-buffer math bug or off-by-one in the tap positions.
        var instance = new RecordingSamplerInstance(gain: 1.0f);
        var wrapper = new BypassableSamplerInstance(instance, initialBypassed: false);
        var dry = new CountingSampleProvider();
        var chain = wrapper.CreateEffect(dry);

        // Audio-realistic buffer size: 960 samples is 10 ms stereo at 48 kHz.
        const int bufferSize = 960;
        const int cycles = 200; // ~2 seconds of audio
        var buf = new float[bufferSize];

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            chain.Read(buf, 0, bufferSize);
            for (int i = 0; i < bufferSize; i++)
            {
                float expected = cycle * bufferSize + i + 1;
                if (buf[i] != expected)
                    throw new Xunit.Sdk.XunitException(
                        $"Sample drift at cycle {cycle}, index {i}: expected {expected}, got {buf[i]}");
            }
        }
    }

    [Fact]
    public void Unity_NotBypassed_ZeroAllocationsPerCycle()
    {
        // The user reported jitter even at 100% gain. Pre-fix, every audio
        // cycle did ArrayPool.Rent(count) + Array.Copy(count) for the
        // dry drain. Post-fix, AdvanceDryTap is a single position bump.
        // Steady-state allocation must be zero (modulo what the test
        // harness's own machinery allocates).
        var instance = new RecordingSamplerInstance(gain: 1.0f);
        var wrapper = new BypassableSamplerInstance(instance, initialBypassed: false);
        var dry = new CountingSampleProvider();
        var chain = wrapper.CreateEffect(dry);

        const int bufferSize = 960;
        var buf = new float[bufferSize];

        // Warmup: JIT, first-pull ring fill, possible internal lazy inits.
        for (int i = 0; i < 50; i++) chain.Read(buf, 0, bufferSize);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) chain.Read(buf, 0, bufferSize);
        long after = GC.GetAllocatedBytesForCurrentThread();

        long perCycle = (after - before) / 1000;
        // Tight bound — the audio thread should be making zero allocations
        // here. Anything more than a few bytes per cycle indicates a
        // regression in the fast path.
        perCycle.Should().BeLessThan(64,
            $"audio thread allocation per cycle was {perCycle} bytes — non-bypassed wet path must not Rent/Copy");
    }

    [Fact]
    public void Bypassed_DoesNotPullWet()
    {
        // Simple-bypass design (v1): while bypassed only the dry path is
        // pulled. Wet stays at its pre-bypass position. The state-freeze
        // discontinuity on un-bypass is inaudible for stateless plugins
        // and accepted for stateful ones — the tee architecture that
        // kept wet warm caused audible jitter on the audio thread.
        var instance = new RecordingSamplerInstance(gain: 2.0f);
        var wrapper = new BypassableSamplerInstance(instance, initialBypassed: true);
        var dry = new CountingSampleProvider();
        var chain = wrapper.CreateEffect(dry);

        var buf = new float[16];
        chain.Read(buf, 0, 16);
        chain.Read(buf, 0, 16);
        chain.Read(buf, 0, 16);

        instance.WetReadCount.Should().Be(0,
            "v1 simple-bypass design: wet is not clocked while bypassed");
        // Source still advances (dry reads it).
        dry.SamplesRead.Should().Be(48);
    }

    [Fact]
    public void TogglingBypass_StaysSampleAccurate_AcrossManyCycles()
    {
        // Realistic load: 200 cycles of 960-sample buffers with bypass
        // toggling every 10 cycles. Source is single-consumer (only one
        // of wet/dry reads per cycle) so samples must stay contiguous
        // regardless of how many toggles happen.
        var instance = new RecordingSamplerInstance(gain: 1.0f);
        var wrapper = new BypassableSamplerInstance(instance, initialBypassed: false);
        var dry = new CountingSampleProvider();
        var chain = wrapper.CreateEffect(dry);

        const int bufferSize = 960;
        var buf = new float[bufferSize];
        int sampleCounter = 0;

        for (int cycle = 0; cycle < 200; cycle++)
        {
            if (cycle > 0 && cycle % 10 == 0)
                wrapper.IsBypassed = !wrapper.IsBypassed;

            chain.Read(buf, 0, bufferSize);

            for (int i = 0; i < bufferSize; i++)
            {
                float expected = ++sampleCounter; // 1-based ramp
                // Gain=1 → both branches are identity passthrough.
                if (buf[i] != expected)
                    throw new Xunit.Sdk.XunitException(
                        $"Sample drift at cycle {cycle} ({(wrapper.IsBypassed ? "BYPASSED" : "WET")}), index {i}: " +
                        $"expected {expected}, got {buf[i]}");
            }
        }
    }
}
