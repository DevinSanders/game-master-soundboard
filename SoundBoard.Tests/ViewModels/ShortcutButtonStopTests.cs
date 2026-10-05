using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;
using System.Collections.ObjectModel;

namespace SoundBoard.Tests.ViewModels;

/// <summary>
/// The locked-board long-press gesture calls <see cref="ShortcutButtonViewModel.Stop"/>,
/// which must fully stop the button's target via the matching engine Stop*
/// method (not pause).
/// </summary>
public sealed class ShortcutButtonStopTests
{
    private static (ShortcutButtonViewModel vm, IAudioPlaybackEngine engine) Make(ShortcutButton model)
    {
        var engine = Substitute.For<IAudioPlaybackEngine>();
        engine.ActiveItems.Returns(new ObservableCollection<IActiveMixerItem>());
        return (new ShortcutButtonViewModel(model, engine), engine);
    }

    [Fact]
    public void Stop_TrackTarget_StopsTrack()
    {
        var track = new Track { Id = 5, Name = "Rain" };
        var (vm, engine) = Make(new ShortcutButton { TrackId = 5, Track = track });
        vm.Stop();
        engine.Received(1).StopTrack(track);
    }

    [Fact]
    public void Stop_PresetTarget_StopsPreset()
    {
        var preset = new Preset { Id = 7, Name = "Combat" };
        var (vm, engine) = Make(new ShortcutButton { PresetId = 7, Preset = preset });
        vm.Stop();
        engine.Received(1).StopPreset(preset);
    }

    [Fact]
    public void Stop_PlaylistTarget_StopsPlaylist()
    {
        var playlist = new Playlist { Id = 9, Name = "Session" };
        var (vm, engine) = Make(new ShortcutButton { PlaylistId = 9, Playlist = playlist });
        vm.Stop();
        engine.Received(1).StopPlaylist(playlist);
    }
}
