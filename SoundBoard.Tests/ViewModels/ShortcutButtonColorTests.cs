using Avalonia.Media;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;
using System.Collections.ObjectModel;

namespace SoundBoard.Tests.ViewModels;

/// <summary>
/// Pins the per-button color contract on <see cref="ShortcutButtonViewModel"/>:
/// icon / button color overrides resolve to the chosen color, and the label
/// only gets its legibility scrim + white text when it sits over an icon or a
/// custom button color (plain text buttons stay clean).
/// </summary>
public sealed class ShortcutButtonColorTests
{
    private static ShortcutButtonViewModel Vm(ShortcutButton model)
    {
        var engine = Substitute.For<IAudioPlaybackEngine>();
        engine.ActiveItems.Returns(new ObservableCollection<IActiveMixerItem>());
        return new ShortcutButtonViewModel(model, engine);
    }

    [Fact]
    public void IconColorOverride_ResolvesToChosenColor()
    {
        var vm = Vm(new ShortcutButton { Label = "X", Icon = "ra-dragon", IconColor = "#F2C14E" });
        vm.IconColor.Should().Be("#F2C14E");
        vm.IconBrush.Should().BeOfType<SolidColorBrush>()
          .Which.Color.Should().Be(Color.Parse("#F2C14E"));
    }

    [Fact]
    public void ButtonColorOverride_ResolvesToChosenColor()
    {
        var vm = Vm(new ShortcutButton { Label = "X", ButtonColor = "#7A1F1F" });
        vm.ButtonColor.Should().Be("#7A1F1F");
        vm.ButtonBrush.Should().BeOfType<SolidColorBrush>()
          .Which.Color.Should().Be(Color.Parse("#7A1F1F"));
    }

    [Fact]
    public void PlainTextButton_HasNoLabelScrim()
    {
        // No icon, no custom color: the label stays transparent so a grid of
        // text buttons reads unchanged.
        var vm = Vm(new ShortcutButton { Label = "Dice Pool" });
        vm.LabelBackground.Should().Be(Brushes.Transparent);
    }

    [Fact]
    public void ButtonWithIcon_GetsScrimAndWhiteLabel()
    {
        var vm = Vm(new ShortcutButton { Label = "Battle", Icon = "ra-dragon" });
        vm.LabelBackground.Should().NotBe(Brushes.Transparent);
        vm.LabelForeground.Should().Be(Brushes.White);
    }

    [Fact]
    public void ButtonWithCustomColorButNoIcon_GetsScrimAndWhiteLabel()
    {
        // A custom button color also breaks the theme's text/surface contrast
        // assumption, so the scrim + white label kicks in even without an icon.
        var vm = Vm(new ShortcutButton { Label = "Alarm", ButtonColor = "#7A1F1F" });
        vm.LabelBackground.Should().NotBe(Brushes.Transparent);
        vm.LabelForeground.Should().Be(Brushes.White);
    }

    [Fact]
    public void InvalidHex_DoesNotThrow_AndFallsBack()
    {
        // A garbage override must not crash the render; it falls back to a
        // brush (theme default or the hard fallback) rather than throwing.
        var vm = Vm(new ShortcutButton { Label = "X", IconColor = "not-a-color", ButtonColor = "###" });
        vm.IconBrush.Should().NotBeNull();
        vm.ButtonBrush.Should().NotBeNull();
    }
}
