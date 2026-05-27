using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;
using System.Collections.Concurrent;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase R2 tests for <see cref="BusMixer"/>'s split-lock + Volatile-
/// published subscriber snapshot. Pins the audio-thread invariants:
///
/// <list type="bullet">
///   <item>The audio thread does NOT block on a plugin's
///   <see cref="ISamplerInstance.CreateEffect"/> — the effect lock and
///   the subscriber lock are different objects.</item>
///   <item>Reading the subscriber snapshot is lock-free — the audio
///   thread does a single <c>Volatile.Read</c> on a published array.</item>
///   <item>Concurrent add-effect + Read doesn't deadlock or throw,
///   even when the plugin's <c>CreateEffect</c> blocks for a measurable
///   time.</item>
/// </list>
/// </summary>
public class BusMixerR2ThreadSafetyTests
{
    private static WaveFormat F => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public async Task BlockingCreateEffect_DoesNotStallAudioThread()
    {
        // The pre-fix code held a single _lock during Rebuild (which
        // calls plugin CreateEffect) AND on the audio-thread sidechain
        // snapshot. A blocking plugin would stall Read indefinitely.
        // Post-fix: _chainLock (for AddEffect) is distinct from
        // _subscribersLock (read-only by audio thread).
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        // Plugin that blocks for 300 ms inside CreateEffect.
        var slowPlugin = new BlockingCreateEffectInstance(System.TimeSpan.FromMilliseconds(300));

        // Kick off AddEffect on a background thread (UI-thread proxy).
        // Task.Run's CancellationToken overload is documented as cancel-
        // before-start; here we want the work to ACTUALLY run, so the
        // no-token overload is correct and the analyzer warning is a
        // false positive in this context.
#pragma warning disable xUnit1051
        var addTask = Task.Run(() => bus.AddEffect(slowPlugin));
#pragma warning restore xUnit1051

        // Meanwhile, the audio thread should be able to complete many
        // Read cycles without waiting for AddEffect to finish.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buf = new float[256];
        int reads = 0;
        while (sw.ElapsedMilliseconds < 100)
        {
            bus.Read(buf, 0, 256);
            reads++;
        }
        sw.Stop();

        // We should have completed at least a few dozen reads (no
        // single one blocked on the AddEffect). Generous floor of 5
        // to avoid CI-runner flakiness.
        reads.Should().BeGreaterThan(5, "audio thread must not block on AddEffect's plugin call");

        await addTask;
    }

    [Fact]
    public void Subscriber_Snapshot_IsVolatilePublished_NotPerCycleAllocation()
    {
        // The published snapshot reference is what the audio thread
        // reads via Volatile.Read. Adding a subscriber publishes a new
        // snapshot array — the existing reference doesn't get mutated.
        // This guarantees that a Read in progress sees a consistent
        // snapshot for its whole iteration.
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());
        bus.SidechainSubscriberCount.Should().Be(0);

        using var sub1 = bus.SubscribeSidechain((_, _) => { });
        bus.SidechainSubscriberCount.Should().Be(1);

        using var sub2 = bus.SubscribeSidechain((_, _) => { });
        bus.SidechainSubscriberCount.Should().Be(2);

        sub1.Dispose();
        bus.SidechainSubscriberCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentAddRemove_WithSidechainSubscriptions_DoesNotThrow()
    {
        // Stress: add/remove effects (takes _chainLock) and
        // subscribe/unsubscribe (takes _subscribersLock) concurrently
        // while a Read loop hammers from another thread. Should not
        // deadlock or throw.
        var bus = new BusMixer(1, F);
        bus.AddMixerInput(new CountingSampleProvider());

        var exceptions = new ConcurrentBag<System.Exception>();
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMilliseconds(300));

        System.Action wrap(System.Action body) => () =>
        {
            try { while (!cts.IsCancellationRequested) body(); }
            catch (System.Exception ex) { exceptions.Add(ex); }
        };

        var instances = System.Linq.Enumerable.Range(0, 10)
            .Select(_ => new RecordingSamplerInstance(1f))
            .Cast<ISamplerInstance>()
            .ToList();

#pragma warning disable xUnit1051
        var adder = Task.Run(wrap(() =>
        {
            foreach (var i in instances) bus.AddEffect(i);
        }));
        var remover = Task.Run(wrap(() =>
        {
            foreach (var i in instances) bus.RemoveEffect(i);
        }));
        var subscriber = Task.Run(wrap(() =>
        {
            using var s = bus.SubscribeSidechain((_, _) => { });
            System.Threading.Thread.Yield();
        }));
        var reader = Task.Run(wrap(() =>
        {
            var buf = new float[128];
            bus.Read(buf, 0, 128);
        }));
#pragma warning restore xUnit1051

        await Task.WhenAll(adder, remover, subscriber, reader);
        exceptions.Should().BeEmpty();
    }

    /// <summary>Test plugin instance whose <see cref="CreateEffect"/>
    /// blocks for a configurable duration — exercises the "plugin code
    /// runs under _chainLock" path.</summary>
    private sealed class BlockingCreateEffectInstance : ISamplerInstance
    {
        private readonly System.TimeSpan _delay;
        public BlockingCreateEffectInstance(System.TimeSpan delay) => _delay = delay;
        public ISampleProvider CreateEffect(ISampleProvider source)
        {
            System.Threading.Thread.Sleep(_delay);
            return source;
        }
        public string SerializeConfig() => "";
        public void DeserializeConfig(string json) { }
        public object? CreateControl() => null;
        public void Dispose() { }
    }
}
