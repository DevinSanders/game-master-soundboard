using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.UI.Services;
using System;
using System.Threading.Tasks;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Library window's "Add Track…" dialog — the single entry
/// point for adding ANY playable source to the library: local file path
/// or remote URL. The field is intentionally generic ("URI") because
/// the host doesn't care which scheme a Track uses — whatever
/// <see cref="SoundBoard.Core.Audio.AudioFileReaderCrossPlatform"/> can
/// route to a registered codec is valid here.
///
/// <para><b>Validation</b> is delegated to the codec registry via the
/// <c>isSupported</c> delegate passed in at construction. Confirm
/// blocks (with an inline warning) when no installed codec claims the
/// URI's extension or scheme. That lets the user type any URI without
/// the dialog second-guessing them, but stops a Track row from being
/// inserted that's guaranteed to fail at play time.</para>
///
/// <para>The Browse button calls back into <see cref="IFileService"/>
/// for the OS file picker. A successful pick replaces whatever the URI
/// field currently holds (manual editing and Browse are alternatives,
/// not additive).</para>
/// </summary>
public partial class AddTrackViewModel : ViewModelBase
{
    private readonly IFileService _fileService;
    private readonly Func<string, bool> _isSupported;

    /// <summary>Track source — a local file path or a URL with a
    /// scheme some codec plugin advertises (e.g. http:// when
    /// gmsb-codec-webstream is installed).</summary>
    [ObservableProperty] private string _uri = "";

    /// <summary>Optional display name. Empty falls back to the URI's
    /// file-name stem (or the host when the URI has no useful path).</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>Optional comma-separated tags, same format as the
    /// Track Editor.</summary>
    [ObservableProperty] private string _tags = "";

    /// <summary>Inline error shown beneath the URI field. Set by
    /// Confirm when the URI is empty or no codec claims it. Cleared
    /// on every keystroke so the user can correct it without the
    /// message lingering.</summary>
    [ObservableProperty] private string? _uriError;

    /// <summary>Final captured values on OK; null on cancel. Read by
    /// the View's code-behind after <see cref="Closed"/> fires.</summary>
    public AddTrackResult? Result { get; private set; }

    public event Action? Closed;

    public AddTrackViewModel(IFileService fileService, Func<string, bool> isSupported)
    {
        _fileService = fileService;
        _isSupported = isSupported;
    }

    partial void OnUriChanged(string value) => UriError = null;

    [RelayCommand]
    private async Task Browse()
    {
        // Common audio extensions in the picker filter — codec validation
        // on Confirm is the source of truth, so even if the picker shows
        // a stray file type that doesn't have a codec installed, the user
        // gets the same warning as if they had typed the path manually.
        var paths = await _fileService.OpenFileDialogAsync(
            "Select audio file",
            new[] { "*.mp3", "*.wav", "*.ogg", "*.flac", "*.opus", "*.m4a", "*.aac" });
        // IFileService returns an empty enumerable on cancel — non-null
        // per the interface contract — so this loop simply doesn't fire.
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            Uri = p;
            return;  // single-track dialog — only the first pick is honoured
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        var trimmed = (Uri ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            UriError = "URI is required. Enter a file path or a URL.";
            return;
        }
        if (!_isSupported(trimmed))
        {
            UriError = "Unsupported URI or file type — no installed codec advertises this " +
                       "extension or scheme. Install a matching codec plugin and try again.";
            return;
        }

        Result = new AddTrackResult(
            Uri:  trimmed,
            Name: string.IsNullOrWhiteSpace(Name) ? DefaultName(trimmed) : Name.Trim(),
            Tags: string.IsNullOrWhiteSpace(Tags) ? "" : Tags.Trim());
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Closed?.Invoke();
    }

    /// <summary>Pick a usable display name from the URI when the user
    /// leaves Name blank. Tries the file-name stem first; if the URI
    /// parses as a URL with no useful path, falls back to the host.
    /// As a last resort returns the URI verbatim so the row at least
    /// reads as the source itself rather than something arbitrary.</summary>
    private static string DefaultName(string uri)
    {
        try
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(uri);
            if (!string.IsNullOrWhiteSpace(stem)) return stem;
        }
        catch { /* malformed path — fall through */ }

        if (System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && !string.IsNullOrEmpty(parsed.Host))
        {
            return parsed.Host;
        }
        return uri;
    }
}

/// <summary>Captured values from the Add-Track dialog. The host's
/// code-behind reads these after the dialog closes to insert the
/// Track row via <see cref="LibraryViewModel.AddTrack"/>.</summary>
public sealed record AddTrackResult(string Uri, string Name, string Tags);
