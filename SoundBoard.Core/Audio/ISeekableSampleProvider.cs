using NAudio.Wave;
using System;

namespace SoundBoard.Core.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that may support random-access seeking
/// and report its total duration. Every codec wrapper in this folder
/// implements this so <see cref="TrackSampleProvider"/> can apply
/// start/end points, loop, and position telemetry uniformly across formats.
///
/// <para>Not every source is seekable — live web streams (Shoutcast,
/// Icecast, live HLS) cannot be scrubbed. Implementations report
/// <see cref="IsSeekable"/> = <c>false</c> in that case; the position
/// setter and <see cref="TotalTime"/> may return defaults or throw.
/// UI layers should check <see cref="IsSeekable"/> before binding scrub
/// sliders.</para>
/// </summary>
public interface ISeekableSampleProvider : ISampleProvider, IDisposable
{
    /// <summary>True when <see cref="Position"/> and <see cref="TotalTime"/>
    /// are meaningful and writable. False for live streams.</summary>
    bool IsSeekable { get; }

    /// <summary>Current playback position within the source. Setter is a
    /// no-op (or may throw) when <see cref="IsSeekable"/> is false.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Total length of the source media. Returns
    /// <see cref="TimeSpan.MaxValue"/> (or another sentinel) when
    /// <see cref="IsSeekable"/> is false.</summary>
    TimeSpan TotalTime { get; }
}
