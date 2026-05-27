using NAudio.Wave;
using System;
using System.Collections.Generic;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Platform-specific audio output sink. Implementations pull samples from an
/// <see cref="ISampleProvider"/> on their own audio thread and feed the OS.
/// One backend per supported platform; selected at runtime by
/// <see cref="LocalAudioPlayer"/>.
/// </summary>
public interface IAudioOutputBackend : IDisposable
{
    /// <summary>
    /// (Re)initialize the backend bound to the given sample source. May be
    /// called multiple times (e.g. when the user changes device). The source's
    /// <see cref="ISampleProvider.WaveFormat"/> dictates the sink's format.
    /// </summary>
    /// <param name="source">Pull source — typically the MasterMixer.</param>
    /// <param name="preferredDeviceId">Backend-specific device id, or null for the system default.</param>
    void Init(ISampleProvider source, string? preferredDeviceId);

    void Play();
    void Pause();
    void Stop();

    bool IsPlaying { get; }

    /// <summary>Enumerate the playback devices visible to this backend.</summary>
    IEnumerable<AudioDevice> GetDevices();
}
