using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SoundBoard.Core.Models;
using SoundBoard.UI.Controls;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Views;

/// <summary>Playlists window view. Owns the drag sources for whole
/// playlists and the per-item drag-reorder logic, plus the drop targets
/// that accept tracks and presets dragged in from other windows.</summary>
public partial class PlaylistsView : UserControl
{
    private static readonly SolidColorBrush DropHighlightBrush = new(Color.FromArgb(180, 0x25, 0x63, 0xEB));

    /// <summary>Drag format retained for back-compat; the current
    /// ghost-mode reorder doesn't put this in a DataTransfer.</summary>
    public static readonly DataFormat<PlaylistEditorItemViewModel> PlaylistItemDragFormat =
        Services.DragFormats.PlaylistItem;

    /// <summary>Drag format used when a playlist row is dragged out of this view
    /// (e.g. onto the URI Builder window).</summary>
    public static readonly DataFormat<Playlist> PlaylistDragFormat =
        Services.DragFormats.Playlist;

    private readonly DragInitiator _playlistDrag = new();
    private ListBox? _playlistList;
    private ItemsControl? _itemRows;
    private GhostCardReorderController<PlaylistEditorItemViewModel>? _reorder;

    public PlaylistsView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnPaneDragOver);
        AddHandler(DragDrop.DropEvent, OnPaneDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnPaneDragLeave);

        // Cross-window drag SOURCE: playlist rows can be dragged out
        // onto the URI Builder window. Stays on OS DnD (cross-window).
        _playlistList = this.FindControl<ListBox>("PlaylistList");
        if (_playlistList != null)
        {
            _playlistList.AddHandler(PointerPressedEvent, OnPlaylistListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _playlistList.AddHandler(PointerMovedEvent, OnPlaylistListPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        // Intra-window item reorder — ghost mode (DPI-correct).
        _itemRows = this.FindControl<ItemsControl>("ItemRows");
        if (_itemRows != null)
        {
            _reorder = new GhostCardReorderController<PlaylistEditorItemViewModel>(
                root: this,
                getItems: () => _itemRows,
                getTemplate: () => _itemRows?.ItemTemplate,
                moveVisually: (s, t) => Vm?.MoveItem(s, t),
                persistOrder: () => Vm?.PersistOrder());
            _reorder.Attach(_itemRows);
        }

        SliderBurstBehavior.Attach(this, () => Vm?.Persistence);
        Unloaded += (s, e) => Vm?.Persistence.Flush();
    }

    private void OnPlaylistListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_playlistList == null) return;
        _playlistDrag.NotifyPressed(e, _playlistList);
    }

    private async void OnPlaylistListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_playlistList == null) return;
        if (!_playlistDrag.ShouldStartDrag(e, _playlistList)) return;
        if (_playlistList.SelectedItem is not Playlist playlist) return;
        _playlistDrag.MarkDragStarted();

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PlaylistDragFormat, playlist));
        await DragDrop.DoDragDropAsync(_playlistDrag.SynthesizePressedArgs(e, _playlistList), data, DragDropEffects.Copy);
    }

    private PlaylistsViewModel? Vm => DataContext as PlaylistsViewModel;

    // ── Pane-level drop (Track / Preset from other windows) ──────────────────

    private bool PaneAccepts(DragEventArgs e) =>
        e.DataTransfer.Contains(LibraryView.TrackDragFormat) ||
        e.DataTransfer.Contains(PresetsView.PresetDragFormat);

    private void OnPaneDragOver(object? sender, DragEventArgs e)
    {
        // Item-level handlers set Handled=true first when reordering — only
        // run pane-level logic for cross-window drops.
        if (e.Handled) return;

        var dropZone = this.FindControl<Border>("DropZone");
        if (PaneAccepts(e) && Vm?.SelectedPlaylist != null)
        {
            e.DragEffects = DragDropEffects.Copy;
            if (dropZone != null) dropZone.BorderBrush = DropHighlightBrush;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnPaneDragLeave(object? sender, DragEventArgs e)
    {
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone != null) dropZone.BorderBrush = Brushes.Transparent;
    }

    private void OnPaneDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;

        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone != null) dropZone.BorderBrush = Brushes.Transparent;

        if (Vm == null || Vm.SelectedPlaylist == null) return;

        var track = e.DataTransfer.TryGetValue(LibraryView.TrackDragFormat);
        if (track != null)
        {
            Vm.AddTrack(track);
            e.Handled = true;
            return;
        }

        var preset = e.DataTransfer.TryGetValue(PresetsView.PresetDragFormat);
        if (preset != null)
        {
            Vm.AddPreset(preset);
            e.Handled = true;
        }
    }

    // Item-level reorder is handled by GhostCardReorderController wired
    // in the constructor — no per-row XAML handlers needed.

    private async void OnPickPlaylistIconClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || Vm.SelectedPlaylist == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            var result = await IconPickerService.PickAsync(owner, Vm.SelectedPlaylist.Icon);
            Vm.SetIconForSelected(result.Icon);
        }, "Pick playlist icon");
}
