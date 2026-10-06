using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SoundBoard.UI.Services;

/// <summary>
/// Locked-board touch gesture: a long-press (hold) on a shortcut card fires
/// <paramref name="onLongPress"/> (used to Stop the card's target) and
/// suppresses the default long-press → context-menu.
///
/// <para>Built on Avalonia's platform <see cref="Gestures.HoldingEvent"/>
/// rather than a raw <c>DispatcherTimer</c>: on Windows the OS turns a touch
/// press-and-hold into a right-click, which pre-empts a timer and pops the
/// context menu. The Holding gesture is recognised by the platform input
/// layer (fires for touch / pen; mouse only if IsHoldWithMouseEnabled), and
/// marking the event handled stops Avalonia raising the follow-on
/// ContextRequested. Enable recognition by setting
/// <c>IsHoldingEnabled="True"</c> on each card.</para>
///
/// <para>Active only while <paramref name="isEnabled"/> returns true (board
/// locked); when unlocked the <see cref="GhostCardReorderController{TCardVm}"/>
/// owns the gesture (drag-reorder) and this stays inert.</para>
/// </summary>
public sealed class LongPressStopController<TCardVm> where TCardVm : class
{
    private readonly Func<bool> _isEnabled;
    private readonly Action<TCardVm> _onLongPress;

    private InputElement? _panel;
    private IPointer? _pointer;

    public LongPressStopController(Func<bool> isEnabled, Action<TCardVm> onLongPress)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _onLongPress = onLongPress ?? throw new ArgumentNullException(nameof(onLongPress));
    }

    /// <summary>Listen for the Holding gesture as it bubbles up from the cards
    /// to the items panel. A tunnelled PointerPressed also records the live
    /// pointer so a fired hold can capture it (see <see cref="OnHolding"/>).</summary>
    public void Attach(InputElement itemsPanel)
    {
        _panel = itemsPanel;
        itemsPanel.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.HoldingEvent, OnHolding, RoutingStrategies.Bubble);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e) => _pointer = e.Pointer;

    private static Control? FindCard(Visual start)
    {
        foreach (var v in start.GetSelfAndVisualAncestors())
            if (v is Control c && c.DataContext is TCardVm) return c;
        return null;
    }

    /// <summary>True when an interactive child (the corner "⋮" button, etc.)
    /// sits between the gesture source and the card — a hold there should not
    /// Stop. The card itself (a Button) is never treated as a child control.</summary>
    private static bool OnChildControl(object? source, Control card)
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
                case Avalonia.Controls.Primitives.ScrollBar:
                    return true;
            }
            current = current.LogicalParent;
        }
        return false;
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (!_isEnabled()) return;
        if (e.HoldingState != HoldingState.Started) return;
        if (e.Source is not Visual v) return;
        var card = FindCard(v);
        if (card is null || OnChildControl(e.Source, card)) return;
        if (card.DataContext is not TCardVm vm) return;

        _onLongPress(vm);
        e.Handled = true; // swallow so the OS long-press context menu doesn't open

        // Capture the still-down pointer to the panel so the card Button
        // receives PointerCaptureLost instead of a release — otherwise it
        // raises Click on finger-up and re-toggles play/pause right after the
        // Stop. The drag controller uses the same trick.
        _pointer?.Capture(_panel);
    }
}
