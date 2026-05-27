using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.UI.Controls;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundBoard.UI.Views;

/// <summary>Settings window view. Hosts the device picker, library
/// management section (import/export, libraries list, create/open), the
/// embedded Discord-bot panel, and the installed-plugins list.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Wire the plugin-install drop zone. Avalonia's DragDrop events
        // are routed, so attaching via AddHandler lets us see DragEnter
        // / DragOver / Drop fired on the Border regardless of which
        // child visual the pointer is over.
        var dropZone = this.FindControl<Border>("PluginInstallDropZone");
        if (dropZone != null)
        {
            DragDrop.SetAllowDrop(dropZone, true);
            dropZone.AddHandler(DragDrop.DragEnterEvent, OnPluginZipDragEnter);
            dropZone.AddHandler(DragDrop.DragOverEvent, OnPluginZipDragOver);
            dropZone.AddHandler(DragDrop.DragLeaveEvent, OnPluginZipDragLeave);
            dropZone.AddHandler(DragDrop.DropEvent, OnPluginZipDrop);
        }

        // Populate the Bridges section once the visual tree is up. Bridges
        // come from PluginService; each one hands us its own Avalonia
        // Control via CreateSettingsControl. We wrap each in a header +
        // card so they all share the same panel chrome.
        AttachedToVisualTree += (_, _) => PopulateBridges();
    }

    private void PopulateBridges()
    {
        var panel = this.FindControl<StackPanel>("BridgesPanel");
        var emptyHint = this.FindControl<TextBlock>("BridgesEmptyHint");
        if (panel == null) return;

        var services = App.Current?.Services;
        if (services == null) return;

        var pluginService = services.GetService<IPluginService>();
        var bridgeHost = services.GetService<AudioBridgeHost>();
        if (pluginService == null || bridgeHost == null) return;

        panel.Children.Clear();
        var bridges = pluginService.BridgePlugins.ToList();
        if (bridges.Count == 0)
        {
            if (emptyHint != null) emptyHint.IsVisible = true;
            return;
        }
        if (emptyHint != null) emptyHint.IsVisible = false;

        foreach (var bridge in bridges)
        {
            var context = pluginService.GetContextFor(bridge);
            var hostHandle = bridgeHost.GetHostFor(bridge);
            if (context == null || hostHandle == null) continue;

            Control bridgeControl;
            try
            {
                bridgeControl = (Control)bridge.CreateSettingsControl(hostHandle, context);
            }
            catch (Exception ex)
            {
                bridgeControl = new TextBlock
                {
                    Text = $"Bridge '{bridge.Name}' failed to build its settings UI: {ex.Message}",
                    Foreground = ThemeBrushes.Resolve("DangerForeground") ?? Brushes.Red,
                    TextWrapping = TextWrapping.Wrap,
                };
            }

            panel.Children.Add(BuildBridgeCard(bridge, bridgeControl));
        }
    }

    private static Control BuildBridgeCard(IAudioBridgePlugin bridge, Control body)
    {
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = $"🔌 {bridge.Name}",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrushes.Resolve("TextPrimary") ?? Brushes.Black,
        });
        if (!string.IsNullOrEmpty(bridge.Description))
        {
            header.Children.Add(new TextBlock
            {
                Text = bridge.Description,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.Resolve("TextSecondary") ?? Brushes.Gray,
            });
        }

        var divider = new Border
        {
            Height = 1,
            Background = ThemeBrushes.Resolve("TextSecondary") ?? Brushes.Gray,
            Opacity = 0.15,
            Margin = new Thickness(0, 8, 0, 8),
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(divider);
        stack.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrushes.Resolve("SubtleBorder") ?? Brushes.Gray,
            Padding = new Thickness(15),
            Child = stack,
        };
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    // Library operations are user-destructive (creating, switching, importing
    // can wipe in-progress edits, change which DB is open, etc.). They run
    // under UiOps.RunGuarded so any failure surfaces a dialog instead of
    // silently logging and leaving the user wondering why nothing happened.

    private async void OnCreateLibraryClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || OwnerWindow == null) return;
            var name = await Dialogs.PromptForTextAsync(
                OwnerWindow,
                "Create new library",
                "Enter a name for the new library. It will be saved in your Application Data folder.",
                placeholder: "e.g. Campaign Two");
            if (string.IsNullOrWhiteSpace(name)) return;
            Vm.CreateLibrary(name);
        }, "Create library");

    private async void OnOpenLibraryClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || OwnerWindow == null) return;
            var libraries = Vm.AvailableLibraries;
            if (libraries.Count == 0)
            {
                Vm.DataStatus = "No libraries found yet — use 'Create new library' first.";
                return;
            }

            var picked = await Dialogs.PickFromListAsync(
                OwnerWindow,
                "Open library",
                "Pick a library to switch to. Switching takes effect after restarting the app.",
                libraries,
                row => row.Name);

            if (picked != null) Vm.OpenLibrary(picked);
        }, "Open library");

    private async void OnImportLibraryClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || OwnerWindow == null) return;

            var paths = await Vm.PickImportFileAsync();
            var path = paths?.FirstOrDefault();
            if (string.IsNullOrEmpty(path)) return;

            var optionsVm = Vm.CreateImportOptionsVm(path);
            var dialog = new AppWindow
            {
                Title = "Import options",
                Width = 620,
                Height = 760,
                ShellContent = optionsVm,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            optionsVm.Closed += () => dialog.Close();
            await dialog.ShowDialog(OwnerWindow);

            if (optionsVm.Result != null)
            {
                await Vm.RunImportAsync(path, optionsVm.Result);
            }
        }, "Import library");

    // ── Plugin installer drop zone ─────────────────────────────────────
    // Highlights the drop zone while a candidate is hovered, accepts the
    // drop, and asks the VM to install each .zip. Highlight brush is
    // pulled from the theme via Services.ThemeBrushes so a palette swap
    // follows.

    private static bool DragHasZips(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return false;
        foreach (var item in e.DataTransfer.Items)
        {
            var file = item.TryGetFile();
            if (file != null && file.Name.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnPluginZipDragEnter(object? sender, DragEventArgs e)
    {
        if (sender is not Border zone) return;
        if (DragHasZips(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            zone.BorderBrush = ThemeBrushes.DropZoneHighlight;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnPluginZipDragOver(object? sender, DragEventArgs e) => OnPluginZipDragEnter(sender, e);

    private void OnPluginZipDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border zone)
            zone.BorderBrush = ThemeBrushes.Resolve("SubtleBorder") ?? Brushes.Gray;
    }

    private async void OnPluginZipDrop(object? sender, DragEventArgs e)
    {
        if (sender is Border zone)
            zone.BorderBrush = ThemeBrushes.Resolve("SubtleBorder") ?? Brushes.Gray;

        if (Vm == null) return;
        if (!DragHasZips(e)) return;

        // Resolve every dropped file to a real path. Avalonia DnD gives
        // us IStorageFile wrappers; .TryGetLocalPath() converts to a
        // disk path when the source is a local file (URL drops would
        // return null and we skip them).
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            var file = item.TryGetFile();
            if (file == null) continue;
            if (!file.Name.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase)) continue;
            var local = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local)) paths.Add(local);
        }

        if (paths.Count > 0)
            await Vm.InstallPluginsFromZipsAsync(paths);

        e.Handled = true;
    }

    private async void OnBrowsePluginZipsClicked(object? sender, RoutedEventArgs e)
        => await UiOps.RunGuarded(async () =>
        {
            if (Vm == null || OwnerWindow == null) return;
            var picked = await Vm.PickPluginZipsAsync();
            if (picked.Count > 0) await Vm.InstallPluginsFromZipsAsync(picked);
        }, "Install plugin");
}
