using NAudio.Wave;
using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Phase 1 #5: pin <see cref="SafeSampleProvider"/>'s exception isolation
/// contract. A plugin that throws from <c>Read</c> must NOT kill the
/// audio thread; the wrapper converts throws to silence, logs once,
/// and after enough consecutive throws enters permanent bypass.
/// </summary>
public class SafeSampleProviderTests
{
    private sealed class ThrowingProvider : ISampleProvider
    {
        private readonly int _throwUntilCall;
        private int _calls;

        public ThrowingProvider(int throwUntilCall = int.MaxValue)
        {
            _throwUntilCall = throwUntilCall;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Calls => _calls;

        public int Read(float[] buffer, int offset, int count)
        {
            _calls++;
            if (_calls <= _throwUntilCall)
                throw new InvalidOperationException($"intentional throw on call {_calls}");
            for (int i = 0; i < count; i++) buffer[offset + i] = 99f;
            return count;
        }
    }

    [Fact]
    public void Read_PassesThrough_WhenInnerSucceeds()
    {
        var inner = new CountingSampleProvider();
        var safe = new SafeSampleProvider(inner);

        var buf = new float[4];
        int read = safe.Read(buf, 0, 4);

        read.Should().Be(4);
        buf.Should().Equal(1f, 2f, 3f, 4f);
        safe.TotalFailures.Should().Be(0);
        safe.IsPermanentlyBypassed.Should().BeFalse();
    }

    [Fact]
    public void Read_ReturnsSilence_WhenInnerThrows()
    {
        var thrower = new ThrowingProvider(throwUntilCall: 1);
        var safe = new SafeSampleProvider(thrower);

        var buf = new float[4];
        // Pre-fill so we can verify it was cleared.
        for (int i = 0; i < 4; i++) buf[i] = 7f;

        int read = safe.Read(buf, 0, 4);

        read.Should().Be(4, "the wrapper must always satisfy the caller's request");
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
        safe.TotalFailures.Should().Be(1);
        safe.IsPermanentlyBypassed.Should().BeFalse("one throw shouldn't trigger permanent bypass");
    }

    [Fact]
    public void Read_RecoversAfterTransientThrow()
    {
        var thrower = new ThrowingProvider(throwUntilCall: 1);
        var safe = new SafeSampleProvider(thrower);
        var buf = new float[4];

        // First call throws → silence.
        safe.Read(buf, 0, 4);
        buf.Should().AllSatisfy(s => s.Should().Be(0f));

        // Second call succeeds → real samples.
        safe.Read(buf, 0, 4);
        buf.Should().AllSatisfy(s => s.Should().Be(99f));
        safe.IsPermanentlyBypassed.Should().BeFalse();
    }

    [Fact]
    public void Read_EntersPermanentBypass_AfterThresholdConsecutiveThrows()
    {
        var thrower = new ThrowingProvider(throwUntilCall: int.MaxValue);
        var safe = new SafeSampleProvider(thrower, consecutiveFailureThreshold: 3);
        var buf = new float[4];

        // 3 consecutive throws trip the lock-out.
        safe.Read(buf, 0, 4);
        safe.Read(buf, 0, 4);
        safe.IsPermanentlyBypassed.Should().BeFalse("two throws is below threshold");
        safe.Read(buf, 0, 4);
        safe.IsPermanentlyBypassed.Should().BeTrue("third throw trips permanent bypass");

        int callsAtLockout = thrower.Calls;

        // Subsequent reads must NOT call into the inner provider.
        safe.Read(buf, 0, 4);
        safe.Read(buf, 0, 4);
        thrower.Calls.Should().Be(callsAtLockout,
            "permanently bypassed wrapper must stop calling the throwing plugin");
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
    }

    [Fact]
    public void ConsecutiveFailureCounter_ResetsOnSuccess()
    {
        // Throw twice, then succeed: the counter resets and the next two
        // throws don't trip the threshold.
        var thrower = new ThrowingProvider(throwUntilCall: 2);
        var safe = new SafeSampleProvider(thrower, consecutiveFailureThreshold: 3);
        var buf = new float[4];

        safe.Read(buf, 0, 4); // throw 1
        safe.Read(buf, 0, 4); // throw 2
        safe.Read(buf, 0, 4); // success — counter resets
        // Now the inner won't throw anymore. Should stay un-bypassed.
        safe.Read(buf, 0, 4);
        safe.IsPermanentlyBypassed.Should().BeFalse();
        safe.TotalFailures.Should().Be(2);
    }

    [Fact]
    public void ShortRead_FromInner_IsPaddedWithSilence()
    {
        // Some providers return short reads at EOF. SafeSampleProvider
        // pads to the requested count so the caller can assume ReadFully.
        var partial = Substitute.For<ISampleProvider>();
        partial.WaveFormat.Returns(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        partial.Read(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<int>())
               .Returns(args =>
               {
                   var buf = (float[])args[0];
                   int offset = (int)args[1];
                   buf[offset + 0] = 1f;
                   buf[offset + 1] = 2f;
                   return 2; // short read
               });

        var safe = new SafeSampleProvider(partial);
        var buf = new float[5];
        for (int i = 0; i < 5; i++) buf[i] = 7f;

        int read = safe.Read(buf, 0, 5);

        read.Should().Be(5);
        buf.Should().Equal(1f, 2f, 0f, 0f, 0f);
    }
}
