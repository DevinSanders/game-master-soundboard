using NAudio.Wave;
using System;
using System.Threading;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Per-track playback engine. Wraps an <see cref="ISeekableSampleProvider"/>
/// from the decoder layer and applies the user-facing controls: volume, fade
/// in/out, start-and-end-point clipping, looping, and the start-delay gap
/// that's re-applied on every loop iteration. One instance corresponds to
/// one playing item on the mixer.
/// </summary>
public class TrackSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISeekableSampleProvider _source;
    
    public WaveFormat WaveFormat => _source.WaveFormat;
    
    public TimeSpan TotalTime => _source.TotalTime;

    /// <summary>True when the underlying codec source supports random-access
    /// seeking. False for live web streams; the mixer card UI hides the
    /// scrub slider and disables looping in that case.</summary>
    public bool IsSeekable => _source.IsSeekable;

    public TimeSpan Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    // Mutable per-track playback state — written from the UI thread
    // (slider drag, loop toggle, pause click) and read from the audio
    // thread inside Read. Plain `{ get; set; }` reads/writes are
    // word-sized on every supported architecture and therefore tear-free,
    // but on weakly-ordered CPUs (ARM macOS / Snapdragon) a non-barriered
    // UI-thread write can remain invisible to the audio thread for an
    // unbounded time. Phase R2 wraps the hot fields in
    // Volatile.Read/Write to flush the writes promptly. TimeSpan is
    // backed by a single long Ticks field, so the long-volatile pattern
    // works for them too — same trick we use for float volumes via
    // BitConverter.SingleToInt32Bits.

    private long _startPointTicks;
    public TimeSpan StartPoint
    {
        get => new TimeSpan(Volatile.Read(ref _startPointTicks));
        set => Volatile.Write(ref _startPointTicks, value.Ticks);
    }

    private long _endPointTicks = TimeSpan.MaxValue.Ticks;
    public TimeSpan EndPoint
    {
        get => new TimeSpan(Volatile.Read(ref _endPointTicks));
        set => Volatile.Write(ref _endPointTicks, value.Ticks);
    }

    private int _isLooping; // 0 = false, 1 = true
    public bool IsLooping
    {
        get => Volatile.Read(ref _isLooping) != 0;
        set => Volatile.Write(ref _isLooping, value ? 1 : 0);
    }

    private int _isPaused; // 0 = false, 1 = true
    public bool IsPaused
    {
        get => Volatile.Read(ref _isPaused) != 0;
        set => Volatile.Write(ref _isPaused, value ? 1 : 0);
    }

    /// <summary>Silence applied at the start of playback (after Play()) and
    /// re-applied on every loop iteration. Useful for staggering tracks in a
    /// preset and for adding breathing room between loop passes.</summary>
    private long _startDelayTicks;
    public TimeSpan StartDelay
    {
        get => new TimeSpan(Volatile.Read(ref _startDelayTicks));
        set => Volatile.Write(ref _startDelayTicks, value.Ticks);
    }

    // float exposed via Volatile.Int32 bit-pattern — same idiom as
    // MasterMixer.LocalVolume and BusMixer.Volume. Default 1.0f.
    private int _volumeBits = BitConverter.SingleToInt32Bits(1.0f);
    public float Volume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _volumeBits));
        set => Volatile.Write(ref _volumeBits, BitConverter.SingleToInt32Bits(value));
    }

    // Silence countdown — driven from Play() (initial delay) and from the
    // loop-back path in Read() (between iterations). Read() emits zeros
    // until it hits zero, then resumes at StartPoint.
    private int _startGapSamplesRemaining = 0;
    
    // Fade state
    private float _currentFadeMultiplier = 1.0f;
    private int _fadeSamplePosition = 0;
    private int _fadeSampleCount = 0;
    private enum FadeState { None, FadingIn, FadingOut }
    private FadeState _fadeState = FadeState.None;

    public Action? OnPlaybackStopped { get; set; }
    public event EventHandler<float[]>? AudioDataAvailable;

    public TrackSampleProvider(ISeekableSampleProvider source)
    {
        _source = source;
        EndPoint = _source.TotalTime;
    }

    public void Play(TimeSpan fadeInDuration)
    {
        _source.Position = StartPoint;
        _stoppedTriggered = false;

        // Initial silence — applied before the first sample is heard so
        // presets can stagger track start times.
        if (StartDelay > TimeSpan.Zero)
        {
            _startGapSamplesRemaining = (int)(StartDelay.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
        }

        if (fadeInDuration > TimeSpan.Zero)
        {
            _fadeState = FadeState.FadingIn;
            _fadeSampleCount = (int)(fadeInDuration.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
            _fadeSamplePosition = 0;
            _currentFadeMultiplier = 0.0f;
        }
        else
        {
            _fadeState = FadeState.None;
            _currentFadeMultiplier = 1.0f;
        }
    }

    public void Stop(TimeSpan fadeOutDuration)
    {
        if (fadeOutDuration > TimeSpan.Zero)
        {
            _fadeState = FadeState.FadingOut;
            _fadeSampleCount = (int)(fadeOutDuration.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
            _fadeSamplePosition = 0;
            // current fade multiplier stays whatever it is, and ramps down to 0
        }
        else
        {
            _currentFadeMultiplier = 0.0f;
            TriggerStopped();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_currentFadeMultiplier <= 0f && _fadeState == FadeState.FadingOut)
        {
            TriggerStopped();
            return 0;
        }

        if (IsPaused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int samplesRead = 0;
        while (samplesRead < count)
        {
            // Start-delay gap: emit silence until the configured delay elapses.
            // Both the initial pre-roll (set in Play()) and the inter-loop gap
            // (set in BeginLoopReturn()) handle their own source seek BEFORE
            // entering the gap, so we never touch _source.Position here.
            if (_startGapSamplesRemaining > 0)
            {
                int gapToWrite = Math.Min(count - samplesRead, _startGapSamplesRemaining);
                Array.Clear(buffer, offset + samplesRead, gapToWrite);
                samplesRead += gapToWrite;
                _startGapSamplesRemaining -= gapToWrite;
                continue;
            }

            TimeSpan currentPosition = _source.Position;
            if (currentPosition >= EndPoint)
            {
                if (IsLooping) { BeginLoopReturn(); continue; }
                TriggerStopped();
                break;
            }

            var timeRemaining = EndPoint - currentPosition;
            var samplesRemaining = (int)(timeRemaining.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);

            int samplesToRead = Math.Min(count - samplesRead, samplesRemaining);
            if (samplesToRead <= 0)
            {
                if (IsLooping) { BeginLoopReturn(); continue; }
                TriggerStopped();
                break;
            }

            int read = _source.Read(buffer, offset + samplesRead, samplesToRead);
            if (read == 0)
            {
                if (IsLooping) { BeginLoopReturn(); continue; }
                TriggerStopped();
                break;
            }

            for (int i = 0; i < read; i++)
            {
                if (_fadeState == FadeState.FadingIn)
                {
                    _currentFadeMultiplier = (float)_fadeSamplePosition / _fadeSampleCount;
                    _fadeSamplePosition++;
                    if (_fadeSamplePosition >= _fadeSampleCount)
                    {
                        _fadeState = FadeState.None;
                        _currentFadeMultiplier = 1.0f;
                    }
                }
                else if (_fadeState == FadeState.FadingOut)
                {
                    _currentFadeMultiplier = Math.Max(0f, _currentFadeMultiplier - (1.0f / _fadeSampleCount));
                    if (_currentFadeMultiplier <= 0.0f)
                    {
                        for (int j = i; j < read; j++) buffer[offset + samplesRead + j] = 0f;
                        read = i; 
                        TriggerStopped();
                        break;
                    }
                }

                buffer[offset + samplesRead + i] *= (Volume * _currentFadeMultiplier);
            }

            samplesRead += read;
            
            if (_fadeState == FadeState.FadingOut && _currentFadeMultiplier <= 0f)
            {
                break;
            }
        }

        if (samplesRead > 0 && AudioDataAvailable != null)
        {
            float[] data = new float[samplesRead];
            Array.Copy(buffer, offset, data, 0, samplesRead);
            AudioDataAvailable.Invoke(this, data);
        }

        return samplesRead;
    }

    private void BeginLoopReturn()
    {
        // Always rewind first; the gap (if any) then emits silence FROM this
        // newly-seeked position. Doing the seek up front avoids a stale read
        // after the gap on codecs that have lazy seek state (NLayer's MpegFile
        // in particular has returned 0 samples on the read immediately after
        // a Time = ... seek in some cases).
        _source.Position = StartPoint;
        if (StartDelay > TimeSpan.Zero)
        {
            _startGapSamplesRemaining = (int)(StartDelay.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
        }
    }

    private bool _stoppedTriggered = false;
    private void TriggerStopped()
    {
        if (!_stoppedTriggered)
        {
            _stoppedTriggered = true;
            OnPlaybackStopped?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
