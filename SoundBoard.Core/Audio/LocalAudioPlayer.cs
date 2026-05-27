using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Thin platform dispatcher in front of an <see cref="IAudioOutputBackend"/>.
/// Picks NAudio (WASAPI/WaveOut) on Windows for low latency and existing
/// device-selection behavior; OpenAL Soft (via Silk.NET.OpenAL) on macOS
/// and Linux for a single, hardened Unix-family backend.
/// </summary>
public class LocalAudioPlayer : IDisposable
{
    private readonly MasterMixer _masterMixer;
    private readonly IAudioOutputBackend _backend;
    private bool _disposed;

    public float Volume { get; set; } = 1.0f;
    public string? PreferredDeviceId { get; set; }

    public LocalAudioPlayer(MasterMixer masterMixer)
    {
        _masterMixer = masterMixer ?? throw new ArgumentNullException(nameof(masterMixer));
        _backend = CreateBackendForPlatform();
    }

    private static IAudioOutputBackend CreateBackendForPlatform()
    {
        // Per-platform backend selection:
        //   Windows       → NAudio WASAPI (mature, per-device routing via
        //                   MMDeviceEnumerator, lowest latency on Windows).
        //   macOS / Linux → OpenAL Soft via Silk.NET.OpenAL. Single backend
        //                   for both Unix-family platforms — one code
        //                   path tested twice, no per-OS native binding
        //                   surface to drift against.
        //
        // Why OpenAL on non-Windows. macOS originally used miniaudio (a
        // bundled C native via Miniaudio-CS) but the binding layer was
        // fragile — we hit a struct-layout bug where pDevice->playback.channels
        // read back as 0, making every callback ask for zero samples
        // (perfect-cadence silence). On Linux miniaudio was worse: its
        // PulseAudio backend heap-corrupted on bleeding-edge userspace
        // (Ubuntu 25.10 + libpulsecommon-17), confirmed via core dump.
        // OpenAL Soft has 20 years of hardened backend selection
        // (CoreAudio on Mac; PipeWire → PulseAudio → ALSA → JACK on Linux)
        // with each backend sandboxed against the others.
        //
        // Both Unix backends ultimately deliver float32 stereo to the
        // OS audio thread bit-identically — audio quality is the same as
        // it would be with any other library. OpenAL's buffer-push model
        // costs us ~80 ms of click-to-sound latency vs miniaudio's ~30 ms,
        // well under the perception threshold for a soundboard.
        //
        // Linux runtime escape hatch: ALSOFT_DRIVERS=alsa forces OpenAL
        // Soft to skip PulseAudio if a future distro breaks libpulse
        // for OpenAL too — no host code change needed.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new NAudioWindowsBackend();
        return new OpenALBackend();
    }

    public void Init() => _backend.Init(_masterMixer, PreferredDeviceId);

    public void Play() => _backend.Play();
    public void Pause() => _backend.Pause();
    public void Stop() => _backend.Stop();

    /// <summary>
    /// Enumerate playback devices for the current platform. Always includes
    /// a "Default Output" sentinel for the system default route.
    /// </summary>
    public static IEnumerable<AudioDevice> GetDevices()
    {
        // GetDevices is static for the settings UI's convenience. Spin up a
        // throw-away backend so we hit the same code path the real player uses.
        IAudioOutputBackend probe = CreateBackendForPlatform();
        var devices = new List<AudioDevice>();
        try { devices.AddRange(probe.GetDevices()); }
        finally { probe.Dispose(); }

        if (devices.Count == 0)
            devices.Add(new AudioDevice { Id = "-1", Name = "Default Output" });
        return devices;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _backend.Dispose();
        _disposed = true;
    }
}
