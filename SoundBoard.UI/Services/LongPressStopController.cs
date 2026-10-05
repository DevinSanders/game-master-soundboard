using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoundBoard.UI.Services;

/// <summary>
/// Locked-board touch gesture: a long-press (hold without moving) on a
/// shortcut card fires <paramref name="onLongPress"/> — used to Stop the
/// card's target — and suppresses the tap's play/pause. Active only while
/// the supplied predicate returns true (board locked); when unlocked the
/// <see cref="GhostCardReorderController{TCardVm}"/> owns long-press
/// (drag-reorder) and this controller stays inert.
///
/// <para>Wire the four pointer events with <see cref="RoutingStrategies.Tunnel"/>
/// on the items panel — same reason as the reorder controller: the cards are
/// Buttons that consume the pointer stream at the bubble phase.</para>
/// </summary>
public sealed class LongPressStopController<TCardVm> where TCardVm : class
{
    // A deliberate hold so a normal tap-and-release never trips Stop; set
    // above the reorder arm time so the two gestures don't feel the same.
    private const double HoldMs = 500;
    private const double MoveCancelPx = 12;

    private readonly Func<ItemsControl?> _getItems;
    private readonly Func<bool> _isEnabled;
    private readonly Action<TCardVm> _onLongPress;
    private readonly DispatcherTimer _timer;

    private IPointer? _pointer;
    private TCardVm? _card;
    private Point _pressPos;
    private bool _fired;

    public LongPressStopController(Func<ItemsControl?> getItems, Func<bool> isEnabled, Action<TCardVm> onLongPress)
    {
        _getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _onLongPress = onLongPress ?? throw new ArgumentNullException(nameof(onLongPress));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _timer.Tick += OnTick;
    }

    public void Attach(InputElement itemsPanel)
    {
        itemsPanel.AddHandler(InputElement.PointerPressedEvent,     OnPressed,     RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerMovedEvent,       OnMoved,       RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerReleasedEvent,    OnReleased,    RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel);
    }

    private static Control? FindCard(Visual start)
    {
        foreach (var v in start.GetSelfAndVisualAncestors())
            if (v is Control c && c.DataContext is TCardVm) return c;
        return null;
    }

    /// <summary>True if an interactive control (the corner "⋮" button, a
    /// slider, etc.) sits strictly between the press source and the card —
    /// the card itself (a Button) never counts, so a press on the card body
    /// still arms the long-press.</summary>
    private static bool PressedOnChildControl(object? source, Control card)
    {
        var current = source as Avalonia.LogicalTree.ILogical;
        while (current != null)
        {
            if (ReferenceEquals(current, card)) return false;
            switch (current)
            {
                case Button:
                case TextBox:
                case ComboBox:
                case Slider:
                case Avalonia.Controls.Primitives.Thumb:
                case Avalonia.Controls.Primitives.ScrollBar:
                    return true;
            }
            current = current.LogicalParent;
        }
        return false;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();
        if (!_isEnabled()) return;
        if (e.Source is not Visual v) return;
        var card = FindCard(v);
        if (card == null) return;
        if (PressedOnChildControl(e.Source, card)) return; // corner menu / child controls

        _pointer = e.Pointer;
        _card = card.DataContext as TCardVm;
        _pressPos = e.GetPosition(null);
        _fired = false;
        _timer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_timer.IsEnabled) return;
        var delta = e.GetPosition(null) - _pressPos;
        if (Math.Abs(delta.X) > MoveCancelPx || Math.Abs(delta.Y) > MoveCancelPx)
            Cancel(); // moved too far — treat as a scroll/pan, not a hold
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_card == null) return;

        // Capture to the panel so the underlying Button never receives the
        // release and can't fire its Click (play/pause) after the Stop.
        _pointer?.Capture(_getItems());
        _fired = true;
        _onLongPress(_card);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var fired = _fired;
        Cancel();
        if (fired) e.Handled = true; // swallow the tap that would have toggled play/pause
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Cancel();

    private void Cancel()
    {
        _timer.Stop();
        _pointer = null;
        _card = null;
        _fired = false;
    }
}
