using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SoundBoard.UI.ViewModels;
using System.Threading.Tasks;

namespace SoundBoard.UI.Views;

/// <summary>
/// Hosts the URI builder form. The whole view is a drop target so a Track,
/// Preset, or Playlist dragged from another window auto-fills the form.
/// </summary>
public partial class UriBuilderView : UserControl
{
    private static readonly SolidColorBrush DropHighlightBrush = new(Color.FromArgb(180, 0x25, 0x63, 0xEB));

    public UriBuilderView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;
    private UriBuilderViewModel? Vm => DataContext as UriBuilderViewModel;

    private Task Copy(string text) =>
        Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    private async void OnCopyPlainClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (Vm == null) return;
            await Copy(Vm.PlainUri);
        }, "Copy plain URI");

    private async void OnCopyMarkdownClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (Vm == null) return;
            await Copy(Vm.MarkdownLink);
        }, "Copy Markdown link");

    private async void OnCopyHtmlClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (Vm == null) return;
            await Copy(Vm.HtmlLink);
        }, "Copy HTML link");

    // ── Drag-drop ────────────────────────────────────────────────────────────

    private bool AcceptsDrag(DragEventArgs e) =>
        e.DataTransfer.Contains(LibraryView.TrackDragFormat) ||
        e.DataTransfer.Contains(PresetsView.PresetDragFormat) ||
        e.DataTransfer.Contains(PlaylistsView.PlaylistDragFormat);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var zone = this.FindControl<Border>("DropZone");
        if (AcceptsDrag(e))
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

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var zone = this.FindControl<Border>("DropZone");
        if (zone != null) zone.BorderBrush = Brushes.Transparent;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var zone = this.FindControl<Border>("DropZone");
        if (zone != null) zone.BorderBrush = Brushes.Transparent;

        if (Vm == null) return;

        var track = e.DataTransfer.TryGetValue(LibraryView.TrackDragFormat);
        if (track != null) { Vm.SetItem(track); e.Handled = true; return; }

        var preset = e.DataTransfer.TryGetValue(PresetsView.PresetDragFormat);
        if (preset != null) { Vm.SetItem(preset); e.Handled = true; return; }

        var playlist = e.DataTransfer.TryGetValue(PlaylistsView.PlaylistDragFormat);
        if (playlist != null) { Vm.SetItem(playlist); e.Handled = true; }
    }
}
