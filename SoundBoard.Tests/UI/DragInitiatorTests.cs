using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SoundBoard.UI.Services;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins <see cref="DragInitiator"/>'s OR-semantic click-vs-drag
/// discrimination: drag fires when EITHER the pointer has been held
/// past <c>MinHoldMs</c> OR moved past <c>MinDistance</c>. Pure-AND
/// semantics were tried first and broke fast drag-reorder (most
/// reorders finish in &lt;180 ms total, so the hold gate never trips).
///
/// <para>Pointer construction requires the Avalonia headless input
/// platform. <c>[AvaloniaFact]</c> from <c>Avalonia.Headless.XUnit</c>
/// sets up a window with a real pointer source per test, so we can
/// drive pointer-pressed and pointer-moved events the same shape
/// Avalonia produces in production.</para>
/// </summary>
public class DragInitiatorTests
{
    /// <summary>Build a window with a single Border, return both. The
    /// border is the reference visual for coordinate computations.</summary>
    private static (Window window, Border target) CreateWindowedTarget()
    {
        var border = new Border { Width = 200, Height = 200, Background = Avalonia.Media.Brushes.Black };
        var window = new Window { Content = border, Width = 300, Height = 300 };
        window.Show();
        // Force layout so the visual has a valid position.
        Dispatcher.UIThread.RunJobs();
        return (window, border);
    }

    [AvaloniaFact]
    public void TinyJitter_DoesNotTripDrag()
    {
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 103, 102), target).Should().BeFalse(
            "3 px of jitter is well under the 12 px gate");
    }

    [AvaloniaFact]
    public void MoveBeyondDistanceThreshold_TripsDrag()
    {
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 115, 100), target).Should().BeTrue(
            "15 px X movement past the 12 px gate");
    }

    [AvaloniaFact]
    public void HoldWithoutMovement_TripsDrag_AfterMinHoldMs()
    {
        // MinHoldMs = 0 so any non-zero elapsed time trips. Verifies the
        // hold-gate path exists; the production threshold is 180 ms.
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator { MinHoldMs = 0, MinDistance = 100 };

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        Thread.Sleep(20);
        drag.ShouldStartDrag(SynthesizeMove(target, 100, 100), target).Should().BeTrue(
            "MinHoldMs=0 + 20ms elapsed → hold gate trips even without movement");
    }

    [AvaloniaFact]
    public void MarkDragStarted_PreventsRedundantFires()
    {
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 200, 200), target).Should().BeTrue();
        drag.MarkDragStarted();

        drag.ShouldStartDrag(SynthesizeMove(target, 210, 210), target).Should().BeFalse();
        drag.ShouldStartDrag(SynthesizeMove(target, 220, 220), target).Should().BeFalse();
    }

    [AvaloniaFact]
    public void NotifyPressed_RearmsAfterDragStarted()
    {
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 200, 200), target).Should().BeTrue();
        drag.MarkDragStarted();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 200, 200), target).Should().BeTrue(
            "a new press re-arms the discriminator");
    }

    [AvaloniaFact]
    public void Reset_DisarmsWithoutNewPress()
    {
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizePress(target, 100, 100), target);
        drag.Reset();

        drag.ShouldStartDrag(SynthesizeMove(target, 999, 999), target).Should().BeFalse(
            "after Reset, no drag fires even on a big move — until the next press re-arms");
    }

    [AvaloniaFact]
    public void RightClick_DoesNotArm()
    {
        // DragInitiator filters on left-button pressed. A right-click +
        // movement should not promote to a drag.
        var (_, target) = CreateWindowedTarget();
        var drag = new DragInitiator();

        drag.NotifyPressed(SynthesizeRightPress(target, 100, 100), target);
        drag.ShouldStartDrag(SynthesizeMove(target, 200, 200), target).Should().BeFalse(
            "right-button press doesn't arm the discriminator");
    }

    // ── Synthetic event helpers ──────────────────────────────────────────────

    private static IPointer GetPointer() =>
        new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static PointerPressedEventArgs SynthesizePress(Visual target, double x, double y) =>
        new(
            target,
            GetPointer(),
            target,
            new Point(x, y),
            (ulong)DateTime.UtcNow.Ticks,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

    private static PointerPressedEventArgs SynthesizeRightPress(Visual target, double x, double y) =>
        new(
            target,
            GetPointer(),
            target,
            new Point(x, y),
            (ulong)DateTime.UtcNow.Ticks,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
            KeyModifiers.None);

    private static PointerEventArgs SynthesizeMove(Visual target, double x, double y) =>
        new(
            InputElement.PointerMovedEvent,
            target,
            GetPointer(),
            target,
            new Point(x, y),
            (ulong)DateTime.UtcNow.Ticks,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None);
}
