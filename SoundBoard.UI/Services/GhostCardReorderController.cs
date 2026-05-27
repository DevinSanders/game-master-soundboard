using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundBoard.UI.Controls;

namespace SoundBoard.UI.Services;

/// <summary>
/// Glue between a view's items panel, its per-item VM type, and the
/// <see cref="GhostDragOverlay"/>. Wraps the click-vs-drag discriminator,
/// pointer capture, ghost lifecycle, midpoint-free live shuffle, and
/// commit-on-release / abort-on-capture-lost so each view only needs to
/// construct one of these and forward four pointer events. Used by every
/// intra-window reorder site (shortcut grid, popped shortcut page, FX
/// chain, preset editor, playlist editor).
///
/// <para><b>Wiring contract.</b> Construct one in the view's constructor
/// (or first attach) with delegates that resolve the items panel + its
/// item template, and that perform the actual collection.Move and
/// PersistOrder calls on the VM. Then wire the four pointer events on
/// the items panel with <c>RoutingStrategies.Tunnel</c> — tunneling
/// matters because the items panel hosts Buttons (and other class-handled
/// controls) whose own handlers would otherwise consume the pointer
/// stream before we see it.</para>
///
/// <para><b>Why a class and not a static helper.</b> The drag holds
/// state across events (which VM is being dragged, whether the ghost is
/// active, the DragInitiator's press timestamp). Static methods would
/// require the view to maintain that state itself — same pattern five
/// times. One instance per view collapses that to one closure-bag.</para>
///
/// <para><b>Cross-window adds.</b> This controller deliberately knows
/// nothing about <c>DragDrop.DoDragDropAsync</c>. Cross-window flows
/// (Library / Presets / Playlists list drag sources dropping into a
/// page) keep using OS DnD via their own per-view handlers — that's the
/// hybrid contract: ghost overlay for intra-window reorder, OS DnD for
/// cross-window adds.</para>
/// </summary>
public sealed class GhostCardReorderController<TCardVm> where TCardVm : class
{
    private readonly Visual _root;
    private readonly DragInitiator _drag;
    private readonly Func<ItemsControl?> _getItems;
    private readonly Func<IDataTemplate?> _getTemplate;
    private readonly Action<TCardVm, TCardVm> _moveVisually;
    private readonly Action _persistOrder;
    private readonly Func<TCardVm, Control>? _buildGhostContent;

    private GhostDragOverlay? _ghostOverlay;
    private TCardVm? _draggingVm;
    private bool _ghostDrag;

    /// <param name="root">Visual reference for <see cref="DragInitiator"/>
    /// distance / hold computations. Usually the view itself.</param>
    /// <param name="getItems">Lazy accessor for the items panel — the
    /// view may not have looked it up at construction time. Evaluated
    /// on every event so a hot-reloaded items panel is picked up too.</param>
    /// <param name="getTemplate">Lazy accessor for the items panel's
    /// item template. <see cref="GhostDragOverlay.BeginWithTemplate"/>
    /// uses this to clone the dragged card via Avalonia's normal render
    /// pipeline (DPI-correct, unlike <see cref="System.Drawing.Bitmap"/>
    /// snapshot — Avalonia issue 17235 makes RenderTargetBitmap blurry
    /// above 100% scale). When the accessor returns null we fall back to
    /// the bitmap path.</param>
    /// <param name="moveVisually">Reorder callback: source &amp; target
    /// VMs, the VM should <c>ObservableCollection.Move</c> the source to
    /// the target's slot. Called many times per drag.</param>
    /// <param name="persistOrder">Single-shot commit on PointerReleased.
    /// Called once after the drag settles; the VM writes the final
    /// Order column values.</param>
    /// <param name="drag">Optional shared <see cref="DragInitiator"/> if
    /// the view wants to tune thresholds. Defaults to a fresh instance
    /// using <see cref="UiConstants.CardDragMinDistance"/>.</param>
    /// <param name="buildGhostContent">Optional override for the ghost
    /// visual. Most views want the default behavior (clone the live card
    /// via <paramref name="getTemplate"/> against the dragged VM); the
    /// override is for cards whose VM exposes a shared <see cref="Control"/>
    /// instance (e.g. <c>AttachedSamplerViewModel.Control</c>, the
    /// plugin-supplied editor). Re-templating against such a VM would try
    /// to give one Control two visual parents and crash. The override
    /// lets the view build a card-shaped placeholder that doesn't
    /// reference the shared instance.</param>
    public GhostCardReorderController(
        Visual root,
        Func<ItemsControl?> getItems,
        Func<IDataTemplate?> getTemplate,
        Action<TCardVm, TCardVm> moveVisually,
        Action persistOrder,
        DragInitiator? drag = null,
        Func<TCardVm, Control>? buildGhostContent = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
        _getTemplate = getTemplate ?? throw new ArgumentNullException(nameof(getTemplate));
        _moveVisually = moveVisually ?? throw new ArgumentNullException(nameof(moveVisually));
        _persistOrder = persistOrder ?? throw new ArgumentNullException(nameof(persistOrder));
        _drag = drag ?? new DragInitiator { MinDistance = UiConstants.CardDragMinDistance };
        _buildGhostContent = buildGhostContent;
    }

    /// <summary>Convenience: AddHandler all four pointer events with
    /// <see cref="RoutingStrategies.Tunnel"/> on <paramref name="itemsPanel"/>.
    /// Equivalent to calling AddHandler four times by hand.</summary>
    public void Attach(InputElement itemsPanel)
    {
        if (itemsPanel == null) throw new ArgumentNullException(nameof(itemsPanel));
        itemsPanel.AddHandler(InputElement.PointerPressedEvent,     OnPointerPressed,     RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerMovedEvent,       OnPointerMoved,       RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerReleasedEvent,    OnPointerReleased,    RoutingStrategies.Tunnel);
        itemsPanel.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);
    }

    /// <summary>Walk up from <paramref name="start"/> looking for a
    /// Control whose DataContext is a <typeparamref name="TCardVm"/>.
    /// Returns null when the event originated outside a card (page tabs,
    /// header chrome, etc.).</summary>
    private static Control? FindCard(Visual start)
    {
        foreach (var v in start.GetSelfAndVisualAncestors())
        {
            if (v is Control c && c.DataContext is TCardVm) return c;
        }
        return null;
    }

    public void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual v) return;
        var card = FindCard(v);
        if (card == null) return;

        // Don't arm if the press landed on an interactive descendant of
        // the card (a slider thumb, toggle, sub-button etc.). The walk
        // must stop AT the card, not past it — for shortcut grids the
        // card itself IS a Button, and treating that as "interactive"
        // would block every reorder gesture.
        if (IsInteractiveBetween(e.Source, card)) { _drag.Reset(); return; }
        _drag.NotifyPressed(e, _root);
    }

    /// <summary>True if there's a Slider / Thumb / Track / Button /
    /// TextBox / ComboBox / ScrollBar strictly between
    /// <paramref name="source"/> (inclusive) and
    /// <paramref name="cardBoundary"/> (exclusive). The card itself never
    /// counts as interactive — a card whose root happens to be a Button
    /// (shortcut grid) is still a draggable card.</summary>
    private static bool IsInteractiveBetween(object? source, Control cardBoundary)
    {
        var current = source as Avalonia.LogicalTree.ILogical;
        while (current != null)
        {
            if (ReferenceEquals(current, cardBoundary)) return false;
            switch (current)
            {
                case Avalonia.Controls.Slider:
                case Avalonia.Controls.Primitives.Thumb:
                case Avalonia.Controls.Primitives.Track:
                case Avalonia.Controls.Button:
                case Avalonia.Controls.TextBox:
                case Avalonia.Controls.ComboBox:
                case Avalonia.Controls.Primitives.ScrollBar:
                    return true;
            }
            current = current.LogicalParent;
        }
        return false;
    }

    public void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var items = _getItems();
        if (items == null) return;

        if (_ghostDrag)
        {
            // Reposition the ghost and shuffle the underlying collection
            // when the pointer is over a different card. InputHitTest
            // ignores the overlay (it's IsHitTestVisible=false), so the
            // hit is always the card underneath.
            _ghostOverlay?.Update(e);

            if (_draggingVm != null)
            {
                var pos = e.GetPosition(items);
                if (items.InputHitTest(pos) is Visual hv
                    && FindCard(hv) is { DataContext: TCardVm targetVm }
                    && !ReferenceEquals(targetVm, _draggingVm))
                {
                    _moveVisually(_draggingVm, targetVm);
                }
            }
            return;
        }

        // Pre-threshold: decide whether this gesture has earned a drag.
        // NB: no IsInteractiveBetween check here — once we've armed on a
        // non-interactive PRESS, the user can sweep the pointer through
        // child Buttons / ToggleSwitches mid-drag without the gesture
        // being silently disarmed. FX chain card headers pack ▲ ▼ 🗑
        // buttons together; without this relaxation a drag-reorder
        // gesture almost always passes over one of them and never
        // promotes. The original press-time guard already filtered the
        // gesture out if the user *meant* to click an interactive child.
        if (e.Source is not Visual sv) return;
        if (FindCard(sv) is not { DataContext: TCardVm sourceVm } sourceCard) return;
        if (!_drag.ShouldStartDrag(e, _root)) return;
        _drag.MarkDragStarted();

        _ghostOverlay ??= GhostDragOverlay.For(_root);
        if (_ghostOverlay == null) return;

        // Three rendering paths, in order of preference:
        // 1. Custom buildGhostContent — for VMs that expose shared
        //    Control instances (would crash a template clone).
        // 2. Template clone — DPI-correct, used by most views.
        // 3. RenderTargetBitmap — last resort, blurs on HiDPI.
        if (_buildGhostContent != null)
        {
            var content = _buildGhostContent(sourceVm);
            _ghostOverlay.BeginWithContent(sourceCard, e, content);
        }
        else if (_getTemplate() is { } template)
        {
            _ghostOverlay.BeginWithTemplate(sourceCard, e, template, sourceVm);
        }
        else
        {
            _ghostOverlay.Begin(sourceCard, e);
        }

        _draggingVm = sourceVm;
        _ghostDrag = true;

        // Capture to the items panel (not the card itself) so the
        // underlying Button / interactive content never receives the
        // release event — prevents phantom Click at end of drag.
        e.Pointer.Capture(items);
    }

    public void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_ghostDrag) return;
        End(commit: true);

        // Swallow the release so the captured panel's children (Buttons
        // in particular) don't fire Click at the end of a reorder.
        e.Handled = true;
    }

    public void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Capture revoked (window deactivated, another control took
        // input, etc.). End the ghost cleanly but don't persist —
        // we don't know whether the user meant to commit.
        if (!_ghostDrag) return;
        End(commit: false);
    }

    private void End(bool commit)
    {
        _ghostOverlay?.End();
        _ghostDrag = false;
        _draggingVm = null;
        _drag.Reset();
        if (commit) _persistOrder();
    }
}
