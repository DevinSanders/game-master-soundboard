using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SoundBoard.Core.Models;
using SoundBoard.UI.Controls;
using SoundBoard.UI.Services;
using System.Linq;

namespace SoundBoard.UI.Views;

/// <summary>Library window view. Owns the drag-source plumbing for tracks
/// (the <see cref="TrackDragFormat"/> data is picked up by Soundboard /
/// Playlist / Preset / URI Builder drop targets) plus the file-picker and
/// in-place editing event handlers.</summary>
public partial class LibraryView : UserControl
{
    /// <summary>In-process data format used when a track row is dragged out
    /// of the library — picked up by other windows' drop targets to create
    /// new shortcuts, playlist items, preset entries, or URI activations.</summary>
    // Forwarded to the central registry (Services.DragFormats) so MIME
    // strings live in one place. Callers that already say
    // LibraryView.TrackDragFormat keep working.
    public static readonly DataFormat<Track> TrackDragFormat = Services.DragFormats.Track;

    private readonly Services.DragInitiator _drag = new();

    /// <summary>The track under the pointer at the last press. Captured
    /// directly from the pressed row's DataContext rather than read back
    /// from the DataGrid's selection — see <see cref="OnGridPointerPressed"/>
    /// for why the selection alone is unreliable.</summary>
    private Track? _pressedTrack;

    public LibraryView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);

        // Wire up drag initiation from the DataGrid
        TrackGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TrackGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _drag.NotifyPressed(e, TrackGrid);

        // Resolve the track from the row physically under the pointer and
        // make it the selection — for BOTH mouse buttons. Two DataGrid
        // behaviours make relying on SelectedItem alone unsafe here:
        //  1. Right-click does NOT select a row, so the "Send to Soundboard"
        //     context menu would otherwise act on a stale SelectedTrack.
        //  2. With an active filter/sort the grid's SelectedItem can lag the
        //     visually-clicked row, so even a left-click drag could carry the
        //     wrong track.
        // Reading the pressed row's own DataContext sidesteps both, so every
        // shortcut-creation path targets exactly the row the user clicked.
        _pressedTrack = ResolveRowTrack(e.Source);
        if (_pressedTrack != null && DataContext is ViewModels.LibraryViewModel vm)
            vm.SelectedTrack = _pressedTrack;
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_drag.ShouldStartDrag(e, TrackGrid)) return;
        var track = _pressedTrack ?? TrackGrid.SelectedItem as Track;
        if (track == null) return;
        _drag.MarkDragStarted();

        var dragData = new DataTransfer();
        dragData.Add(DataTransferItem.Create(TrackDragFormat, track));
        await DragDrop.DoDragDropAsync(_drag.SynthesizePressedArgs(e, TrackGrid), dragData, DragDropEffects.Copy);
    }

    /// <summary>Walk up from the hit visual to the owning
    /// <see cref="DataGridRow"/> and return its bound <see cref="Track"/>,
    /// or null if the press landed outside a data row (header, empty area).</summary>
    private static Track? ResolveRowTrack(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is DataGridRow row)
                return row.DataContext as Track;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    // File drop import (external drag-and-drop of audio files INTO the library)
    private void DragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files != null && files.Length > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files != null && DataContext is ViewModels.LibraryViewModel vm)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            vm.ImportFiles(paths);
        }
    }

    // Open the unified Add-Track modal. Hosts the dialog VM in an
    // AppWindow, ShowDialog against the LibraryView's TopLevel so it
    // stays modal, then on confirm hands the captured values to the VM
    // for insertion. The VM's CreateAddTrackVm factory wires in the
    // file-service (for the Browse button) and the codec-support
    // predicate (for URI validation) without leaking either out as
    // public dialog construction surface.
    private async void OnAddTrackClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (DataContext is not ViewModels.LibraryViewModel vm) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;

            var dialogVm = vm.CreateAddTrackVm();
            var dialog = new AppWindow
            {
                Title = "Add Track",
                Width = 620,
                Height = 420,
                ShellContent = dialogVm,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            dialogVm.Closed += () => dialog.Close();
            await dialog.ShowDialog(owner);

            if (dialogVm.Result is { } result)
                vm.AddTrack(result.Uri, result.Name, result.Tags);
        }, "Add Track");
}
