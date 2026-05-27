using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SoundBoard.UI.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Small modal dialog helpers — text prompt and single-pick list. Built ad-hoc
/// in code rather than as XAML views because they don't need data-binding,
/// commands, or sustainability beyond one-shot input.
/// </summary>
public static class Dialogs
{
    /// <summary>Ask the user for a string. Returns null if they cancel.</summary>
    public static async Task<string?> PromptForTextAsync(Window owner, string title, string prompt, string initial = "", string placeholder = "")
    {
        var dialog = new AppWindow
        {
            Title = title,
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Avalonia.Thickness(20, 16, 20, 8),
        };
        var textBox = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            Margin = new Avalonia.Thickness(20, 0, 20, 10),
            FontSize = 14,
        };
        var okButton = new Button
        {
            Content = "OK",
            Classes = { "primary" },
            Padding = new Avalonia.Thickness(20, 8),
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(20, 8),
            IsCancel = true,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(20, 0, 20, 16),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        string? result = null;
        void Submit() { result = textBox.Text; dialog.Close(); }

        okButton.Click += (_, _) => Submit();
        cancelButton.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
            else if (e.Key == Key.Escape) dialog.Close();
        };

        var panel = new StackPanel();
        panel.Children.Add(promptText);
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);
        dialog.ShellContent = panel;

        textBox.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Pick one option from a list. Returns null if cancelled.</summary>
    public static async Task<T?> PickFromListAsync<T>(
        Window owner, string title, string prompt,
        IEnumerable<T> options, System.Func<T, string> labelFor) where T : class
    {
        var dialog = new AppWindow
        {
            Title = title,
            Width = 460,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Avalonia.Thickness(20, 16, 20, 8),
        };
        var list = new ListBox
        {
            Margin = new Avalonia.Thickness(20, 0, 20, 12),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(1),
            ItemTemplate = new FuncDataTemplate<T>((item, _) =>
            {
                var tb = new TextBlock { Padding = new Avalonia.Thickness(6, 8) };
                if (item != null) tb.Text = labelFor(item);
                return tb;
            }),
        };
        list.ItemsSource = options;

        var okButton = new Button
        {
            Content = "Open",
            Classes = { "primary" },
            Padding = new Avalonia.Thickness(20, 8),
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(20, 8),
            IsCancel = true,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(20, 0, 20, 16),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        T? result = null;
        void Submit()
        {
            if (list.SelectedItem is T picked) { result = picked; dialog.Close(); }
        }

        okButton.Click += (_, _) => Submit();
        cancelButton.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => Submit();

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(promptText, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(promptText);
        grid.Children.Add(list);
        grid.Children.Add(buttons);
        dialog.ShellContent = grid;

        await dialog.ShowDialog(owner);
        return result;
    }
}
