using System.Collections.Generic;

namespace SoundBoard.Core.Audio;

/// <summary>An audio output endpoint as enumerated by the platform backend
/// (e.g. a Windows WASAPI MMDevice, a macOS CoreAudio device).</summary>
public interface IAudioOutputDevice
{
    /// <summary>Backend-specific identifier (e.g. WASAPI MMDevice ID
    /// "{0.0.0.00000000}.{guid}"). Used to recall the user's preferred
    /// device across launches.</summary>
    string Id { get; }

    /// <summary>Human-readable device name to show in the settings UI.</summary>
    string Name { get; }
}

/// <summary>Concrete <see cref="IAudioOutputDevice"/> populated by each
/// platform backend (NAudio on Windows, OpenAL Soft on macOS/Linux).</summary>
public class AudioDevice : IAudioOutputDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>Surfaces the list of audio output devices to UI code that
/// doesn't want to depend directly on a specific platform backend.</summary>
public interface IAudioOutputService
{
    IEnumerable<IAudioOutputDevice> GetOutputDevices();
}
