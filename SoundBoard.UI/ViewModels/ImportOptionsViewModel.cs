using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the "Import Library" options dialog. Lets the user pick a merge vs.
/// new-library mode, name the new library if they go that route, and add
/// search directories so the importer can reconcile audio file paths that
/// moved between machines (Drive mount points, copied folders, etc.).
/// </summary>
public partial class ImportOptionsViewModel : ViewModelBase
{
    private readonly IFileService _fileService;

    public string SourceFile { get; }

    [ObservableProperty] private bool _isMergeMode = true;
    [ObservableProperty] private bool _isNewLibraryMode;

    [ObservableProperty] private string _newLibraryName = "";

    public ObservableCollection<string> SearchPaths { get; } = new();

    /// <summary>Captured options on OK; null on cancel.</summary>
    public ImportOptions? Result { get; private set; }

    public event Action? Closed;

    public ImportOptionsViewModel(string sourceFile, IFileService fileService)
    {
        SourceFile = sourceFile;
        _fileService = fileService;
    }

    // Keep the two flags mutually exclusive. RadioButton groups handle this in
    // XAML by group-name but compiled bindings prefer property-level toggles
    // for two-way semantics.
    partial void OnIsMergeModeChanged(bool value)
    {
        if (value) IsNewLibraryMode = false;
        else if (!IsNewLibraryMode) IsNewLibraryMode = true;
    }

    partial void OnIsNewLibraryModeChanged(bool value)
    {
        if (value) IsMergeMode = false;
        else if (!IsMergeMode) IsMergeMode = true;
    }

    [RelayCommand]
    private async Task AddSearchPathAsync()
    {
        var dir = await _fileService.OpenFolderDialogAsync("Add search directory");
        if (!string.IsNullOrWhiteSpace(dir) &&
            !SearchPaths.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)))
        {
            SearchPaths.Add(dir);
        }
    }

    [RelayCommand]
    private void RemoveSearchPath(string? path)
    {
        if (path != null) SearchPaths.Remove(path);
    }

    [RelayCommand]
    private void Confirm()
    {
        var options = new ImportOptions
        {
            Mode = IsNewLibraryMode ? ImportMode.NewLibrary : ImportMode.Merge,
            NewLibraryName = IsNewLibraryMode ? NewLibraryName.Trim() : null,
            SearchPaths = new(SearchPaths),
        };

        if (options.Mode == ImportMode.NewLibrary && string.IsNullOrWhiteSpace(options.NewLibraryName))
        {
            // Don't accept an empty name — the caller would crash on
            // ReserveLibraryPath, and a silently-defaulted name would surprise
            // the user.
            return;
        }

        Result = options;
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Closed?.Invoke();
    }
}
