using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SoundBoard.UI.Services;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins the lifecycle contract of <see cref="GhostDragOverlay"/>:
/// <c>Begin</c> adds a hit-test-invisible visual to the window's overlay
/// layer, <c>Update</c> repositions it under the pointer, <c>End</c>
/// removes it. The overlay is the cornerstone of the polished
/// intra-window drag-reorder UX (Phase A); a regression here would
/// silently turn the shortcut grid back into "OS cursor only" feedback.
///
/// <para>Headless caveat: <see cref="Avalonia.Media.Imaging.RenderTargetBitmap.Render"/>
/// can fail on headless platforms without a render thread. The overlay
/// catches that and falls through to a no-op so tests remain stable —
/// these tests therefore verify the layer mechanics rather than the
/// snapshot bitmap pixels. The actual snapshot quality is validated by
/// manual smoke test on Windows / macOS / Linux.</para>
/// </summary>
public class GhostDragOverlayTests
{
    /// <summary>Build a real headless window with a target child Border
    /// big enough to snapshot. Returns the window (caller owns disposal),
    /// the target visual, and its overlay layer.</summary>
    private static (Window window, Border target, OverlayLayer layer) CreateWindowedTarget()
    {
        var border = new Border { Width = 80, Height = 80, Background = Avalonia.Media.Brushes.SlateGray };
        var window = new Window { Content = border, Width = 300, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var layer = OverlayLayer.GetOverlayLayer(border)!;
        return (window, border, layer);
    }

    private static PointerEventArgs SyntheticMove(Visual target, double x, double y) =>
        new(
            InputElement.PointerMovedEvent,
            target,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true),
            target,
            new Point(x, y),
            (ulong)System.DateTime.UtcNow.Ticks,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None);

    [AvaloniaFact]
    public void For_OnAttachedVisual_ReturnsOverlay()
    {
        var (_, target, _) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target);
        overlay.Should().NotBeNull("an attached visual always resolves to a TopLevel + OverlayLayer");
    }

    [AvaloniaFact]
    public void Begin_AddsChildToOverlayLayer_AndMarksActive()
    {
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;

        int before = layer.Children.Count;
        overlay.Begin(target, SyntheticMove(target, 10, 10));

        overlay.IsActive.Should().BeTrue();
        layer.Children.Count.Should().Be(before + 1, "Begin must add exactly one child");
    }

    [AvaloniaFact]
    public void End_RemovesChildAndClearsActive()
    {
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;
        overlay.Begin(target, SyntheticMove(target, 10, 10));
        int afterBegin = layer.Children.Count;

        overlay.End();

        overlay.IsActive.Should().BeFalse();
        layer.Children.Count.Should().Be(afterBegin - 1, "End must remove the ghost it added");
    }

    [AvaloniaFact]
    public void Begin_Twice_ReplacesPreviousGhost()
    {
        // Defensive contract — if a drag is cut short without End (e.g.
        // capture revoked then a new gesture starts), the second Begin
        // must not leak the first ghost.
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;
        int baseline = layer.Children.Count;

        overlay.Begin(target, SyntheticMove(target, 10, 10));
        overlay.Begin(target, SyntheticMove(target, 20, 20));

        layer.Children.Count.Should().Be(baseline + 1, "the second Begin must replace, not stack");
    }

    [AvaloniaFact]
    public void Update_AfterEnd_IsNoop()
    {
        var (_, target, _) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;
        overlay.Begin(target, SyntheticMove(target, 10, 10));
        overlay.End();

        var act = () => overlay.Update(SyntheticMove(target, 50, 50));
        act.Should().NotThrow("Update after End must be safely ignored");
    }

    [AvaloniaFact]
    public void Update_TracksPointer_RigidlyByDelta()
    {
        // The grab offset is the pointer's position inside the source at
        // Begin time. The ghost's top-left then tracks the pointer
        // rigidly: between any two Update calls, the ghost shifts by
        // exactly the pointer's delta. Asserting deltas (rather than
        // absolute Canvas values) sidesteps the source-vs-layer
        // coordinate offset that depends on Window chrome / centering.
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;

        overlay.Begin(target, SyntheticMove(target, 30, 40));
        Dispatcher.UIThread.RunJobs();
        var ghost = (Visual)layer.Children[^1];

        overlay.Update(SyntheticMove(target, 130, 140));
        Dispatcher.UIThread.RunJobs();
        var x1 = Canvas.GetLeft(ghost);
        var y1 = Canvas.GetTop(ghost);

        // Pointer moves +70 X / -80 Y in source-space, which is the same
        // delta in layer-space (the two frames differ only by a constant
        // offset, so deltas are preserved).
        overlay.Update(SyntheticMove(target, 200, 60));
        Dispatcher.UIThread.RunJobs();
        var x2 = Canvas.GetLeft(ghost);
        var y2 = Canvas.GetTop(ghost);

        (x2 - x1).Should().BeApproximately(70, 0.5,
            "+70 X on the pointer should move the ghost +70 X");
        (y2 - y1).Should().BeApproximately(-80, 0.5,
            "-80 Y on the pointer should move the ghost -80 Y");
    }

    [AvaloniaFact]
    public void Ghost_IsNotHitTestVisible()
    {
        // Critical for the live-shuffle hit-test: the ItemsControl's
        // InputHitTest must return the underlying card, never the ghost
        // sitting above it. If the ghost ever steals hits, the reorder
        // logic gets confused and the wrong item moves.
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;

        overlay.Begin(target, SyntheticMove(target, 10, 10));

        var ghost = (Control)layer.Children[^1];
        ghost.IsHitTestVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void BeginWithTemplate_InstantiatesTemplateAgainstDataContext()
    {
        // Template-clone path is the DPI-correct one (Avalonia issue #17235).
        // Verify it: the cloned visual is a ContentControl whose Content is
        // the supplied dataContext and whose ContentTemplate is the
        // supplied template — Avalonia then renders the result the same
        // way as the real item, no bitmap interpolation involved.
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;

        var template = new Avalonia.Controls.Templates.FuncDataTemplate<string>(
            (data, _) => new Avalonia.Controls.TextBlock { Text = data });
        overlay.BeginWithTemplate(target, SyntheticMove(target, 5, 5), template, "ghost-text");

        overlay.IsActive.Should().BeTrue();
        var outer = (Border)layer.Children[^1];
        outer.Child.Should().BeOfType<ContentControl>();
        var clone = (ContentControl)outer.Child!;
        clone.Content.Should().Be("ghost-text");
        clone.ContentTemplate.Should().BeSameAs(template);
    }

    [AvaloniaFact]
    public void BeginWithTemplate_GhostIsHitTestInvisible()
    {
        // Identical to the bitmap-path test — same contract for the
        // template clone: under no circumstances should the ghost
        // intercept hit-tests intended for cards underneath.
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;
        var template = new Avalonia.Controls.Templates.FuncDataTemplate<string>(
            (data, _) => new Avalonia.Controls.TextBlock { Text = data });

        overlay.BeginWithTemplate(target, SyntheticMove(target, 5, 5), template, "x");

        ((Control)layer.Children[^1]).IsHitTestVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Dispose_RemovesActiveGhost()
    {
        var (_, target, layer) = CreateWindowedTarget();
        var overlay = GhostDragOverlay.For(target)!;
        overlay.Begin(target, SyntheticMove(target, 10, 10));
        int afterBegin = layer.Children.Count;

        overlay.Dispose();

        layer.Children.Count.Should().Be(afterBegin - 1);
        overlay.IsActive.Should().BeFalse();
    }
}
