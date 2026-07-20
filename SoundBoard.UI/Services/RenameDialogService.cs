using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Small modal "rename" prompt: a single text box plus Save / Cancel. Returns
/// the trimmed new name, or null if the user cancelled or left it blank.
/// Shared by the shortcut-page, shortcut-button, and playlist rename flows so
/// they look identical and there's one place to keep the styling correct.
///
/// <para>Uses a normal OS-decorated window with an opaque, theme-colored
/// background. (An earlier version set <c>ExtendClientAreaToDecorationsHint</c>
/// for a frameless look, but that composites a see-through client area on
/// Windows — the dialog and its Save button became invisible over a busy
/// page. A plain decorated window paints its background opaquely.)</para>
/// </summary>
public static class RenameDialogService
{
    public static async Task<string?> PromptAsync(Window owner, string title, string? current)
    {
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = ThemeBrushes.Resolve("ContentBackground")
                         ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
        };

        var textBox = new TextBox
        {
            Text = current ?? "",
            Margin = new Avalonia.Thickness(20, 20, 20, 10),
            FontSize = 16,
        };

        void Commit()
        {
            var t = textBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(t)) result = t;
            dialog.Close();
        }

        var saveButton = new Button
        {
            Content = "Save",
            Classes = { "primary" },
            Padding = new Avalonia.Thickness(20, 8),
        };
        saveButton.Click += (_, _) => Commit();

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(16, 8),
        };
        cancelButton.Click += (_, _) => dialog.Close();

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) dialog.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 0, 20, 20),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        // Focus the box and preselect the text so a rename is type-over.
        textBox.AttachedToVisualTree += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        await dialog.ShowDialog(owner);
        return result;
    }
}
