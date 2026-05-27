using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Unit tests for <see cref="TeeSampleProvider"/> — the source fan-out
/// that makes state-warm bypass possible. Pins the lockstep contract,
/// the single-pull-per-position guarantee, and the ring-wrap behavior.
/// </summary>
public class TeeSampleProviderTests
{
    [Fact]
    public void DryAndWetTaps_ReturnIdenticalSampleStreams()
    {
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src);

        var dryBuf = new float[8];
        var wetBuf = new float[8];

        tee.DryTap.Read(dryBuf, 0, 8);
        tee.WetTap.Read(wetBuf, 0, 8);

        dryBuf.Should().Equal(wetBuf);
        dryBuf.Should().Equal(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f);
    }

    [Fact]
    public void Upstream_IsPulledOncePerSamplePosition()
    {
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src);

        var dryBuf = new float[16];
        var wetBuf = new float[16];

        tee.DryTap.Read(dryBuf, 0, 16);
        tee.WetTap.Read(wetBuf, 0, 16);

        // The whole point of the tee: both taps got 16 samples each, but
        // upstream was only advanced 16 — not 32.
        src.SamplesRead.Should().Be(16);
        tee.DryPosition.Should().Be(16);
        tee.WetPosition.Should().Be(16);
        tee.UpstreamPosition.Should().Be(16);
    }

    [Fact]
    public void WetCanLagDry_UpToRingSize()
    {
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src, ringSize: 64); // pow2 → 64

        var dryBuf = new float[64];

        // Drain dry without touching wet: should fill the ring exactly.
        tee.DryTap.Read(dryBuf, 0, 64);
        dryBuf.Last().Should().Be(64f);
        tee.UpstreamPosition.Should().Be(64);
        tee.WetPosition.Should().Be(0);

        // Now drain wet. It should get the same samples from the ring,
        // without upstream being pulled again.
        var wetBuf = new float[64];
        int read = tee.WetTap.Read(wetBuf, 0, 64);
        read.Should().Be(64);
        wetBuf.Should().Equal(dryBuf);
        src.SamplesRead.Should().Be(64, "wet should have read from the cached ring, not from upstream");
    }

    [Fact]
    public void WetLagBeyondRingSize_ProducesShortRead()
    {
        // Contract: if a caller violates lockstep, the leading tap stops
        // delivering once it'd overrun the lagging tap's unread samples.
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src, ringSize: 64);

        var dryBuf = new float[128];

        int read = tee.DryTap.Read(dryBuf, 0, 128);

        // Dry only got the first ring-size (64) samples; the rest would
        // overwrite samples wet hasn't read yet.
        read.Should().Be(64);
        tee.UpstreamPosition.Should().Be(64);
    }

    [Fact]
    public void RingWrap_PreservesSampleContinuity()
    {
        // Pull the ring twice over, alternating taps in lockstep, to
        // exercise the wrap-around copy path.
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src, ringSize: 16);

        var dryBuf = new float[10];
        var wetBuf = new float[10];

        for (int cycle = 0; cycle < 5; cycle++)
        {
            tee.DryTap.Read(dryBuf, 0, 10);
            tee.WetTap.Read(wetBuf, 0, 10);
            dryBuf.Should().Equal(wetBuf, "wrap cycle {0} drifted", cycle);

            for (int i = 0; i < 10; i++)
            {
                float expected = cycle * 10 + i + 1;
                dryBuf[i].Should().Be(expected, "sample {0} of cycle {1}", i, cycle);
            }
        }
    }

    [Fact]
    public void RingSize_RoundedUpToPowerOfTwo()
    {
        // 100 → 128, 4096 → 4096.
        new TeeSampleProvider(new CountingSampleProvider(), ringSize: 100).RingSize.Should().Be(128);
        new TeeSampleProvider(new CountingSampleProvider(), ringSize: 4096).RingSize.Should().Be(4096);
        new TeeSampleProvider(new CountingSampleProvider(), ringSize: 1).RingSize.Should().Be(1);
    }

    [Fact]
    public void WaveFormat_MatchesUpstream()
    {
        var src = new CountingSampleProvider();
        var tee = new TeeSampleProvider(src);

        tee.DryTap.WaveFormat.Should().BeSameAs(src.WaveFormat);
        tee.WetTap.WaveFormat.Should().BeSameAs(src.WaveFormat);
    }
}
