using Avalonia.Input;
using SoundBoard.Core.Models;
using SoundBoard.PluginApi;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Services;

/// <summary>
/// One place for every in-process drag-and-drop payload format the app
/// uses. Pre-fix each view defined its own <see cref="DataFormat{T}"/>
/// static; drop targets had to <c>using</c> the source view's class just
/// to reach the format, and the MIME-string naming drifted between
/// "-card", "-item", "-button" without rhyme or reason.
///
/// <para>Naming convention: <c>application/x-soundboard-{noun}</c> where
/// noun is the dragged thing in singular form.</para>
/// </summary>
public static class DragFormats
{
    /// <summary>A library Track being dragged onto a preset or playlist
    /// editor, or onto the soundboard.</summary>
    public static readonly DataFormat<Track> Track =
        DataFormat.CreateInProcessFormat<Track>("application/x-soundboard-track");

    /// <summary>A preset being dragged onto a playlist or the soundboard.</summary>
    public static readonly DataFormat<Preset> Preset =
        DataFormat.CreateInProcessFormat<Preset>("application/x-soundboard-preset");

    /// <summary>A playlist being dragged from the Playlists window onto
    /// the soundboard.</summary>
    public static readonly DataFormat<Playlist> Playlist =
        DataFormat.CreateInProcessFormat<Playlist>("application/x-soundboard-playlist");

    /// <summary>A playlist item (row VM) being reordered inside the
    /// Playlist editor.</summary>
    public static readonly DataFormat<PlaylistEditorItemViewModel> PlaylistItem =
        DataFormat.CreateInProcessFormat<PlaylistEditorItemViewModel>("application/x-soundboard-playlist-item");

    /// <summary>A preset-editor card (per-Track override) being reordered
    /// inside the Preset editor.</summary>
    public static readonly DataFormat<PresetTrackCardViewModel> PresetCard =
        DataFormat.CreateInProcessFormat<PresetTrackCardViewModel>("application/x-soundboard-preset-card");

    /// <summary>A shortcut button being reordered inside the soundboard
    /// grid (or moved across pages).</summary>
    public static readonly DataFormat<ShortcutButtonViewModel> ShortcutButton =
        DataFormat.CreateInProcessFormat<ShortcutButtonViewModel>("application/x-soundboard-button");

    /// <summary>A plugin instance being dragged from the FX Chain editor's
    /// "Available" list onto the chain pane.</summary>
    public static readonly DataFormat<IAudioSamplerPlugin> SamplerPlugin =
        DataFormat.CreateInProcessFormat<IAudioSamplerPlugin>("application/x-soundboard-sampler-plugin");

    /// <summary>An attached FX chain row being reordered inside the
    /// FX Chain editor.</summary>
    public static readonly DataFormat<AttachedSamplerViewModel> FxChainItem =
        DataFormat.CreateInProcessFormat<AttachedSamplerViewModel>("application/x-soundboard-fx-chain-item");
}
