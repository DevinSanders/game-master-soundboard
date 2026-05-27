using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SoundBoard.Core.Models;

namespace SoundBoard.UI.Views;

/// <summary>Presets window view. Lists every preset with inline play/stop
/// buttons and a drag source so a preset can be dropped onto the soundboard
/// or a playlist.</summary>
public partial class PresetsView : UserControl
{
    /// <summary>Drag format used when a preset is dragged out of this view
    /// (e.g. dropped onto the Playlists window).</summary>
    public static readonly DataFormat<Preset> PresetDragFormat = Services.DragFormats.Preset;

    private readonly Services.DragInitiator _drag = new();
    private ListBox? _presetList;

    public PresetsView()
    {
        InitializeComponent();
        _presetList = this.FindControl<ListBox>("PresetList");
        if (_presetList != null)
        {
            _presetList.AddHandler(PointerPressedEvent, OnListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _presetList.AddHandler(PointerMovedEvent, OnListPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_presetList == null) return;
        _drag.NotifyPressed(e, _presetList);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_presetList == null) return;
        if (!_drag.ShouldStartDrag(e, _presetList)) return;
        if (_presetList.SelectedItem is not Preset preset) return;
        _drag.MarkDragStarted();

        var dragData = new DataTransfer();
        dragData.Add(DataTransferItem.Create(PresetDragFormat, preset));
        await DragDrop.DoDragDropAsync(_drag.SynthesizePressedArgs(e, _presetList), dragData, DragDropEffects.Copy);
    }
}
