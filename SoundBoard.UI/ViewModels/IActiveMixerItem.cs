using System;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Anything the mixer can show as a single playing card. Today: a Track or a
/// Preset. The Mixer view picks a card template based on the runtime type;
/// this interface unifies the parts that every card needs.
/// </summary>
public interface IActiveMixerItem
{
    string Name { get; }
    bool IsPaused { get; set; }
    double Volume { get; set; }
    void Stop();
}
