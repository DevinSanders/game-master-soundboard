using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Activation;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Builds <c>gmsound://</c> activation links. The user drops a Track, Preset,
/// or Playlist onto the window; the builder infers the item type and offers
/// the action set + parameters that make sense for that kind. Three formats
/// (plain URI, Markdown, HTML) are emitted side-by-side with Copy buttons.
/// </summary>
public partial class UriBuilderViewModel : ViewModelBase
{
    public ObservableCollection<string> ActionOptions { get; } = new()
    {
        // Default set — overridden when an item is dropped (see RefreshActionOptions).
        "Play", "Stop", "Toggle", "Stop All"
    };

    /// <summary>What was dragged in. Null until the user drops something
    /// (or selects Stop All, which doesn't need a target).</summary>
    [ObservableProperty] private SoundboardUriItemType? _selectedItemKind;
    [ObservableProperty] private int? _selectedItemId;
    [ObservableProperty] private string _selectedItemName = "";

    [ObservableProperty] private string _selectedAction = "Play";
    [ObservableProperty] private string _linkLabel = "Play";

    // Optional parameters
    [ObservableProperty] private bool _useVolume;
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private bool _useLoop;
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _useFadeIn;
    [ObservableProperty] private double _fadeInSeconds;
    [ObservableProperty] private bool _useFadeOut;
    [ObservableProperty] private double _fadeOutSeconds;
    [ObservableProperty] private bool _useStartDelay;
    [ObservableProperty] private double _startDelaySeconds;
    [ObservableProperty] private bool _stopPlaying;

    // ── Outputs ──────────────────────────────────────────────────────────────

    public string PlainUri    => BuildUri()?.ToUriString() ?? "(drop a track, preset, or playlist)";
    public string MarkdownLink
    {
        get
        {
            var uri = BuildUri()?.ToUriString();
            return uri is null ? "(drop a track, preset, or playlist)"
                               : $"[{EscapeMarkdown(LinkLabel)}]({uri})";
        }
    }
    public string HtmlLink
    {
        get
        {
            var uri = BuildUri()?.ToUriString();
            return uri is null ? "(drop a track, preset, or playlist)"
                               : $"<a href=\"{uri}\">{EscapeHtml(LinkLabel)}</a>";
        }
    }

    public bool HasItem => SelectedItemId.HasValue;
    public bool RequiresItem => SelectedAction != "Stop All";
    public bool ActionAllowsParams => SelectedAction == "Play";
    public bool CanGenerate => !RequiresItem || HasItem;

    public string ItemKindDisplay => SelectedItemKind switch
    {
        SoundboardUriItemType.Track    => "Track",
        SoundboardUriItemType.Preset   => "Preset",
        SoundboardUriItemType.Playlist => "Playlist",
        _ => ""
    };

    public UriBuilderViewModel()
    {
        // No DB dependency — the builder only reads what the user drops
        // onto it (Track / Preset / Playlist VMs from sibling windows
        // carry their own metadata) and emits URI strings. Earlier
        // versions took a SoundBoardDbContext that was never read, which
        // silently broke DI activation because SoundBoardDbContext isn't
        // registered (only the factory is). The RelayCommand caught the
        // ServiceProvider exception and dropped the click on the floor.
        RefreshActionOptions();
    }

    /// <summary>Set the target from a drag-drop on the URI Builder window.</summary>
    public void SetItem(SoundboardUriItemType kind, int id, string name)
    {
        SelectedItemKind = kind;
        SelectedItemId = id;
        SelectedItemName = name;
        LinkLabel = name;
        RefreshActionOptions();
        NotifyAll();
    }

    /// <summary>Convenience overloads for the view's drop handler.</summary>
    public void SetItem(Track track)       => SetItem(SoundboardUriItemType.Track,    track.Id,    track.Name);
    public void SetItem(Preset preset)     => SetItem(SoundboardUriItemType.Preset,   preset.Id,   preset.Name);
    public void SetItem(Playlist playlist) => SetItem(SoundboardUriItemType.Playlist, playlist.Id, playlist.Name);

    [RelayCommand]
    private void ClearItem()
    {
        SelectedItemKind = null;
        SelectedItemId = null;
        SelectedItemName = "";
        RefreshActionOptions();
        NotifyAll();
    }

    partial void OnSelectedActionChanged(string value)
    {
        OnPropertyChanged(nameof(RequiresItem));
        OnPropertyChanged(nameof(ActionAllowsParams));
        OnPropertyChanged(nameof(CanGenerate));
        NotifyOutputs();
    }

    partial void OnLinkLabelChanged(string value)        => NotifyOutputs();
    partial void OnUseVolumeChanged(bool value)          => NotifyOutputs();
    partial void OnVolumeChanged(double value)           => NotifyOutputs();
    partial void OnUseLoopChanged(bool value)            => NotifyOutputs();
    partial void OnLoopChanged(bool value)               => NotifyOutputs();
    partial void OnUseFadeInChanged(bool value)          => NotifyOutputs();
    partial void OnFadeInSecondsChanged(double value)    => NotifyOutputs();
    partial void OnUseFadeOutChanged(bool value)         => NotifyOutputs();
    partial void OnFadeOutSecondsChanged(double value)   => NotifyOutputs();
    partial void OnUseStartDelayChanged(bool value)       => NotifyOutputs();
    partial void OnStartDelaySecondsChanged(double value) => NotifyOutputs();
    partial void OnStopPlayingChanged(bool value)        => NotifyOutputs();

    private void RefreshActionOptions()
    {
        var current = SelectedAction;
        ActionOptions.Clear();

        // Stop All is always available as a target-less action.
        // Per-item actions depend on what was dropped.
        switch (SelectedItemKind)
        {
            case SoundboardUriItemType.Playlist:
                ActionOptions.Add("Play");
                ActionOptions.Add("Stop");
                ActionOptions.Add("Toggle");
                ActionOptions.Add("Next");
                ActionOptions.Add("Previous");
                break;

            case SoundboardUriItemType.Track:
            case SoundboardUriItemType.Preset:
                ActionOptions.Add("Play");
                ActionOptions.Add("Stop");
                ActionOptions.Add("Toggle");
                break;

            default:
                // No item dropped yet — just offer the target-less action.
                ActionOptions.Add("Play");
                ActionOptions.Add("Stop");
                ActionOptions.Add("Toggle");
                break;
        }
        ActionOptions.Add("Stop All");

        // Try to keep the user's current pick if still valid; otherwise default.
        SelectedAction = ActionOptions.Contains(current) ? current : ActionOptions[0];
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasItem));
        OnPropertyChanged(nameof(RequiresItem));
        OnPropertyChanged(nameof(ActionAllowsParams));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(ItemKindDisplay));
        NotifyOutputs();
    }

    private void NotifyOutputs()
    {
        OnPropertyChanged(nameof(PlainUri));
        OnPropertyChanged(nameof(MarkdownLink));
        OnPropertyChanged(nameof(HtmlLink));
    }

    // ── Build / serialize ────────────────────────────────────────────────────

    private SoundboardUri? BuildUri()
    {
        var action = SelectedAction switch
        {
            "Play"     => SoundboardUriAction.Play,
            "Stop"     => SoundboardUriAction.Stop,
            "Toggle"   => SoundboardUriAction.Toggle,
            "Stop All" => SoundboardUriAction.StopAll,
            "Next"     => SoundboardUriAction.Next,
            "Previous" => SoundboardUriAction.Previous,
            _ => SoundboardUriAction.Play
        };

        var uri = new SoundboardUri { Action = action };

        if (action != SoundboardUriAction.StopAll)
        {
            if (SelectedItemKind == null || SelectedItemId == null) return null;
            uri.ItemType = SelectedItemKind;
            uri.ItemId = SelectedItemId;
        }

        if (action == SoundboardUriAction.Play)
        {
            if (UseVolume)    uri.Volume    = (float)Volume;
            if (UseLoop)      uri.Loop      = Loop;
            if (UseFadeIn)    uri.FadeIn    = TimeSpan.FromSeconds(FadeInSeconds);
            if (UseFadeOut)   uri.FadeOut   = TimeSpan.FromSeconds(FadeOutSeconds);
            if (UseStartDelay) uri.StartDelay = TimeSpan.FromSeconds(StartDelaySeconds);
        }

        // stopPlaying is allowed on every action — but is a no-op when the
        // action itself is StopAll (the parser & handler still tolerate it).
        if (StopPlaying) uri.StopPlaying = true;

        return uri;
    }

    // ── Copy helpers ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyPlainAsync(IClipboard? clipboard)    => await CopyAsync(clipboard, PlainUri);

    [RelayCommand]
    private async Task CopyMarkdownAsync(IClipboard? clipboard) => await CopyAsync(clipboard, MarkdownLink);

    [RelayCommand]
    private async Task CopyHtmlAsync(IClipboard? clipboard)     => await CopyAsync(clipboard, HtmlLink);

    private static async Task CopyAsync(IClipboard? clipboard, string text)
    {
        if (clipboard == null || string.IsNullOrEmpty(text)) return;
        try { await clipboard.SetTextAsync(text); }
        catch (Exception ex) { Log.Warn("URI", "Clipboard copy failed", ex); }
    }

    private static string EscapeMarkdown(string label) =>
        label.Replace("\\", "\\\\").Replace("]", "\\]").Replace("[", "\\[");

    private static string EscapeHtml(string label) =>
        label.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
