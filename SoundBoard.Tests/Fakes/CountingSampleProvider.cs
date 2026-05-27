using NAudio.Wave;

namespace SoundBoard.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="ISampleProvider"/> that emits an arithmetic
/// sequence: sample 0 = 1.0f, sample 1 = 2.0f, …, sample N = (N+1).
///
/// <para>Used as a "dry" source where every test can compute the expected
/// output for any read offset/length. Exposes <see cref="SamplesRead"/>
/// so tests can verify how far the source has been advanced — critical
/// for bypass / state-freeze tests where we need to distinguish "the
/// switch pulled from me" vs "my wrapper pulled from me".</para>
/// </summary>
public sealed class CountingSampleProvider : ISampleProvider
{
    private int _position;

    /// <summary>Total samples read from this provider across all Read calls.</summary>
    public int SamplesRead => _position;

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            // 1-based so the first sample is non-zero (easier to spot in
            // assertions that compare against a known "ramp" value).
            buffer[offset + i] = _position + i + 1;
        }
        _position += count;
        return count;
    }
}
