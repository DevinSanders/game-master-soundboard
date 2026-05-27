using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoundBoard.UI.Views;

/// <summary>Modal icon-picker dialog. Shows the RPG Awesome glyph grid with
/// search; selecting a glyph closes the dialog and returns the icon name.</summary>
public partial class IconPickerView : UserControl
{
    public IconPickerView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
