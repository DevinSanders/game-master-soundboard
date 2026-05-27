using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SoundBoard.UI.Controls;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Views;

/// <summary>Preset editor view. Hosts the card-per-track UI with override
/// sliders (volume, fades, start delay) and the live-edit hooks that push
/// changes to a running preset instance without a save round-trip.
/// Card reorder uses the ghost overlay (<see cref="GhostCardReorderController{TCardVm}"/>);
/// cross-window drops (Library Track → pane) still use OS DnD.</summary>
public partial class PresetEditorView : UserControl
{
    // Pulls the live theme brush so a palette swap follows. Falls back to
    // the historical hex if Application.Current isn't available (tests).
    private static IBrush DropHighlightBrush => Services.ThemeBrushes.DropZoneHighlight;

    /// <summary>Drag format retained for back-compat (pre-Phase-B callers).
    /// The current ghost-mode reorder does not put this in a DataTransfer —
    /// it captures the pointer and drives the overlay directly.</summary>
    public static readonly DataFormat<PresetTrackCardViewModel> CardDragFormat =
        Services.DragFormats.PresetCard;

    private ItemsControl? _cardItems;
    private GhostCardReorderController<PresetTrackCardViewModel>? _reorder;

    public PresetEditorView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnPaneDragOver);
        AddHandler(DragDrop.DropEvent, OnPaneDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnPaneDragLeave);

        // Suspend the debounced auto-save while the user is dragging a slider,
        // and flush on release. Pull the live VM via a lambda so the binding
        // is resilient to DataContext swaps.
        SliderBurstBehavior.Attach(this, () => Vm?.Persistence);

        _cardItems = this.FindControl<ItemsControl>("CardItems");
        if (_cardItems != null)
        {
            _reorder = new GhostCardReorderController<PresetTrackCardViewModel>(
                root: this,
                getItems: () => _cardItems,
                getTemplate: () => _cardItems?.ItemTemplate,
                moveVisually: (s, t) => Vm?.MoveCard(s, t),
                persistOrder: () => Vm?.PersistOrder());
            _reorder.Attach(_cardItems);
        }

        // Window-close safety net: flush any pending debounced writes AND
        // dispose the VM so its CollectionChanged subscription on the
        // engine's ActiveItems unhooks. Pre-fix the handler stayed wired
        // forever and every editor close leaked the previous VM.
        Unloaded += (s, e) => Vm?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private PresetEditorViewModel? Vm => DataContext as PresetEditorViewModel;

    // ── Pane-level drop (Track from Library) ─────────────────────────────────

    private void OnPaneDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var zone = this.FindControl<Border>("DropZone");
        if (e.DataTransfer.Contains(LibraryView.TrackDragFormat) && Vm?.Preset != null)
        {
            e.DragEffects = DragDropEffects.Copy;
            if (zone != null) zone.BorderBrush = DropHighlightBrush;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnPaneDragLeave(object? sender, DragEventArgs e)
    {
        var zone = this.FindControl<Border>("DropZone");
        if (zone != null) zone.BorderBrush = Brushes.Transparent;
    }

    private void OnPaneDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var zone = this.FindControl<Border>("DropZone");
        if (zone != null) zone.BorderBrush = Brushes.Transparent;

        if (Vm == null || Vm.Preset == null) return;
        var track = e.DataTransfer.TryGetValue(LibraryView.TrackDragFormat);
        if (track != null)
        {
            Vm.AddTrack(track);
            e.Handled = true;
        }
    }

    // Card-level drag-to-reorder is handled by the GhostCardReorderController
    // wired in the constructor — no per-card XAML handlers needed.

    private async void OnPickIconClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || Vm.Preset == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            var result = await IconPickerService.PickAsync(owner, Vm.Preset.Icon);
            Vm.SetIcon(result.Icon);
        }, "Pick preset icon");
}
