using NAudio.Wave;
using System;

namespace SoundBoard.Core.Audio;

/// <summary>
/// WAV decoder wrapper. Built-in handler for <c>.wav</c> files, used by
/// <see cref="AudioFileReaderCrossPlatform.Create"/>. Delegates to NAudio's
/// WaveFileReader and lets its built-in <c>ToSampleProvider()</c> pick the
/// right PCM-to-float conversion for the input bit depth.
/// </summary>
public class SeekableWaveSampleProvider : ISeekableSampleProvider, IDisposable
{
    private readonly WaveFileReader _reader;
    private readonly ISampleProvider _sampleProvider;

    public WaveFormat WaveFormat => _sampleProvider.WaveFormat;

    public SeekableWaveSampleProvider(string fileName)
    {
        _reader = new WaveFileReader(fileName);
        _sampleProvider = _reader.ToSampleProvider();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return _sampleProvider.Read(buffer, offset, count);
    }

    public TimeSpan Position
    {
        get => _reader.CurrentTime;
        set => _reader.CurrentTime = value;
    }

    public TimeSpan TotalTime => _reader.TotalTime;

    public bool IsSeekable => true; // Local WAV file — always seekable.

    public void Dispose()
    {
        _reader.Dispose();
    }
}
