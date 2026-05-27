using Avalonia.Headless.XUnit;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Regression for task #145: <see cref="WindowManagerService"/> must
/// dispose the previous <c>ShellContent</c> when (a) a dedup-keyed call
/// swaps the content in an existing window, and (b) a window is closed.
/// Without this, editor VMs (which start <see cref="DispatcherTimer"/>s
/// in their constructors) leaked as ghosts and competed to push
/// conflicting config into alive ephemerals.
/// </summary>
public class WindowManagerServiceDisposalTests
{
    /// <summary>Stand-in for any editor VM. Inherits ViewModelBase so it
    /// satisfies the manager's API; implements IDisposable so the
    /// disposal contract can be observed.</summary>
    private class DisposableVm : ViewModelBase, IDisposable
    {
        public int DisposeCalls;
        public virtual void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    /// <summary>Second VM type so the "different type swap" path of
    /// <see cref="WindowManagerService"/> can be exercised.</summary>
    private sealed class AlternateDisposableVm : DisposableVm { }

    [AvaloniaFact]
    public void RelaunchSameType_DisposesDuplicate_PreservesExisting()
    {
        // Phase R4 contract: when a factory-resolved VM is re-shown on
        // the same key (e.g. user clicks 🎛 FX Chain twice), the
        // second instance is a duplicate the launcher built before
        // checking if a window already existed. The WindowManager
        // disposes the DUPLICATE and keeps the live editor — preserving
        // scroll position, transient UI state, and in-flight live-edit
        // pushes. Pre-fix the OPPOSITE happened: the live editor was
        // torn down and the duplicate took its place.
        var manager = new WindowManagerService();
        var first = new DisposableVm();
        var second = new DisposableVm();

        manager.ShowWindow(first, key: "test-window", title: "First", width: 400, height: 300);
        manager.ShowWindow(second, key: "test-window", title: "Second", width: 400, height: 300);

        first.DisposeCalls.Should().Be(0,
            "the currently-displayed editor must survive the duplicate re-launch");
        second.DisposeCalls.Should().Be(1,
            "the duplicate VM the caller built must be disposed");
    }

    [AvaloniaFact]
    public void RelaunchDifferentType_DisposesOld_InstallsNew()
    {
        // The "different type swap" path: same window key reused for a
        // genuinely different VM type. The old content gets disposed
        // and the new one takes over. Currently no production caller
        // hits this branch (per-owner keys ensure types match), but
        // the contract is preserved for future use.
        var manager = new WindowManagerService();
        var first = new DisposableVm();
        var second = new AlternateDisposableVm();

        manager.ShowWindow(first, key: "test-window", title: "First", width: 400, height: 300);
        manager.ShowWindow(second, key: "test-window", title: "Second", width: 400, height: 300);

        first.DisposeCalls.Should().Be(1,
            "the displaced content of a different type must be disposed");
        second.DisposeCalls.Should().Be(0,
            "the new content is now the live ShellContent");
    }

    [AvaloniaFact]
    public void Close_DisposesCurrentShellContent()
    {
        // CloseWindow uses KeyFor(content) — the type-based key — so the
        // test must use the type-keyed overload too.
        var manager = new WindowManagerService();
        var vm = new DisposableVm();

        manager.ShowWindow(vm, "X", 400, 300);
        vm.DisposeCalls.Should().Be(0, "still open");

        manager.CloseWindow(vm);

        vm.DisposeCalls.Should().Be(1, "window-close handler must dispose the ShellContent");
    }

    [AvaloniaFact]
    public void Swap_WithSameContentReference_DoesNotDispose()
    {
        // Defensive: if the caller somehow passes the SAME VM through
        // ShowWindow twice with the same key (e.g., a re-focus call),
        // we must NOT dispose the VM that is still being displayed.
        var manager = new WindowManagerService();
        var vm = new DisposableVm();

        manager.ShowWindow(vm, key: "same-key", title: "A", width: 400, height: 300);
        manager.ShowWindow(vm, key: "same-key", title: "B", width: 400, height: 300);

        vm.DisposeCalls.Should().Be(0,
            "same-reference re-show must not dispose the still-active content");
    }

    [AvaloniaFact]
    public void NonDisposable_ShellContent_NoCrash()
    {
        // Many existing VMs don't implement IDisposable; the manager
        // must gracefully no-op for them.
        var manager = new WindowManagerService();
        var plain = new ViewModelBase();

        var act = () =>
        {
            manager.ShowWindow(plain, key: "plain", title: "P", width: 400, height: 300);
            manager.CloseWindow(plain);
        };

        act.Should().NotThrow();
    }
}
