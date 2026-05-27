using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Thin wrapper over Avalonia's storage-provider file pickers so view
/// models don't need to take a direct dependency on Avalonia types. One
/// implementation per platform shell is fine; the UI project ships the
/// desktop one.
/// </summary>
public interface IFileService
{
    Task<IEnumerable<string>> OpenFileDialogAsync(string title, string[] extensions);
    Task<string?> SaveFileDialogAsync(string title, string defaultExtension, string[] filters);
    Task<string?> OpenFolderDialogAsync(string title);
}
