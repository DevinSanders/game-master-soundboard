using System;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Anything that produces a stream of float samples can drive the
/// <see cref="SoundBoard.UI.Controls.AudioVisualizer"/>. Both
/// <see cref="PlayingTrackViewModel"/> and
/// <see cref="PlayingPresetViewModel"/> implement this so the mixer's track
/// card and preset card can share the same visualizer control.
/// </summary>
public interface IAudioSamples
{
    event EventHandler<float[]>? AudioDataAvailable;
}
