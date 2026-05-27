using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SoundBoard.Core.Logging;
using SoundBoard.UI.Controls;
using System;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Helpers for code-behind event handlers that invoke async work. The
/// <c>async void</c> pattern Avalonia event signatures force on us has a
/// nasty failure mode: any unhandled exception escapes to the dispatcher
/// and the user sees nothing except a log line. <see cref="RunGuarded"/>
/// wraps the body in try/catch, logs the failure, and surfaces a small
/// error dialog so the user knows something went wrong.
///
/// Use for destructive or user-visible operations (Save, Delete, Import).
/// Cosmetic handlers (drag previews, hover effects) can stay bare —
/// failures there are typically harmless and a dialog would be more
/// annoying than the bug.
/// </summary>
public static class UiOps
{
    /// <summary>Run an async operation and surface any exception as a
    /// modal error dialog. Always returns; never throws.</summary>
    public static async Task RunGuarded(Func<Task> body, string operationName)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Log.Error("UI", $"{operationName} failed", ex);
            await Dispatcher.UIThread.InvokeAsync(() => ShowError(operationName, ex));
        }
    }

    private static async Task ShowError(string operationName, Exception ex)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null) return;

        var dialog = new AppWindow
        {
            Title = $"{operationName} failed",
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(20, 6),
        };
        ok.Click += (_, __) => dialog.Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
        };
        panel.Children.Add(new TextBlock
        {
            Text = operationName + " did not complete.",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 16,
        });
        panel.Children.Add(new TextBlock
        {
            Text = ex.Message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.85,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "See the debug log for details.",
            FontSize = 11,
            Opacity = 0.6,
        });
        panel.Children.Add(ok);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
    }
}
