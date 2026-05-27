using Avalonia;
using Avalonia.Controls;
using System;

namespace SoundBoard.UI.Views;

/// <summary>Mixer window view. Hosts the wrap-panel of active item cards
/// (tracks / presets / playlists), the master local + Discord volume strip,
/// and a slot for plugin UI extensions placed at the mixer.</summary>
public partial class MixerView : UserControl
{
    public MixerView()
    {
        InitializeComponent();
    }
}
