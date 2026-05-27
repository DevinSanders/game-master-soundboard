using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SoundBoard.PluginApi;
using SoundBoard.UI.Controls;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Views;

/// <summary>
/// FX Chain editor. Available effect plugins live on the left as a list of
/// draggable cards; the user drops one onto the chain pane on the right to
/// attach it. Mirrors the drag-from-list / drop-on-pane pattern used by the
/// Library → Soundboard and Presets → Playlist flows.
/// </summary>
public partial class SamplerEditorView : UserControl
{
    /// <summary>Drag payload carrying the plugin instance the user picked
    /// from the Available list. The plugin reference is fine to carry —
    /// plugins are singletons in <see cref="IPluginService.LoadedPlugins"/>
    /// for the app's lifetime and the drop handler just reads its
    /// <c>Id</c> to delegate to the VM's <c>AddSampler</c>.</summary>
    public static readonly DataFormat<IAudioSamplerPlugin> SamplerDragFormat = Services.DragFormats.SamplerPlugin;

    /// <summary>Drag payload for reordering chain cards within the editor.
    /// Carries the row VM rather than the plugin so the drop handler can
    /// locate the moving card by reference in the <c>Attached</c>
    /// collection — same shape as <c>PlaylistItemDragFormat</c> and
    /// <c>CardDragFormat</c> in the other editors.</summary>
    public static readonly DataFormat<AttachedSamplerViewModel> ChainItemDragFormat = Services.DragFormats.FxChainItem;

    // Highlight applied to the drop zone while the pointer is over it with
    // a valid payload. Pulled from the theme so a palette swap follows.
    private static IBrush DropHighlightBrush => Services.ThemeBrushes.DropZoneHighlight;

    private readonly DragInitiator _drag = new();
    private ListBox? _availableList;
    private Border? _chainDropZone;
    private ItemsControl? _chainItems;
    private GhostCardReorderController<AttachedSamplerViewModel>? _reorder;

    public SamplerEditorView()
    {
        InitializeComponent();

        // Drag source — the Available list. Tunnel routing so we see the
        // pointer events before the ListBox's own selection handling
        // marks them handled. This stays on OS DnD because the user
        // could in theory drop on adjacent windows (though current chain
        // pane is intra-window; OS path is fine either way).
        _availableList = this.FindControl<ListBox>("AvailableList");
        if (_availableList != null)
        {
            _availableList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
            _availableList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        }

        // Drop target — the chain pane. Accepts the Available-list "Add"
        // drag (OS DnD). Ghost-mode reorder events route through the
        // GhostCardReorderController below, NOT through DragDrop events.
        _chainDropZone = this.FindControl<Border>("ChainDropZone");
        if (_chainDropZone != null)
        {
            _chainDropZone.AddHandler(DragDrop.DragOverEvent, OnChainDragOver);
            _chainDropZone.AddHandler(DragDrop.DragLeaveEvent, OnChainDragLeave);
            _chainDropZone.AddHandler(DragDrop.DropEvent, OnChainDrop);
        }

        // Ghost-mode chain-card reorder. We don't pass the items panel's
        // ItemTemplate to the controller because AttachedSamplerViewModel
        // exposes a SHARED plugin Control (via .Control) that the template
        // re-binds to — re-templating against the same VM would try to
        // give that plugin Control two visual parents and crash. Instead
        // we supply a header-only placeholder via buildGhostContent.
        _chainItems = this.FindControl<ItemsControl>("ChainItems");
        if (_chainItems != null)
        {
            _reorder = new GhostCardReorderController<AttachedSamplerViewModel>(
                root: this,
                getItems: () => _chainItems,
                getTemplate: () => null,
                moveVisually: (s, t) => Vm?.MoveItemVisually(s, t),
                persistOrder: () => Vm?.PersistOrder(),
                drag: new DragInitiator { MinDistance = Services.UiConstants.CardDragMinDistance },
                buildGhostContent: BuildChainCardGhost);
            _reorder.Attach(_chainItems);
        }

        // Plugin-supplied controls inside each AttachedSamplerViewModel
        // (the body of every chain card) typically host sliders. Without
        // SliderBurstBehavior, the 100 ms editor tick commits a fresh
        // debounced write on every slider drag tick — wasted DB writes
        // and momentary inconsistencies. The behavior tunnels
        // PointerPressed / PointerCaptureLost on the root and calls
        // EditPersistence.BeginBurst/EndBurst so writes only commit on
        // pointer release. Same idea Preset / Track / Playlist editors use.
        Services.SliderBurstBehavior.Attach(this, () => Vm?.Persistence);

        // Don't subscribe Unloaded here: WindowManagerService now disposes
        // ShellContent on window close AND on dedup-swap, which calls
        // SamplerEditorViewModel.Dispose (stops the 100 ms timer, flushes
        // Persistence, disposes editor instances). A view-level Unloaded
        // hook would either double-dispose (mostly benign, see Dispose)
        // or duplicate the Persistence.Flush in a way that's easy to drift
        // away from Dispose later.
    }

    /// <summary>Build a placeholder visual for the FX chain ghost. Shows
    /// the order # + plugin name + tier label — enough for the user to
    /// recognise which row they're dragging — without touching the
    /// VM's shared plugin Control instance (re-templating against the
    /// same VM would crash with "control already has a visual parent").</summary>
    private static Control BuildChainCardGhost(AttachedSamplerViewModel vm)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            Margin = new Avalonia.Thickness(15),
        };

        var orderText = new TextBlock
        {
            Text = $"#{vm.Order}",
            FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace"),
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(orderText, 0);

        var titleStack = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = vm.PluginName,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = vm.TierLabel,
            FontSize = 10,
            Opacity = 0.6,
        });
        Grid.SetColumn(titleStack, 1);

        grid.Children.Add(orderText);
        grid.Children.Add(titleStack);

        return new Border
        {
            // Safe lookup — Application.Current.FindResource returns
            // UnsetValue on a miss, which throws on a hard IBrush cast.
            // ThemeBrushes.Resolve does the TryGetResource dance with
            // the active theme variant and returns null on miss.
            Background = Services.ThemeBrushes.Resolve("PanelBackground2") ?? Avalonia.Media.Brushes.DimGray,
            CornerRadius = new Avalonia.CornerRadius(8),
            Child = grid,
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SamplerEditorViewModel? Vm => DataContext as SamplerEditorViewModel;

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_availableList == null) return;
        _drag.NotifyPressed(e, _availableList);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_availableList == null) return;
        if (!_drag.ShouldStartDrag(e, _availableList)) return;
        if (_availableList.SelectedItem is not IAudioSamplerPlugin plugin) return;
        _drag.MarkDragStarted();

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SamplerDragFormat, plugin));
        await DragDrop.DoDragDropAsync(_drag.SynthesizePressedArgs(e, _availableList), data, DragDropEffects.Copy);
    }

    private void OnChainDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(SamplerDragFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_chainDropZone != null) _chainDropZone.BorderBrush = DropHighlightBrush;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnChainDragLeave(object? sender, DragEventArgs e)
    {
        if (_chainDropZone != null) _chainDropZone.BorderBrush = Brushes.Transparent;
    }

    private void OnChainDrop(object? sender, DragEventArgs e)
    {
        if (_chainDropZone != null) _chainDropZone.BorderBrush = Brushes.Transparent;

        if (Vm == null) return;

        // Only the "Add new plugin" (SamplerDragFormat) flow comes here
        // now. Chain-item reorders route through the GhostCardReorderController
        // and never reach the DragDrop pipeline.
        var plugin = e.DataTransfer.TryGetValue(SamplerDragFormat);
        if (plugin == null) return;

        Vm.AddSampler(plugin);
        e.Handled = true;
    }
}
