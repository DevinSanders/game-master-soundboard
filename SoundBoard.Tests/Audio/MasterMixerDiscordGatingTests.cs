using SoundBoard.Core.Audio;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Broadcast subscription contract on <see cref="MasterMixer"/>: each
/// subscriber gets its own bounded queue + its own volume, so two bridges
/// can run simultaneously without race-dequeueing the same chunks.
///
/// <para>Pre-Phase-1 there was a single shared queue and these tests were
/// named "Discord*". Phase 1 generalised the name; the subsequent
/// per-bridge fan-out work made the queue/volume per-subscription. The
/// file name stays "Discord" for git-history continuity — rename freely
/// when next touching it.</para>
/// </summary>
public class MasterMixerDiscordGatingTests
{
    [Fact]
    public void NoSubscribers_NoChunksQueuedAnywhere()
    {
        using var mixer = new MasterMixer();
        mixer.Subscribers.Should().BeEmpty();
        var buf = new float[256];

        // Run the audio thread a bunch of times with no subscribers. The
        // fast-path skip means no per-subscription allocation happens at
        // all; we just verify no externally-visible state changed.
        for (int i = 0; i < 100; i++) mixer.Read(buf, 0, 256);

        mixer.Subscribers.Should().BeEmpty();
    }

    [Fact]
    public void Subscriber_ReceivesEveryChunk_UntilQueueCap()
    {
        using var mixer = new MasterMixer();
        var sub = mixer.Subscribe("test");
        var buf = new float[256];

        // Fill past the per-subscription cap.
        for (int i = 0; i < MasterMixer.BroadcastQueueCap + 10; i++)
            mixer.Read(buf, 0, 256);

        int drained = 0;
        while (sub.TryDequeue(out _)) drained++;

        drained.Should().Be(MasterMixer.BroadcastQueueCap,
            "the per-subscription queue caps at BroadcastQueueCap; overflow gets dropped");
        sub.DropCount.Should().Be(10, "10 reads exceeded the cap; each should be counted as a drop");
    }

    [Fact]
    public void TwoSubscribers_BothReceiveEveryChunk()
    {
        // The critical guarantee of the per-subscription design: two
        // bridges connected simultaneously each see the FULL audio
        // stream, not 50% each.
        using var mixer = new MasterMixer();
        var subA = mixer.Subscribe("A");
        var subB = mixer.Subscribe("B");
        var buf = new float[128];

        const int cycles = 10;
        for (int i = 0; i < cycles; i++) mixer.Read(buf, 0, 128);

        int drainedA = 0, drainedB = 0;
        while (subA.TryDequeue(out _)) drainedA++;
        while (subB.TryDequeue(out _)) drainedB++;

        drainedA.Should().Be(cycles, "subscriber A should see every chunk");
        drainedB.Should().Be(cycles, "subscriber B should see every chunk independently of A");
    }

    [Fact]
    public void SubscribeUnsubscribe_TogglesGating()
    {
        using var mixer = new MasterMixer();
        var buf = new float[128];

        // No subscribers yet.
        for (int i = 0; i < 5; i++) mixer.Read(buf, 0, 128);
        mixer.Subscribers.Should().BeEmpty();

        // Subscribe: chunks now accumulate.
        var sub = mixer.Subscribe("test");
        for (int i = 0; i < 5; i++) mixer.Read(buf, 0, 128);

        int chunksAfterOn = 0;
        while (sub.TryDequeue(out _)) chunksAfterOn++;
        chunksAfterOn.Should().Be(5);

        // Unsubscribe (Dispose): no more chunks queued.
        sub.Dispose();
        mixer.Subscribers.Should().BeEmpty();
        for (int i = 0; i < 5; i++) mixer.Read(buf, 0, 128);
        sub.TryDequeue(out _).Should().BeFalse("disposed subscription returns false from TryDequeue");
    }

    [Fact]
    public void ResetDropCount_ClearsCounter()
    {
        using var mixer = new MasterMixer();
        var sub = mixer.Subscribe("test");
        var buf = new float[128];
        for (int i = 0; i < MasterMixer.BroadcastQueueCap + 3; i++) mixer.Read(buf, 0, 128);

        sub.DropCount.Should().Be(3);
        sub.ResetDropCount();
        sub.DropCount.Should().Be(0);
    }

    [Fact]
    public void Volume_AppliedAtEnqueueTime()
    {
        // The subscription's Volume is multiplied into each chunk at
        // enqueue, so two subscriptions with different volumes get
        // different scaled samples even though the underlying mix is
        // identical.
        using var mixer = new MasterMixer();
        var unityChan = mixer.Subscribe("unity");
        var halfChan = mixer.Subscribe("half");
        halfChan.Volume = 0.5f;

        // Add a 1.0 DC source so we get a deterministic non-zero output.
        mixer.AddMixerInput(new ConstantSampleProvider(1.0f, mixer.WaveFormat));
        var buf = new float[128];
        mixer.Read(buf, 0, 128);

        unityChan.TryDequeue(out var unityChunk).Should().BeTrue();
        halfChan.TryDequeue(out var halfChunk).Should().BeTrue();

        unityChunk!.Length.Should().BeGreaterThan(0);
        halfChunk!.Length.Should().Be(unityChunk.Length);

        // Unity should see ~1.0, half should see ~0.5 — modulo floating
        // point noise. We compare a single sample because the source is
        // a constant.
        unityChunk[0].Should().BeApproximately(1.0f, 0.01f);
        halfChunk[0].Should().BeApproximately(0.5f, 0.01f);
    }
}

/// <summary>Trivial ISampleProvider that emits a constant DC value
/// indefinitely. Used by the volume-application test to give the mixer
/// something deterministic to read.</summary>
internal sealed class ConstantSampleProvider : NAudio.Wave.ISampleProvider
{
    private readonly float _value;
    public NAudio.Wave.WaveFormat WaveFormat { get; }
    public ConstantSampleProvider(float value, NAudio.Wave.WaveFormat fmt)
    {
        _value = value;
        WaveFormat = fmt;
    }
    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++) buffer[offset + i] = _value;
        return count;
    }
}
