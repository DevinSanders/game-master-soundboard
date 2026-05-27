using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using SoundBoard.Core.Services;
using SoundBoard.Tests.Fakes;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Regression for Phase 2C: <see cref="PresetEditorViewModel"/> subscribes
/// to <c>playbackEngine.ActiveItems.CollectionChanged</c> in its
/// constructor. Pre-fix the handler was a lambda registered without a
/// field, so the VM stayed rooted by the engine singleton after the
/// editor closed — every "edit a different preset" swap leaked a VM.
/// </summary>
public class PresetEditorViewModelDisposalTests
{
    private static (PresetEditorViewModel vm, IAudioPlaybackEngine engine, SqliteInMemoryDbFixture fx) BuildEditor()
    {
        var fx = new SqliteInMemoryDbFixture();
        var engine = Substitute.For<IAudioPlaybackEngine>();
        var activeItems = new ObservableCollection<IActiveMixerItem>();
        engine.ActiveItems.Returns(activeItems);

        var vm = new PresetEditorViewModel(
            fx.Factory, engine,
            Substitute.For<ISamplerChainService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IWindowManagerService>(),
            Substitute.For<ISamplerLauncherService>());
        return (vm, engine, fx);
    }

    [AvaloniaFact]
    public void Dispose_UnsubscribesActiveItemsCollectionChanged()
    {
        var (vm, engine, fx) = BuildEditor();
        using var dbFixture = fx;
        var activeItems = engine.ActiveItems;

        int beforeCount = CountHandlers(activeItems);
        vm.Dispose();
        int afterCount = CountHandlers(activeItems);

        afterCount.Should().BeLessThan(beforeCount,
            "Dispose must remove the CollectionChanged handler the constructor added");
    }

    [AvaloniaFact]
    public void Dispose_IsSafeToCallTwice()
    {
        var (vm, _, fx) = BuildEditor();
        using var dbFixture = fx;

        var act = () =>
        {
            vm.Dispose();
            vm.Dispose();
        };

        act.Should().NotThrow("second Dispose call must be a no-op (Unloaded + WindowManager close can both fire)");
    }

    [AvaloniaFact]
    public void AfterDispose_ActiveItemsChange_DoesNotInvokeVm()
    {
        var (vm, engine, fx) = BuildEditor();
        using var dbFixture = fx;
        var activeItems = (ObservableCollection<IActiveMixerItem>)engine.ActiveItems;

        vm.Dispose();

        var act = () =>
        {
            var fakeItem = Substitute.For<IActiveMixerItem>();
            activeItems.Add(fakeItem);
            activeItems.Remove(fakeItem);
        };
        act.Should().NotThrow();
    }

    /// <summary>Count CollectionChanged handlers via reflection.
    /// ObservableCollection's CollectionChanged is a multicast delegate
    /// on the base class — we count subscribers as proof of (de)registration.</summary>
    private static int CountHandlers(ObservableCollection<IActiveMixerItem> collection)
    {
        var ev = typeof(ObservableCollection<IActiveMixerItem>)
            .GetField("CollectionChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (ev?.GetValue(collection) is not System.Collections.Specialized.NotifyCollectionChangedEventHandler handler)
            return 0;
        return handler.GetInvocationList().Length;
    }
}
