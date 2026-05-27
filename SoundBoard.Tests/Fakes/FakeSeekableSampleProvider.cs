using NAudio.Wave;
using SoundBoard.Core.Audio;
using System;

namespace SoundBoard.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="ISeekableSampleProvider"/> for
/// <see cref="SoundBoard.Core.Audio.TrackSampleProvider"/> tests. Emits a
/// deterministic ramp (sample N = N+1 at integer positions) so output
/// values reveal exactly where the source was read from. Length and
/// sample rate are configurable; <see cref="IsSeekable"/> can be flipped
/// to model the non-seekable web-stream case.
/// </summary>
public sealed class FakeSeekableSampleProvider : ISeekableSampleProvider
{
    private long _position;
    private readonly long _lengthSamples;

    public FakeSeekableSampleProvider(int sampleRate = 48000, int channels = 2, TimeSpan? duration = null, bool seekable = true)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        var d = duration ?? TimeSpan.FromMinutes(5);
        _lengthSamples = (long)(d.TotalSeconds * sampleRate * channels);
        IsSeekable = seekable;
    }

    public WaveFormat WaveFormat { get; }
    public bool IsSeekable { get; }

    public TimeSpan TotalTime =>
        TimeSpan.FromSeconds((double)_lengthSamples / WaveFormat.SampleRate / WaveFormat.Channels);

    public TimeSpan Position
    {
        get
        {
            return TimeSpan.FromSeconds((double)_position / WaveFormat.SampleRate / WaveFormat.Channels);
        }
        set
        {
            if (!IsSeekable) return;
            _position = (long)(value.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
            if (_position < 0) _position = 0;
            if (_position > _lengthSamples) _position = _lengthSamples;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long available = _lengthSamples - _position;
        int toRead = (int)Math.Min(count, available);
        for (int i = 0; i < toRead; i++)
        {
            // 1-based ramp so the first sample is non-zero and the value
            // at any position is its 1-based sample index.
            buffer[offset + i] = (float)(_position + i + 1);
        }
        _position += toRead;
        return toRead;
    }

    public void Dispose() { }
}
