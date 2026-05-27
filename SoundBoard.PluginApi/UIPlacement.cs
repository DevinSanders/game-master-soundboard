using System;

namespace SoundBoard.PluginApi;

/// <summary>
/// Insertion points where a plugin's UI can be mounted in the host shell.
/// </summary>
[Flags]
public enum UIPlacement
{
    /// <summary>No placement — the extension contributes no UI. Use a real
    /// flag instead; this is the zero default.</summary>
    None = 0,

    /// <summary>Mounted inside the Master Mixer panel.</summary>
    Mixer = 1,

    /// <summary>Mounted in the per-track editor window.</summary>
    TrackEditor = 2,

    /// <summary>Mounted in the Settings window.</summary>
    Settings = 4,

    /// <summary>Surfaced as a global overlay or its own window.</summary>
    Overlay = 8
}
