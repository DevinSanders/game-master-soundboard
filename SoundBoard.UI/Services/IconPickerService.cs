using Avalonia.Controls;
using SoundBoard.UI.Controls;
using SoundBoard.UI.ViewModels;
using SoundBoard.UI.Views;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Convenience helper: show the icon picker as a modal dialog and await the
/// selection. <c>null</c> result means "clear icon" — the cancel path returns
/// whatever icon was passed in unchanged so the call site can detect "no change".
/// </summary>
public static class IconPickerService
{
    /// <summary>Outcome of <see cref="PickAsync"/>: <c>Changed</c> is true
    /// if the user committed a different selection; <c>Icon</c> is the new
    /// value (null means "no icon").</summary>
    public sealed class Result
    {
        public bool Changed { get; init; }
        public string? Icon { get; init; }
    }

    public static async Task<Result> PickAsync(Window owner, string? currentIcon = null)
    {
        var vm = new IconPickerViewModel(currentIcon);

        var dialog = new AppWindow
        {
            Title = "Choose icon",
            Width = 640,
            Height = 540,
            ShellContent = vm,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        string? chosen = currentIcon;
        bool changed = false;
        vm.Done += value =>
        {
            chosen = value;
            changed = !ReferenceEquals(value, currentIcon) && value != currentIcon;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return new Result { Changed = changed, Icon = chosen };
    }
}
