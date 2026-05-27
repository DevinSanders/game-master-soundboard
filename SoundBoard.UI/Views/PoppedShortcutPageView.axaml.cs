using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Views;

/// <summary>
/// Pop-out window view for a single shortcut page. Mirrors
/// <see cref="ShortcutsView"/>'s ghost-mode reorder via a shared
/// <see cref="GhostCardReorderController{TCardVm}"/>. Cross-window drops
/// are intentionally NOT accepted here — the main soundboard is the
/// canonical add target; the popped page is reorder-only.
/// </summary>
public partial class PoppedShortcutPageView : UserControl
{
    private ItemsControl? _items;
    private GhostCardReorderController<ShortcutButtonViewModel>? _reorder;

    public PoppedShortcutPageView()
    {
        InitializeComponent();

        _items = this.FindControl<ItemsControl>("ShortcutItems");
        if (_items != null)
        {
            _reorder = new GhostCardReorderController<ShortcutButtonViewModel>(
                root: this,
                getItems: () => _items,
                getTemplate: () => _items?.ItemTemplate,
                moveVisually: (s, t) => Vm?.SwapButtons(s, t),
                persistOrder: () => Vm?.PersistButtonOrder());
            _reorder.Attach(_items);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private PoppedShortcutPageViewModel? Vm => DataContext as PoppedShortcutPageViewModel;
}
