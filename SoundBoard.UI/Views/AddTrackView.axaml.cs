using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoundBoard.UI.Views;

/// <summary>Modal dialog shown when the user clicks "Add Track…" in the
/// Library window. Lets them enter a URI (file path or URL) with an
/// optional Browse button to populate it from the OS file picker,
/// plus optional Name + Tags. The host inserts the Track row on
/// confirm if a registered codec claims the URI.</summary>
public partial class AddTrackView : UserControl
{
    public AddTrackView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
