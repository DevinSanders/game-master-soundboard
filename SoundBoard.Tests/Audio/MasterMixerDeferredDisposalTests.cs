using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Pins the Phase 1 #6 contract: <see cref="MasterMixer.DeferDispose"/>
/// must NOT dispose synchronously (the audio thread may still hold the
/// reference) and must eventually dispose after one Read cycle has
/// elapsed since the enqueue.
///
/// <para>The drainer runs on a background <see cref="Task"/>, so tests
/// use <see cref="MasterMixer.FlushPendingDisposalsForTest"/> to force
/// synchronous draining without driving real audio cycles.</para>
/// </summary>
public class MasterMixerDeferredDisposalTests
{
    private sealed class DisposalProbe : IDisposable
    {
        public int DisposeCallCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCallCount);
    }

    [Fact]
    public void DeferDispose_DoesNotDisposeImmediately()
    {
        using var mixer = new MasterMixer();
        var probe = new DisposalProbe();

        mixer.DeferDispose(probe);

        probe.DisposeCallCount.Should().Be(0);
        mixer.PendingDisposalCount.Should().Be(1);
    }

    [Fact]
    public void DeferDispose_DisposesAfterAtLeastOneReadCycle()
    {
        using var mixer = new MasterMixer();
        var probe = new DisposalProbe();
        var buf = new float[256];

        // Read cycle 1 — pre-enqueue baseline.
        mixer.Read(buf, 0, 256);

        // Enqueue during cycle ≈1.
        mixer.DeferDispose(probe);

        // Cycle 2: still in the cycle that captured the enqueue; item
        // should NOT yet be eligible.
        mixer.Read(buf, 0, 256);
        probe.DisposeCallCount.Should().Be(0,
            "items must survive at least one full Read cycle so the audio thread can move past them");

        // Cycle 3: now eligible. Drainer runs asynchronously; give it a
        // moment.
        mixer.Read(buf, 0, 256);
        SpinWait.SpinUntil(() => probe.DisposeCallCount > 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("disposal must happen on the drainer thread after the second Read cycle");
        probe.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public void DeferDispose_DisposesEachItemExactlyOnce()
    {
        using var mixer = new MasterMixer();
        var probes = Enumerable.Range(0, 5).Select(_ => new DisposalProbe()).ToList();
        var buf = new float[128];

        foreach (var p in probes) mixer.DeferDispose(p);

        // Two reads drains everything to the background channel.
        mixer.Read(buf, 0, 128);
        mixer.Read(buf, 0, 128);
        mixer.Read(buf, 0, 128);

        SpinWait.SpinUntil(() => probes.All(p => p.DisposeCallCount == 1), TimeSpan.FromSeconds(2))
            .Should().BeTrue();
        probes.Should().AllSatisfy(p => p.DisposeCallCount.Should().Be(1));
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public int DisposeCalls;
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            throw new InvalidOperationException("plugin bug");
        }
    }

    [Fact]
    public void DeferDispose_SwallowsExceptionsFromDisposable()
    {
        using var mixer = new MasterMixer();
        var thrower = new ThrowingDisposable();

        mixer.DeferDispose(thrower);

        // Drain pushes the item to the background drainer; the drainer
        // must catch and swallow rather than propagate to the Task.
        mixer.FlushPendingDisposalsForTest();

        SpinWait.SpinUntil(() => thrower.DisposeCalls > 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the drainer must reach and invoke the disposable");

        // The drainer's Task should still be alive — i.e., not faulted by
        // the exception. We can't directly observe that, but enqueueing a
        // second item and seeing it disposed proves the drainer survived.
        var probe = new DisposalProbe();
        mixer.DeferDispose(probe);
        mixer.FlushPendingDisposalsForTest();
        SpinWait.SpinUntil(() => probe.DisposeCallCount > 0, TimeSpan.FromSeconds(2))
            .Should().BeTrue("the drainer must keep running after a throwing dispose");
    }

    [Fact]
    public void RemoveGlobalEffect_FollowedByDispose_DoesNotRaceAudioThread()
    {
        // Integration smoke: simulate the SamplerChainService usage pattern.
        // Audio thread calling Read concurrently with a remove+defer.
        using var mixer = new MasterMixer();
        var probe = new DisposalProbe();
        var fakeEffect = new TrackingSamplerInstance(probe);
        mixer.AddGlobalEffect(fakeEffect);

        var buf = new float[128];
        for (int i = 0; i < 5; i++) mixer.Read(buf, 0, 128);

        // Simulate the UI-thread side of SamplerChainService.RemoveAttachment.
        mixer.RemoveGlobalEffect(fakeEffect);
        mixer.DeferDispose(fakeEffect);

        // The instance must NOT be disposed at this point — audio thread
        // could still be inside Read on the pre-rebuild chain.
        probe.DisposeCallCount.Should().Be(0);

        // Push two more Read cycles; the disposal becomes eligible.
        mixer.Read(buf, 0, 128);
        mixer.Read(buf, 0, 128);
        mixer.Read(buf, 0, 128);

        SpinWait.SpinUntil(() => probe.DisposeCallCount == 1, TimeSpan.FromSeconds(2))
            .Should().BeTrue();
    }

    /// <summary>Audio-thread-allocation contract. Pool the temp buffer in
    /// <c>MasterMixer.Read</c>: a steady run of identical-sized Reads
    /// should average well under the size of one temp buffer in
    /// per-Read allocations. The Discord and visualizer chunks are
    /// still allocated per Read (Phase 5 nice-to-have to pool them too),
    /// so the threshold accommodates two <c>float[count]</c> allocations
    /// plus minor channel/struct overhead.</summary>
    [Fact]
    public void Read_AllocationsPerCycle_AreBoundedAndDoNotPoolTempBuffer()
    {
        using var mixer = new MasterMixer();
        const int count = 480; // 10 ms of stereo at 48 kHz
        var buf = new float[count];

        // Warm-up: JIT, pool prime, first-call paths.
        for (int i = 0; i < 50; i++) mixer.Read(buf, 0, count);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) mixer.Read(buf, 0, count);
        long after = GC.GetAllocatedBytesForCurrentThread();

        long perRead = (after - before) / 200;
        // 1 × pooled temp (≈ 0 bytes amortized) + 1 × discord chunk
        // (1920 bytes for 480 floats) + 1 × visualizer chunk if a
        // subscriber exists (we don't subscribe in this test, so 0).
        // Threshold: well under the pre-fix 3× allocation.
        perRead.Should().BeLessThan(count * sizeof(float) * 2,
            "the pooled temp buffer should no longer contribute per-Read allocation");
    }
}

/// <summary>Test double that simulates an <see cref="SoundBoard.PluginApi.ISamplerInstance"/>
/// for the remove-then-defer integration test. Wraps a
/// <c>DisposalProbe</c> so the test can assert dispose ordering.</summary>
internal sealed class TrackingSamplerInstance : SoundBoard.PluginApi.ISamplerInstance
{
    private readonly IDisposable _onDispose;
    public TrackingSamplerInstance(IDisposable onDispose) { _onDispose = onDispose; }
    public string SerializeConfig() => "";
    public void DeserializeConfig(string json) { }
    public NAudio.Wave.ISampleProvider CreateEffect(NAudio.Wave.ISampleProvider source) => source;
    public object? CreateControl() => null;
    public void Dispose() => _onDispose.Dispose();
}
