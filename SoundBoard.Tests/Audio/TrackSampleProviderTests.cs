using SoundBoard.Core.Audio;
using SoundBoard.Tests.Fakes;
using System;
using System.Threading;

namespace SoundBoard.Tests.Audio;

/// <summary>
/// Core audio playback contract: <see cref="TrackSampleProvider"/> applies
/// volume, fade in/out, StartPoint clipping, looping, start-delay gap, and
/// fires <c>OnPlaybackStopped</c> exactly once. These behaviors were
/// previously only covered by manual testing — a regression here would
/// affect every track playback the app does.
/// </summary>
public class TrackSampleProviderTests
{
    private static TrackSampleProvider MakeProvider(int sampleRate = 48000, int channels = 2, int durationSeconds = 5)
    {
        var src = new FakeSeekableSampleProvider(
            sampleRate, channels, TimeSpan.FromSeconds(durationSeconds));
        return new TrackSampleProvider(src);
    }

    [Fact]
    public void Play_WithoutDelayOrFade_EmitsSourceSamplesAtUnityVolume()
    {
        var p = MakeProvider();
        p.Play(TimeSpan.Zero);
        var buf = new float[8];

        int read = p.Read(buf, 0, 8);

        read.Should().Be(8);
        // FakeSeekableSampleProvider's ramp: 1, 2, 3, ... * Volume(=1.0) * fade(=1.0).
        buf.Should().Equal(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f);
    }

    [Fact]
    public void Volume_ScalesOutput()
    {
        var p = MakeProvider();
        p.Volume = 0.5f;
        p.Play(TimeSpan.Zero);
        var buf = new float[4];

        p.Read(buf, 0, 4);

        buf.Should().Equal(0.5f, 1.0f, 1.5f, 2.0f);
    }

    [Fact]
    public void IsPaused_EmitsSilenceWithoutAdvancingSource()
    {
        var p = MakeProvider();
        p.Play(TimeSpan.Zero);
        var buf = new float[4];

        // Read once to get past Play()'s zero-position.
        p.Read(buf, 0, 4);
        buf.Should().Equal(1f, 2f, 3f, 4f);

        p.IsPaused = true;
        var posBefore = p.Position;
        p.Read(buf, 0, 4);
        buf.Should().AllSatisfy(s => s.Should().Be(0f));
        p.Position.Should().Be(posBefore, "pause must not advance the source");

        p.IsPaused = false;
        p.Read(buf, 0, 4);
        buf.Should().Equal(new[] { 5f, 6f, 7f, 8f }, "playback resumes from where it paused");
    }

    [Fact]
    public void StartPoint_SeeksSourceOnPlay()
    {
        // StartPoint at 1 second of 48kHz stereo = 96000 samples.
        var p = MakeProvider(sampleRate: 48000, channels: 2, durationSeconds: 10);
        p.StartPoint = TimeSpan.FromSeconds(1);

        p.Play(TimeSpan.Zero);
        var buf = new float[4];
        p.Read(buf, 0, 4);

        // Source ramp starts at 1, so position 96000 yields 96001, 96002, ...
        buf.Should().Equal(96001f, 96002f, 96003f, 96004f);
    }

    [Fact]
    public void EndPoint_TriggersStop_BeforeSourceEnd()
    {
        // EndPoint at 0.5 sec of 48kHz stereo = 48000 samples. After that,
        // the provider should stop emitting source data and fire OnPlaybackStopped.
        var p = MakeProvider(sampleRate: 48000, channels: 2, durationSeconds: 10);
        p.EndPoint = TimeSpan.FromSeconds(0.5);
        bool stoppedFired = false;
        p.OnPlaybackStopped = () => stoppedFired = true;

        p.Play(TimeSpan.Zero);

        // Read enough to cross EndPoint.
        var buf = new float[100_000];
        p.Read(buf, 0, 100_000);

        stoppedFired.Should().BeTrue("EndPoint reached → OnPlaybackStopped fires");
    }

    [Fact]
    public void OnPlaybackStopped_FiresAtMostOnce()
    {
        // TriggerStopped is idempotent — multiple stop paths (EndPoint,
        // source EOF, Stop()) might fire but the callback runs once.
        var p = MakeProvider(durationSeconds: 1);
        int fireCount = 0;
        p.OnPlaybackStopped = () => fireCount++;
        p.Play(TimeSpan.Zero);

        var buf = new float[200_000];
        p.Read(buf, 0, 200_000);   // probably crosses source end
        p.Read(buf, 0, 200_000);   // after end, should still be at most 1 call
        p.Stop(TimeSpan.Zero);     // explicit stop after natural end

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Looping_RewindsToStartPoint_OnSourceEnd()
    {
        var p = MakeProvider(durationSeconds: 1);
        p.IsLooping = true;
        p.StartPoint = TimeSpan.Zero;
        p.EndPoint = TimeSpan.FromSeconds(1);

        p.Play(TimeSpan.Zero);

        // After consuming the whole source, position should rewind, not fire stop.
        bool stopFired = false;
        p.OnPlaybackStopped = () => stopFired = true;

        var buf = new float[200_000]; // > 1 sec at 48k stereo (96000)
        p.Read(buf, 0, 200_000);

        stopFired.Should().BeFalse("loop must rewind, not stop");
    }

    [Fact]
    public void StartDelay_EmitsSilencePrefix()
    {
        // StartDelay = 0.1s @ 48k stereo = 9600 silence samples before audio.
        var p = MakeProvider(sampleRate: 48000, channels: 2);
        p.StartDelay = TimeSpan.FromMilliseconds(100);
        p.Play(TimeSpan.Zero);

        var buf = new float[100];
        p.Read(buf, 0, 100);

        // First 100 samples are inside the start-delay gap → all zeros.
        buf.Should().AllSatisfy(s => s.Should().Be(0f),
            "first samples after Play are still inside the StartDelay gap");
    }

    [Fact]
    public void WaveFormat_PassesThroughFromSource()
    {
        var p = MakeProvider(sampleRate: 44100, channels: 1);
        p.WaveFormat.SampleRate.Should().Be(44100);
        p.WaveFormat.Channels.Should().Be(1);
    }

    [Fact]
    public void Dispose_DisposesSource()
    {
        // FakeSeekableSampleProvider doesn't track Dispose calls but the
        // pattern matters: TrackSampleProvider should forward Dispose to
        // the source. Smoke test by ensuring no exceptions.
        var src = new FakeSeekableSampleProvider();
        var p = new TrackSampleProvider(src);

        var act = () => p.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void NonSeekable_Source_ReportsIsSeekableFalse()
    {
        var src = new FakeSeekableSampleProvider(seekable: false);
        var p = new TrackSampleProvider(src);
        p.IsSeekable.Should().BeFalse("a live-stream codec exposes IsSeekable=false; UI hides scrub/loop");
    }
}
