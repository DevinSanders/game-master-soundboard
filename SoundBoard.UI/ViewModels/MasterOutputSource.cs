using SoundBoard.Core.Audio;
using System;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Adapts <see cref="MasterMixer.AudioDataAvailable"/> (which lives in Core
/// and can't reference UI types) into the <see cref="IAudioSamples"/> contract
/// the <see cref="SoundBoard.UI.Controls.AudioVisualizer"/> expects. One
/// instance lives on the main window so the Now Playing strip can render the
/// post-DSP final mix.
/// </summary>
internal sealed class MasterOutputSource : IAudioSamples
{
    public event EventHandler<float[]>? AudioDataAvailable;

    public MasterOutputSource(MasterMixer master)
    {
        master.AudioDataAvailable += (s, data) => AudioDataAvailable?.Invoke(this, data);
    }
}
