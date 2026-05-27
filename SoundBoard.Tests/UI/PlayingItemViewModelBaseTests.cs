using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins the contract of <see cref="PlayingItemViewModelBase"/>: the shared
/// Volume / IsPaused / VolumePercent / PlayPauseText surface that the Mixer
/// view binds to is identical across PlayingTrack, PlayingPreset, and
/// PlayingPlaylist VMs. Derived classes route the actual audio plumbing via
/// the virtual <c>OnXChangedCore</c> hooks.
///
/// <para>Uses a stub subclass instead of any concrete VM so the test stays
/// independent of audio providers and child collections.</para>
/// </summary>
public class PlayingItemViewModelBaseTests
{
    private sealed class Stub : PlayingItemViewModelBase
    {
        public int VolumeCoreCalls { get; private set; }
        public double LastVolumeCore { get; private set; }
        public int IsPausedCoreCalls { get; private set; }
        public bool LastIsPausedCore { get; private set; }
        public int StopCalls { get; private set; }

        public override string Name => "stub";
        public override void Stop() => StopCalls++;

        protected override void OnVolumeChangedCore(double value)
        {
            VolumeCoreCalls++;
            LastVolumeCore = value;
        }

        protected override void OnIsPausedChangedCore(bool value)
        {
            IsPausedCoreCalls++;
            LastIsPausedCore = value;
        }
    }

    [AvaloniaFact]
    public void Volume_DefaultsToOne_AndVolumePercentTracksIt()
    {
        var vm = new Stub();
        vm.Volume.Should().Be(1.0);
        vm.VolumePercent.Should().Be(100);

        vm.Volume = 0.5;
        vm.VolumePercent.Should().Be(50);

        vm.Volume = 1.5;
        vm.VolumePercent.Should().Be(150);
    }

    [AvaloniaFact]
    public void IsPaused_FlipsPlayPauseText()
    {
        var vm = new Stub();
        vm.PlayPauseText.Should().Be("Pause");

        vm.IsPaused = true;
        vm.PlayPauseText.Should().Be("Resume");

        vm.IsPaused = false;
        vm.PlayPauseText.Should().Be("Pause");
    }

    [AvaloniaFact]
    public void OnVolumeChanged_InvokesCoreHook()
    {
        // The whole reason the base exists: derived VMs wire concrete audio
        // through OnVolumeChangedCore. A regression that stops calling the
        // hook would silently break mixer-volume sliders for every card type.
        var vm = new Stub();
        vm.VolumeCoreCalls.Should().Be(0);

        vm.Volume = 0.75;
        vm.VolumeCoreCalls.Should().Be(1);
        vm.LastVolumeCore.Should().Be(0.75);

        vm.Volume = 0.25;
        vm.VolumeCoreCalls.Should().Be(2);
        vm.LastVolumeCore.Should().Be(0.25);
    }

    [AvaloniaFact]
    public void OnIsPausedChanged_InvokesCoreHook()
    {
        var vm = new Stub();
        vm.IsPausedCoreCalls.Should().Be(0);

        vm.IsPaused = true;
        vm.IsPausedCoreCalls.Should().Be(1);
        vm.LastIsPausedCore.Should().BeTrue();

        vm.IsPaused = false;
        vm.IsPausedCoreCalls.Should().Be(2);
        vm.LastIsPausedCore.Should().BeFalse();
    }

    [AvaloniaFact]
    public void HasAttachedSamplers_ReflectsListNonEmpty()
    {
        var emptyVm = new Stub();
        emptyVm.HasAttachedSamplers.Should().BeFalse();

        var withVm = new Stub { AttachedSamplers = new[] { new SamplerBadge("foo", "F") } };
        withVm.HasAttachedSamplers.Should().BeTrue();
    }

    [AvaloniaFact]
    public void HasSamplerEditor_ReflectsActionPresence()
    {
        var noEditor = new Stub();
        noEditor.HasSamplerEditor.Should().BeFalse();

        bool invoked = false;
        var withEditor = new Stub { OpenSamplerEditorAction = () => invoked = true };
        withEditor.HasSamplerEditor.Should().BeTrue();

        withEditor.OpenSamplerEditorCommand.Execute(null);
        invoked.Should().BeTrue("OpenSamplerEditorCommand routes through OpenSamplerEditorAction");
    }
}
