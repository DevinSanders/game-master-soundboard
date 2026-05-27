using NAudio.Wave;
using System;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Scans an audio source for leading and trailing silence and reports the
/// first/last non-silent timestamps. Used by the track editor's
/// auto-trim-silence option to suggest tighter start and end points without
/// re-encoding the file.
/// </summary>
public static class SilenceTrimmer
{
    public static void TrimSilence(ISeekableSampleProvider provider, out TimeSpan start, out TimeSpan end, float threshold = 0.01f)
    {
        start = TimeSpan.Zero;
        end = provider.TotalTime;

        var originalPos = provider.Position;
        provider.Position = TimeSpan.Zero;

        // ~0.1 seconds of audio per Read. Small enough that a corrupt-frame
        // decoder stall has more frequent chances to either return or be
        // interrupted by a reader-dispose from the caller; large enough that
        // typical files still finish in a handful of iterations.
        int bufferSize = (provider.WaveFormat.SampleRate / 10) * provider.WaveFormat.Channels;
        if (bufferSize < 256) bufferSize = 256;
        float[] buffer = new float[bufferSize];

        // Compute an upper bound on the total sample count from TotalTime.
        // Some decoders (NVorbis on certain ogg files) can return >0 from
        // Read() past the end of stream — without a count-based safety net
        // the trim scan loops forever. Allow a 10% slack for rounding.
        long maxSamples = (long)Math.Ceiling(
            provider.TotalTime.TotalSeconds *
            provider.WaveFormat.SampleRate *
            provider.WaveFormat.Channels * 1.10);
        if (maxSamples < bufferSize) maxSamples = bufferSize * 4L; // fallback for unknown TotalTime

        bool startFound = false;
        long startSample = 0;

        long currentSample = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                if (Math.Abs(buffer[i]) > threshold)
                {
                    startSample = currentSample + i;
                    startFound = true;
                    break;
                }
            }
            if (startFound) break;
            currentSample += read;
            if (currentSample >= maxSamples) break; // safety: decoder won't stop
        }

        if (!startFound)
        {
             provider.Position = originalPos;
             return;
        }

        start = TimeSpan.FromSeconds((double)startSample / (provider.WaveFormat.SampleRate * provider.WaveFormat.Channels));

        provider.Position = start;
        long lastNonSilentSample = startSample;
        currentSample = startSample;

        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
             for (int i = 0; i < read; i++)
             {
                  if (Math.Abs(buffer[i]) > threshold)
                  {
                       lastNonSilentSample = currentSample + i;
                  }
             }
             currentSample += read;
             if (currentSample >= maxSamples) break; // safety: decoder won't stop
        }

        end = TimeSpan.FromSeconds((double)lastNonSilentSample / (provider.WaveFormat.SampleRate * provider.WaveFormat.Channels));

        provider.Position = originalPos;
    }
}
