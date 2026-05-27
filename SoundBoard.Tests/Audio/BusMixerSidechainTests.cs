using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;
using System.Collections.Generic;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase D contract tests for <see cref="BusMixer"/>'s sidechain
/// push fan-out — the audio-thread tee that delivers post-FX samples
/// to subscribed plugins.
/// </summary>
public class BusMixerSidechainTests
{
    private static WaveFormat F => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public void NoSubscribers_ReadPathDoesNotAllocate()
    {
        // Indirect test — we can't easily assert "no allocations"
        // without BenchmarkDotNet, but we CAN assert the volatile
        // count gate is zero when nobody is listening. The Read fast
        // path keys off that. (Microbenchmarks live elsewhere.)
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        bus.SidechainSubscriberCount.Should().Be(0);

        var buf = new float[4];
        var act = () => bus.Read(buf, 0, 4);
        act.Should().NotThrow();
    }

    [Fact]
    public void Subscribe_InvokesCallbackPerBuffer_WithSourceSamples()
    {
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        var received = new List<float[]>();
        using var sub = bus.SubscribeSidechain((buffer, count) =>
        {
            // Copy because the contract is "don't retain the reference".
            // Test happens to do that for the assertion only.
            var snapshot = new float[count];
            System.Array.Copy(buffer, snapshot, count);
            received.Add(snapshot);
        });

        var dummy = new float[4];
        bus.Read(dummy, 0, 4);
        bus.Read(dummy, 0, 4);

        received.Should().HaveCount(2);
        // First buffer: 1, 2, 3, 4. Second: 5, 6, 7, 8.
        received[0].Should().Equal(1f, 2f, 3f, 4f);
        received[1].Should().Equal(5f, 6f, 7f, 8f);
    }

    [Fact]
    public void Subscribe_SeesPreVolumeSamples()
    {
        // The sidechain fan-out runs BEFORE the bus-volume multiply so
        // the trigger envelope reflects the FX-chain output, not the
        // user-attenuated signal. Pin that ordering — a refactor that
        // moved the multiply above the sidechain branch would silently
        // change ducker behaviour.
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());
        bus.Volume = 0.5f;

        float[]? captured = null;
        using var sub = bus.SubscribeSidechain((buffer, count) =>
        {
            captured = new float[count];
            System.Array.Copy(buffer, captured, count);
        });

        var buf = new float[4];
        bus.Read(buf, 0, 4);

        // Subscriber saw 1, 2, 3, 4 (pre-volume); local output got 0.5, 1.0, 1.5, 2.0.
        captured.Should().Equal(1f, 2f, 3f, 4f);
        buf.Should().Equal(0.5f, 1f, 1.5f, 2f);
    }

    [Fact]
    public void Subscribe_DisposingHandle_DetachesCallback()
    {
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        int invocations = 0;
        var sub = bus.SubscribeSidechain((_, _) => invocations++);
        sub.Dispose();

        var buf = new float[4];
        bus.Read(buf, 0, 4);
        bus.Read(buf, 0, 4);

        invocations.Should().Be(0);
        bus.SidechainSubscriberCount.Should().Be(0);
    }

    [Fact]
    public void Subscribe_MultipleSubscribers_EachSeesCorrectSamplesDuringCallback()
    {
        // Contract: at the moment each subscriber's callback is invoked,
        // it receives a fresh COPY of the post-FX samples — so subscriber
        // A mutating the buffer (which it MUST NOT do, but the host
        // defends against the contract violation anyway) cannot corrupt
        // what subscriber B sees on the same Read cycle.
        //
        // Note on retention: pre-pool the buffers were freshly allocated
        // per subscriber, so callbacks that stashed the reference past
        // the call coincidentally got distinct arrays. The pooled version
        // (Phase R2) may recycle arrays between subscribers in the same
        // Read cycle — the documented "do not retain the reference"
        // contract is now strict. This test honours the contract by
        // snapshotting inside the callback.
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        float[]? a = null;
        float[]? b = null;
        using var subA = bus.SubscribeSidechain((buffer, count) =>
        {
            a = new float[count];
            System.Array.Copy(buffer, a, count);
        });
        using var subB = bus.SubscribeSidechain((buffer, count) =>
        {
            b = new float[count];
            System.Array.Copy(buffer, b, count);
        });

        var dummy = new float[4];
        bus.Read(dummy, 0, 4);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        // Both saw the same samples (1, 2, 3, 4) at invocation time.
        // The host's per-callback fresh-buffer guarantee means that
        // even if subA had mutated its buffer, subB's would not have
        // been touched.
        a.Should().Equal(1f, 2f, 3f, 4f);
        b.Should().Equal(1f, 2f, 3f, 4f);
    }

    [Fact]
    public void Subscribe_ThrowingCallback_DoesNotBreakOtherSubscribers()
    {
        // A faulty sidechain plugin should not stall the audio thread
        // or stop other subscribers from receiving their samples.
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        using var bad = bus.SubscribeSidechain((_, _) =>
            throw new System.InvalidOperationException("intentional"));
        int goodInvocations = 0;
        using var good = bus.SubscribeSidechain((_, _) => goodInvocations++);

        var buf = new float[4];
        var act = () => bus.Read(buf, 0, 4);

        act.Should().NotThrow();
        goodInvocations.Should().Be(1, "the throwing subscriber must not break the fan-out");
    }
}
