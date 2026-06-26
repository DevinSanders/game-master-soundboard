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

    // Duplicate-handling policy. Three mutually-exclusive radio buttons in the
    // dialog; defaults to Skip — the safest choice (nothing existing is
    // mutated and nothing duplicate is inserted).
    [ObservableProperty] private bool _isDuplicateSkip = true;
    [ObservableProperty] private bool _isDuplicateReplace;
    [ObservableProperty] private bool _isDuplicateAllow;

    // Rename strategy applied when IsDuplicateAllow is selected. Two
    // mutually-exclusive radio buttons; defaults to numeric suffix.
    [ObservableProperty] private bool _isRenameNumeric = true;
    [ObservableProperty] private bool _isRenameCopy;

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

    // Keep the three duplicate-policy flags mutually exclusive — same pattern
    // as the destination radio group above. The setters short-circuit on
    // already-correct state so the cascading writes terminate after one round.
    partial void OnIsDuplicateSkipChanged(bool value)
    {
        if (value) { IsDuplicateReplace = false; IsDuplicateAllow = false; }
        else if (!IsDuplicateReplace && !IsDuplicateAllow) IsDuplicateSkip = true;
    }

    partial void OnIsDuplicateReplaceChanged(bool value)
    {
        if (value) { IsDuplicateSkip = false; IsDuplicateAllow = false; }
        else if (!IsDuplicateSkip && !IsDuplicateAllow) IsDuplicateReplace = true;
    }

    partial void OnIsDuplicateAllowChanged(bool value)
    {
        if (value) { IsDuplicateSkip = false; IsDuplicateReplace = false; }
        else if (!IsDuplicateSkip && !IsDuplicateReplace) IsDuplicateAllow = true;
    }

    partial void OnIsRenameNumericChanged(bool value)
    {
        if (value) IsRenameCopy = false;
        else if (!IsRenameCopy) IsRenameNumeric = true;
    }

    partial void OnIsRenameCopyChanged(bool value)
    {
        if (value) IsRenameNumeric = false;
        else if (!IsRenameNumeric) IsRenameCopy = true;
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
            DuplicateHandling =
                IsDuplicateReplace ? DuplicatePolicy.Replace :
                IsDuplicateAllow   ? DuplicatePolicy.AllowDuplicates :
                                     DuplicatePolicy.Skip,
            RenameStrategy = IsRenameCopy ? DuplicateRenameStrategy.CopySuffix : DuplicateRenameStrategy.NumericSuffix,
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
