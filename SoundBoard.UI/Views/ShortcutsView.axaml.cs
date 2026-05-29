using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using System.Linq;
using System.Threading.Tasks;

namespace SoundBoard.UI.Views;

/// <summary>Soundboard view (embedded in the main window). The grid of
/// shortcut buttons with drag-to-reorder, accepts drops from Library /
/// Presets / Playlists windows to spawn new buttons, and surfaces
/// per-button rename / icon-pick / remove context menus.
///
/// <para><b>Drag model.</b> Shortcut reordering uses the hybrid
/// "ghost overlay" pattern: intra-window reorder drives a snapshot
/// follower through
/// <see cref="GhostDragOverlay"/> and capture-the-pointer, NOT
/// <c>DragDrop.DoDragDropAsync</c>. The OS DnD path is reserved for
/// cross-window adds — Library / Presets / Playlists dropping into the
/// page to spawn a new button. The two paths coexist on the same
/// ItemsControl because they fire on different data formats.</para></summary>
public partial class ShortcutsView : UserControl
{
    /// <summary>Drag format retained for backwards compatibility — the
    /// PoppedShortcutPageView still uses OS DnD for its reorder gesture
    /// (Phase A converts only the main view; Phase B will fold the popped
    /// page into the ghost overlay too). Once both are converted this
    /// field can be removed.</summary>
    public static readonly DataFormat<ViewModels.ShortcutButtonViewModel> ButtonDragFormat =
        Services.DragFormats.ShortcutButton;

    private ItemsControl? _items;
    private GhostCardReorderController<ViewModels.ShortcutButtonViewModel>? _reorder;

    public ShortcutsView()
    {
        InitializeComponent();

        // Register as a drop target for cross-window OS DnD (Library /
        // Presets / Playlists → page). Shortcut reorders never travel
        // through this pipeline anymore — see the ghost controller below.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);

        // Ghost-mode reorder controller. Tunnel routing on the items
        // panel is what makes the four pointer events reach us before
        // the per-button class handlers — Button consumes PointerMoved
        // during a held press, so XAML bubble-phase wiring on the
        // Buttons themselves wouldn't ever fire.
        _items = this.FindControl<ItemsControl>("ShortcutItems");
        if (_items != null)
        {
            _reorder = new GhostCardReorderController<ViewModels.ShortcutButtonViewModel>(
                root: this,
                getItems: () => _items,
                getTemplate: () => _items?.ItemTemplate,
                moveVisually: (s, t) => Vm?.SwapButtons(s, t),
                persistOrder: () => Vm?.PersistButtonOrder());
            _reorder.Attach(_items);
        }
    }

    private ViewModels.ShortcutsViewModel? Vm => DataContext as ViewModels.ShortcutsViewModel;

    // ── Drag & Drop ──────────────────────────────────────────

    private void DragOver(object? sender, DragEventArgs e)
    {
        // Cross-window adds only. Shortcut reorders bypass DragDrop
        // entirely (ghost mode) so ButtonDragFormat never appears here.
        if (e.DataTransfer.Contains(LibraryView.TrackDragFormat) ||
            e.DataTransfer.Contains(PresetsView.PresetDragFormat) ||
            e.DataTransfer.Contains(PlaylistsView.PlaylistDragFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        if (Vm == null) { e.Handled = true; return; }

        var track = e.DataTransfer.TryGetValue(LibraryView.TrackDragFormat);
        if (track != null)
        {
            Vm.AddTrackToCurrentPage(track);
            e.Handled = true;
            return;
        }

        var preset = e.DataTransfer.TryGetValue(PresetsView.PresetDragFormat);
        if (preset != null)
        {
            Vm.AddPresetToCurrentPage(preset);
            e.Handled = true;
            return;
        }

        var playlist = e.DataTransfer.TryGetValue(PlaylistsView.PlaylistDragFormat);
        if (playlist != null)
        {
            Vm.AddPlaylistToCurrentPage(playlist);
            e.Handled = true;
        }
    }

    private void OnButtonDragOver(object? sender, DragEventArgs e)
    {
        // Per-button DragOver runs only for cross-window OS DnD now —
        // shortcut reorders bypass DragDrop entirely (ghost mode). We
        // accept tracks/presets/playlists so the user sees a "Copy"
        // cursor, but let the event bubble to the UserControl's Drop
        // handler which actually spawns the new button.
        if (e.DataTransfer.Contains(LibraryView.TrackDragFormat) ||
            e.DataTransfer.Contains(PresetsView.PresetDragFormat) ||
            e.DataTransfer.Contains(PlaylistsView.PlaylistDragFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnButtonDrop(object? sender, DragEventArgs e)
    {
        // No-op — cross-window adds are handled by the UserControl's
        // Drop. Buttons don't act as drop targets for tracks/presets/
        // playlists (that would overwrite them silently). Reorder drops
        // don't reach here anymore (ghost mode bypasses DragDrop).
    }

    // ── Page Tab Scroller ────────────────────────────────────

    // Route vertical mouse-wheel input over the page-tabs scroller to
    // horizontal scrolling. The strip is horizontal-only (no vertical
    // scroll), so the wheel would otherwise be unusable — and there's no
    // natural horizontal-wheel hardware on most desks. Trackpads with
    // genuine horizontal scroll still route through the scroller's normal
    // path because their delta arrives as Delta.X, which we leave alone.
    private void OnPageTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var dy = e.Delta.Y;
        if (dy == 0) return;
        // One "notch" of the wheel ≈ 1 unit. Multiply by a tab-ish width so
        // each notch moves about a tab's worth instead of a pixel.
        sv.Offset = new Avalonia.Vector(
            System.Math.Clamp(sv.Offset.X - dy * 60, 0, System.Math.Max(0, sv.Extent.Width - sv.Viewport.Width)),
            sv.Offset.Y);
        e.Handled = true;
    }

    // ── Page Tab Context Menu ────────────────────────────────

    private async void OnPageTabDoubleTapped(object? sender, TappedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (sender is TextBlock tb && tb.DataContext is ShortcutPage page)
                await PromptRenamePageAsync(page);
        }, "Rename page");

    private async void OnRenamePageClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (sender is MenuItem mi && mi.DataContext is ShortcutPage page)
                await PromptRenamePageAsync(page);
        }, "Rename page");

    private async Task PromptRenamePageAsync(ShortcutPage page)
    {
        if (Vm == null) return;

        // Create a simple inline rename dialog
        var dialog = new Window
        {
            Title = "Rename Page",
            Width = 350,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.Transparent,
            ExtendClientAreaToDecorationsHint = true,
        };

        var textBox = new TextBox
        {
            Text = page.Name,
            Margin = new Thickness(20, 20, 20, 10),
            FontSize = 16,
        };

        var saveButton = new Button
        {
            Content = "Save",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 20, 20),
            Padding = new Thickness(20, 8),
        };

        saveButton.Click += (s, args) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text))
            {
                Vm.RenamePageDirect(page.Id, textBox.Text.Trim());
            }
            dialog.Close();
        };

        textBox.KeyDown += (s, args) =>
        {
            if (args.Key == Key.Enter && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                Vm.RenamePageDirect(page.Id, textBox.Text.Trim());
                dialog.Close();
            }
            else if (args.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(saveButton);
        dialog.Content = panel;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private void OnDeletePageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ShortcutPage page)
        {
            Vm?.DeletePageDirect(page.Id);
        }
    }

    // ── Button Context Menu ──────────────────────────────────

    private async void OnRenameButtonClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (sender is not MenuItem mi || mi.DataContext is not ViewModels.ShortcutButtonViewModel btnVm) return;
            if (Vm == null) return;

            var dialog = new Window
            {
                Title = "Rename Button",
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Transparent,
                ExtendClientAreaToDecorationsHint = true,
            };

            var textBox = new TextBox
            {
                Text = btnVm.Label ?? "",
                Margin = new Thickness(20, 20, 20, 10),
                FontSize = 16,
            };

            var saveButton = new Button
            {
                Content = "Save",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 20, 20),
                Padding = new Thickness(20, 8),
            };

            saveButton.Click += (s, args) =>
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                    Vm.RenameButtonDirect(btnVm.ModelId, textBox.Text.Trim());
                dialog.Close();
            };

            textBox.KeyDown += (s, args) =>
            {
                if (args.Key == Key.Enter && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    Vm.RenameButtonDirect(btnVm.ModelId, textBox.Text.Trim());
                    dialog.Close();
                }
                else if (args.Key == Key.Escape)
                {
                    dialog.Close();
                }
            };

            var panel = new StackPanel();
            panel.Children.Add(textBox);
            panel.Children.Add(saveButton);
            dialog.Content = panel;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner != null) await dialog.ShowDialog(owner);
            else                dialog.Show();
        }, "Rename button");

    private void OnRemoveButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ViewModels.ShortcutButtonViewModel btnVm)
        {
            Vm?.RemoveButtonDirect(btnVm.ModelId);
        }
    }

    private async void OnSetButtonIconClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (sender is not MenuItem mi || mi.DataContext is not ViewModels.ShortcutButtonViewModel btnVm) return;
            if (Vm == null) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            var result = await Services.IconPickerService.PickAsync(owner, btnVm.ButtonModel.Icon);
            Vm.SetButtonIconDirect(btnVm.ModelId, result.Icon);
        }, "Set button icon");

    private void OnConfigureShortcutSamplersClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not ViewModels.ShortcutButtonViewModel btnVm) return;
        if (Vm == null) return;
        Vm.OpenSamplerEditorFor(btnVm);
    }

    /// <summary>Open a small dialog to set/clear the bus override for a
    /// Track-targeting shortcut. The combobox shows "(Inherit from track)"
    /// as the null option plus every configured bus. On Save the VM
    /// persists the new value; the menu item is only visible for Track
    /// targets (Preset / Playlist shortcuts ignore the field at play
    /// time per the design spec).</summary>
    private async void OnSetButtonBusOverrideClicked(object? sender, RoutedEventArgs e)
        => await SoundBoard.UI.Services.UiOps.RunGuarded(async () =>
        {
            if (sender is not MenuItem mi || mi.DataContext is not ViewModels.ShortcutButtonViewModel btnVm) return;
            if (Vm == null) return;

            var buses = Vm.ListBuses();
            // Build the choice list: (null, "(Inherit from track)") + every bus.
            var choices = new System.Collections.Generic.List<(int? Id, string Label)>
            {
                (null, "(Inherit from track)")
            };
            foreach (var b in buses) choices.Add((b.Id, b.Name));

            var combo = new ComboBox
            {
                Margin = new Thickness(20, 20, 20, 10),
                ItemsSource = choices,
                DisplayMemberBinding = new Avalonia.Data.Binding("Label"),
                SelectedValueBinding = new Avalonia.Data.Binding("Id"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            // Pre-select the current value.
            var current = Vm.GetButtonBusOverride(btnVm.ModelId);
            combo.SelectedIndex = 0;
            for (int i = 0; i < choices.Count; i++)
            {
                if (choices[i].Id == current) { combo.SelectedIndex = i; break; }
            }

            var saveButton = new Button
            {
                Content = "Save",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 20, 20),
                Padding = new Thickness(20, 8),
            };

            var dialog = new Window
            {
                Title = "Bus Override",
                Width = 380,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Transparent,
                ExtendClientAreaToDecorationsHint = true,
            };

            saveButton.Click += (s, args) =>
            {
                if (combo.SelectedItem is System.ValueTuple<int?, string> picked)
                    Vm.SetButtonBusOverrideDirect(btnVm.ModelId, picked.Item1);
                dialog.Close();
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Force this shortcut's track through a specific bus, or leave as 'Inherit'.",
                Margin = new Thickness(20, 20, 20, 4),
                FontSize = 12,
            });
            panel.Children.Add(combo);
            panel.Children.Add(saveButton);
            dialog.Content = panel;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner != null) await dialog.ShowDialog(owner);
            else                dialog.Show();
        }, "Set button bus override");
}
