using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Small hand-built modal color picker — a live preview swatch, a hex entry
/// box, a row of preset swatches, and "Use theme default" / Save / Cancel.
/// Mirrors <see cref="IconPickerService"/> in shape so the shortcut context
/// menu can set an icon color and a button color the same way it sets a glyph.
///
/// <para>Built programmatically (no XAML view) to match the app's other
/// hand-built dialogs and to avoid pulling in the separate
/// Avalonia.Controls.ColorPicker package.</para>
/// </summary>
public static class ColorPickerService
{
    /// <summary>Outcome: <c>Changed</c> is true only if the user committed a
    /// value different from <paramref name="current"/>; <c>Color</c> is the
    /// new hex string, or null for "use the theme default".</summary>
    public sealed class Result
    {
        public bool Changed { get; init; }
        public string? Color { get; init; }
    }

    // The client area is extended under the OS chrome for a frameless look,
    // so content reserves this top gutter to clear the caption region.
    private const double CaptionGutter = 30;

    private static readonly string[] Presets =
    {
        "#E23636", "#E8833A", "#F2C14E", "#55B85A", "#3AA6A6", "#2563EB",
        "#4F46E5", "#8B5CF6", "#EC4899", "#FFFFFF", "#94A3B8", "#1E1E1E",
    };

    public static async Task<Result> PickAsync(Window owner, string title, string? current)
    {
        string? chosen = current;
        bool committed = false;

        var preview = new Border
        {
            Height = 40,
            CornerRadius = new Avalonia.CornerRadius(6),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            Margin = new Avalonia.Thickness(20, CaptionGutter, 20, 6),
        };

        var hexBox = new TextBox
        {
            PlaceholderText = "#RRGGBB or #AARRGGBB",
            Text = current ?? "",
            Margin = new Avalonia.Thickness(20, 0, 20, 6),
        };

        void ApplyPreview(string? hex)
        {
            preview.Background = !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c)
                ? new SolidColorBrush(c)
                : Brushes.Transparent;
        }
        ApplyPreview(current);
        hexBox.TextChanged += (_, _) => ApplyPreview(hexBox.Text);

        var swatches = new WrapPanel { Margin = new Avalonia.Thickness(20, 0, 20, 10) };
        foreach (var p in Presets)
        {
            if (!Color.TryParse(p, out var pc)) continue;
            var sw = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Avalonia.Thickness(2),
                Padding = new Avalonia.Thickness(0),
                Background = new SolidColorBrush(pc),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Avalonia.Thickness(1),
            };
            var hex = p;
            ToolTip.SetTip(sw, hex);
            sw.Click += (_, _) => hexBox.Text = hex;
            swatches.Children.Add(sw);
        }

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            ExtendClientAreaToDecorationsHint = true,
        };

        void Commit(string? value)
        {
            chosen = value;
            committed = true;
            dialog.Close();
        }

        var defaultBtn = new Button
        {
            Content = "Use theme default",
            Padding = new Avalonia.Thickness(12, 8),
        };
        defaultBtn.Click += (_, _) => Commit(null);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(12, 8),
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        var saveBtn = new Button
        {
            Content = "Save",
            Classes = { "primary" },
            Padding = new Avalonia.Thickness(16, 8),
        };
        void TryCommitHex()
        {
            var t = hexBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(t) && Color.TryParse(t, out _)) Commit(t);
        }
        saveBtn.Click += (_, _) => TryCommitHex();

        hexBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) TryCommitHex();
            else if (args.Key == Key.Escape) dialog.Close();
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(20, 0, 20, 20),
        };
        buttonRow.Children.Add(defaultBtn);
        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(saveBtn);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a preset, or type a hex value.",
            Margin = new Avalonia.Thickness(20, 0, 20, 4),
            FontSize = 12,
        });
        panel.Children.Add(preview);
        panel.Children.Add(hexBox);
        panel.Children.Add(swatches);
        panel.Children.Add(buttonRow);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);

        var changed = committed && chosen != current;
        return new Result { Changed = changed, Color = chosen };
    }
}
