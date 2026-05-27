using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.UI.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the icon picker dialog. Lists every RPG Awesome icon name, filters by
/// the search box, and reports the chosen icon (or null for "no icon") via
/// <see cref="Done"/>.
/// </summary>
public partial class IconPickerViewModel : ViewModelBase
{
    private static readonly string[] AllNames =
        RpgAwesomeIcons.Codepoints.Keys.OrderBy(n => n).ToArray();

    public ObservableCollection<string> FilteredIcons { get; } = new(AllNames);

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _selectedIcon;

    /// <summary>Fires when the user makes a selection. Argument is the chosen
    /// icon name, or null for "clear icon" / cancel.</summary>
    public event Action<string?>? Done;

    public IconPickerViewModel(string? initial = null)
    {
        SelectedIcon = initial;
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredIcons.Clear();
        var q = value.Trim().ToLowerInvariant();
        foreach (var name in AllNames)
        {
            if (q.Length == 0 || name.Contains(q, StringComparison.Ordinal))
                FilteredIcons.Add(name);
        }
    }

    [RelayCommand]
    private void Pick(string? icon)
    {
        SelectedIcon = icon;
        Done?.Invoke(icon);
    }

    [RelayCommand]
    private void Clear() => Done?.Invoke(null);

    [RelayCommand]
    private void Cancel() => Done?.Invoke(SelectedIcon); // keep whatever was passed in
}
