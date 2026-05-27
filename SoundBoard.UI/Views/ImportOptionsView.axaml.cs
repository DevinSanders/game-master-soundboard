using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SoundBoard.UI.Views;

/// <summary>Modal dialog shown before a library import: pick merge vs new
/// library, name the new library, and add roots for path reconciliation.</summary>
public partial class ImportOptionsView : UserControl
{
    public ImportOptionsView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
