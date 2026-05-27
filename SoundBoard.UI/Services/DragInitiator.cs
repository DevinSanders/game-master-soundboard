using Avalonia;
using Avalonia.Input;
using System;

namespace SoundBoard.UI.Services;

/// <summary>
/// Click-vs-drag discriminator. Tracks pointer press state and reports
/// when a gesture has crossed either threshold (hold time OR distance) to
/// count as a drag instead of a click.
///
/// Why two gates with OR semantics? The previous app-wide pattern fired on
/// any ~6px of jitter between press and release — so a slightly-shaky
/// click became a phantom drag and the Button's Click never fired (because
/// <see cref="DragDrop.DoDragDropAsync"/> had captured the pointer). The
/// fix is to widen the distance threshold past realistic click jitter
/// (12px) and add a time-based alternative for users who want
/// press-and-hold-to-drag without much motion. A fast reorder gesture
/// trips the distance gate immediately; a careful press-and-hold trips
/// the time gate; a normal click stays under both.
///
/// Pure AND semantics (both gates must pass) was tried first and broke
/// fast drag-reorder — most reorder gestures complete in &lt;180 ms total,
/// so the time gate never elapsed and the whole gesture was misclassified
/// as a click.
///
/// Usage per view:
/// <code>
/// private readonly DragInitiator _drag = new();
///
/// void OnPressed(object? s, PointerPressedEventArgs e) =&gt;
///     _drag.NotifyPressed(e, this);
///
/// async void OnMoved(object? s, PointerEventArgs e) {
///     if (!_drag.ShouldStartDrag(e, this)) return;
///     _drag.MarkDragStarted();
///     await DragDrop.DoDragDropAsync(_drag.SynthesizePressedArgs(e, this), data, effects);
/// }
/// </code>
/// State resets on the next <see cref="NotifyPressed"/>, so the view does
/// not need to wire <c>PointerReleased</c> just to clean up.
/// </summary>
public sealed class DragInitiator
{
    /// <summary>Hold-time gate. Once the pointer has been held longer than
    /// this (even without much movement), the next pointer-move arms drag.
    /// Lets users start a drag with a deliberate press-and-hold.</summary>
    public int MinHoldMs { get; set; } = UiConstants.DragMinHoldMs;

    /// <summary>Distance gate. Any movement beyond this on either axis
    /// arms drag immediately, regardless of hold time. Must be wider than
    /// realistic click jitter — 12 px catches most shaky hands, trackpad
    /// taps, and tap-with-tremor cases without rejecting intentional
    /// drag gestures.</summary>
    public double MinDistance { get; set; } = UiConstants.DragMinDistance;

    private Point _pressPosition;
    private DateTime _pressTime;
    private bool _armed;
    private bool _dragStarted;

    /// <summary>Call from <c>PointerPressed</c>. Captures the press
    /// position and timestamp and arms the discriminator.</summary>
    public void NotifyPressed(PointerPressedEventArgs e, Visual reference)
    {
        if (!e.GetCurrentPoint(reference).Properties.IsLeftButtonPressed)
        {
            _armed = false;
            _dragStarted = false;
            return;
        }

        _pressPosition = e.GetPosition(reference);
        _pressTime = DateTime.UtcNow;
        _armed = true;
        _dragStarted = false;
    }

    /// <summary>Returns true once *either* the hold-time gate or the
    /// distance gate has been crossed since the last
    /// <see cref="NotifyPressed"/>. The caller is responsible for
    /// invoking <see cref="MarkDragStarted"/> immediately before kicking
    /// off <see cref="DragDrop.DoDragDropAsync"/> so this method does not
    /// fire again during the async hand-off.</summary>
    public bool ShouldStartDrag(PointerEventArgs e, Visual reference)
    {
        if (!_armed || _dragStarted) return false;
        if (!e.GetCurrentPoint(reference).Properties.IsLeftButtonPressed) return false;

        var delta = e.GetPosition(reference) - _pressPosition;
        var distanceTripped = Math.Abs(delta.X) >= MinDistance || Math.Abs(delta.Y) >= MinDistance;

        var elapsedMs = (DateTime.UtcNow - _pressTime).TotalMilliseconds;
        var holdTripped = elapsedMs >= MinHoldMs;

        return distanceTripped || holdTripped;
    }

    /// <summary>Marks the drag as started. Subsequent
    /// <see cref="ShouldStartDrag"/> calls return false until the next
    /// <see cref="NotifyPressed"/>.</summary>
    public void MarkDragStarted() => _dragStarted = true;

    /// <summary>Resets state. Optional — <see cref="NotifyPressed"/> on the
    /// next gesture also resets. Wire this from <c>PointerCaptureLost</c>
    /// if you want to be defensive about drags interrupted by another
    /// control taking capture.</summary>
    public void Reset()
    {
        _armed = false;
        _dragStarted = false;
    }

    /// <summary><see cref="DragDrop.DoDragDropAsync"/> requires a
    /// <see cref="PointerPressedEventArgs"/>, but the discriminator fires
    /// during <c>PointerMoved</c> — we don't have the original press args
    /// anymore. Synthesize one from the current pointer state. This is the
    /// same workaround every drag site already used; centralised here so
    /// the boilerplate doesn't get duplicated in five views.</summary>
    public PointerPressedEventArgs SynthesizePressedArgs(PointerEventArgs e, Visual reference) =>
        new(
            e.Source!,
            e.Pointer,
            reference,
            e.GetPosition(reference),
            e.Timestamp,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            e.KeyModifiers);
}
