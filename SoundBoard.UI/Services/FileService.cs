using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <inheritdoc cref="IFileService"/>
public class FileService : IFileService
{
    public async Task<IEnumerable<string>> OpenFileDialogAsync(string title, string[] extensions)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel != null)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Files") { Patterns = extensions }
                    }
                });

                return files.Select(f => f.Path.LocalPath);
            }
        }
        return Enumerable.Empty<string>();
    }

    public async Task<string?> SaveFileDialogAsync(string title, string defaultExtension, string[] filters)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel != null)
            {
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title,
                    DefaultExtension = defaultExtension,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Database Files") { Patterns = filters }
                    }
                });

                return file?.Path.LocalPath;
            }
        }
        return null;
    }

    public async Task<string?> OpenFolderDialogAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                });

                return folders.FirstOrDefault()?.Path.LocalPath;
            }
        }
        return null;
    }
}
