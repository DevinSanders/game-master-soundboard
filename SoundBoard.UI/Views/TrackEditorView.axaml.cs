using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;
using System;
using System.ComponentModel;

namespace SoundBoard.UI.Views;

/// <summary>Track Editor view. Hosts the waveform preview, range selector,
/// fade / loop / start-delay sliders, icon-pick button, and the slot for
/// plugin UI extensions placed at the track editor.</summary>
public partial class TrackEditorView : UserControl
{
    public TrackEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Suspend the debounced save while a slider is being dragged; flush on release.
        SliderBurstBehavior.Attach(this, () => (DataContext as TrackEditorViewModel)?.Persistence);

        // Flush any pending writes on close so the user can't lose edits.
        Unloaded += (s, e) => (DataContext as TrackEditorViewModel)?.Persistence.Flush();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TrackEditorViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateCursor(vm.IsBusy);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackEditorViewModel.IsBusy) && DataContext is TrackEditorViewModel vm)
        {
            UpdateCursor(vm.IsBusy);
        }
    }

    private void UpdateCursor(bool isBusy)
    {
        Cursor = isBusy ? new Cursor(StandardCursorType.Wait) : null;
    }

    private async void OnPickIconClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (DataContext is not TrackEditorViewModel vm || vm.Track == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;

            var result = await IconPickerService.PickAsync(owner, vm.Track.Icon);
            // Closing the dialog without picking returns the original — only update on a real change.
            vm.SetIcon(result.Icon);
        }, "Pick track icon");
}
