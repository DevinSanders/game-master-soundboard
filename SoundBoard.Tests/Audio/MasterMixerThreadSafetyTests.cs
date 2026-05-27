using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Stress tests for Phase 1 #7: <see cref="MasterMixer"/>'s mutation
/// surface (<c>AddGlobalEffect</c>, <c>RemoveGlobalEffect</c>, the
/// implicit <c>RebuildProcessingChain</c>) must not throw when called
/// concurrently with <c>Read</c> on the audio thread. The tests can't
/// prove memory ordering correctness on weakly-ordered CPUs, but they
/// reliably reproduce <see cref="InvalidOperationException"/> from
/// mutating-collection-during-iteration if the locking is removed.
/// </summary>
public class MasterMixerThreadSafetyTests
{
    [Fact]
    public async Task ConcurrentAddRemove_WithReader_DoesNotThrow()
    {
        using var mixer = new MasterMixer();
        var instances = Enumerable.Range(0, 50)
            .Select(_ => new RecordingSamplerInstance(1f))
            .Cast<SoundBoard.PluginApi.ISamplerInstance>()
            .ToList();

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        Action wrap(Action body) => () =>
        {
            try
            {
                while (!cts.IsCancellationRequested) body();
            }
            catch (Exception ex) { exceptions.Add(ex); }
        };

        // The wrap() closure already loops on `cts.IsCancellationRequested`,
        // so Task.Run's own CancellationToken arg would be redundant here
        // (and xUnit1051's suggestion to use TestContext.Current.CancellationToken
        // doesn't apply — our cts is a stress-test timer, not a test-cancel
        // signal).
#pragma warning disable xUnit1051
        var adder = Task.Run(wrap(() =>
        {
            foreach (var i in instances) mixer.AddGlobalEffect(i);
        }));
        var remover = Task.Run(wrap(() =>
        {
            foreach (var i in instances) mixer.RemoveGlobalEffect(i);
        }));
        var reader = Task.Run(wrap(() =>
        {
            var buf = new float[128];
            mixer.Read(buf, 0, 128);
        }));
        var snapshot = Task.Run(wrap(() =>
        {
            // Iterate the public GlobalEffects snapshot. Pre-fix this was
            // _globalEffects.AsReadOnly() — a live view that throws under
            // concurrent mutation. Post-fix it's a defensive ToArray.
            foreach (var _ in mixer.GlobalEffects) { }
        }));
#pragma warning restore xUnit1051

        await Task.WhenAll(adder, remover, reader, snapshot);

        exceptions.Should().BeEmpty();
    }

    [Fact]
    public void GlobalEffects_ReturnsDefensiveCopy_NotLiveView()
    {
        // If the getter returned _globalEffects.AsReadOnly(), iterating the
        // returned collection while Add/Remove runs would throw. The fix
        // is to return ToArray() (a snapshot). This test verifies the
        // snapshot contract: enumerating outside a lock is safe.
        using var mixer = new MasterMixer();
        var inst = new RecordingSamplerInstance(1f);
        mixer.AddGlobalEffect(inst);

        var snapshot = mixer.GlobalEffects;

        // Mutate while holding the snapshot — must not affect the snapshot.
        mixer.RemoveGlobalEffect(inst);

        snapshot.Should().HaveCount(1);
        snapshot[0].Should().BeSameAs(inst);
        mixer.GlobalEffects.Should().BeEmpty();
    }
}
