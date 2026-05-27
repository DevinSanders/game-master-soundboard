using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace SoundBoard.UI.Services;

/// <summary>
/// Polished intra-window drag visual: spawns a snapshot of the source
/// element into the window's <see cref="OverlayLayer"/> and follows the
/// pointer until <see cref="End"/>. Pairs with a per-view reorder
/// controller (currently <c>ShortcutsView</c>) that hit-tests the items
/// panel on each pointer move and calls <c>ObservableCollection.Move</c>
/// so the underlying items shift live around the ghost.
///
/// <para><b>Why not <c>DragDrop.DoDragDropAsync</c>?</b> That API hands off
/// to the OS pipeline, which draws its own cursor and does not let us
/// substitute a custom visual. For polished card-reorder UX we drive the
/// drag ourselves: pointer capture + manual ghost positioning + manual
/// hit-testing. The OS DnD path is still used for cross-window drops
/// (Library → Soundboard, Presets → Playlist, etc.) where a custom
/// follower can't follow the pointer across window boundaries — the
/// hybrid contract: ghost overlay for intra-window reorder, OS DnD for
/// cross-window adds.</para>
///
/// <para><b>OverlayLayer vs. AdornerLayer.</b> <see cref="OverlayLayer"/>
/// is the right host for a free-floating follower: it has no "adorned
/// element" coupling, children are positioned with <c>Canvas.Left/Top</c>,
/// and it draws above every adorned element. <see cref="AdornerLayer"/>
/// would tether the ghost to a specific adorned element and follow its
/// layout — wrong for a pointer-following floater.</para>
///
/// <para><b>DPI / scaling — prefer <see cref="BeginWithTemplate"/>.</b>
/// Per <a href="https://github.com/AvaloniaUI/Avalonia/issues/17235">Avalonia
/// issue #17235</a>, Avalonia's renderer ignores non-96 DPI metadata
/// when DISPLAYING bitmaps. That means a high-resolution
/// <see cref="RenderTargetBitmap"/> snapshot can't be made pixel-crisp
/// above 100% scaling by tagging it with a higher DPI — the renderer
/// downscales it on display. The clean fix is to skip rasterisation
/// entirely: clone the source by re-instantiating its
/// <see cref="IDataTemplate"/> with the same <c>DataContext</c> and let
/// Avalonia draw the clone the normal way. That path is DPI-correct
/// because the regular renderer handles scaling. <see cref="Begin"/>'s
/// bitmap path is retained as a fallback for sources without a usable
/// template (e.g. one-off Borders), but visibly blurs above 100%.</para>
///
/// <para>Lifecycle: <see cref="Begin"/> / <see cref="BeginWithTemplate"/> spawn the ghost and store the
/// pointer's offset within the source (so it stays under the same point
/// of the dragged element). <see cref="Update"/> repositions on every
/// PointerMoved. <see cref="End"/> removes the ghost. Multiple Begin
/// calls without an End are tolerated — the previous ghost is removed
/// first. Disposal cleans up any in-flight ghost.</para>
/// </summary>
public sealed class GhostDragOverlay : IDisposable
{
    private readonly TopLevel _topLevel;
    private readonly OverlayLayer _layer;

    private Border? _ghost;

    /// <summary>Where the pointer was within the source element when the
    /// drag started, in source-local coordinates. We subtract this from
    /// the pointer's overlay-space position on every Update so the ghost
    /// stays anchored under the same point of the dragged visual.</summary>
    private Point _grabOffset;

    public GhostDragOverlay(TopLevel topLevel, OverlayLayer layer)
    {
        _topLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
    }

    /// <summary>Convenience: resolve the OverlayLayer for the given visual's
    /// TopLevel and construct an overlay. Returns null when the visual is
    /// detached (no TopLevel) or the TopLevel has no overlay layer (would
    /// be very unusual; defensive against test environments).</summary>
    public static GhostDragOverlay? For(Visual visual)
    {
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel == null) return null;
        var layer = OverlayLayer.GetOverlayLayer(visual);
        if (layer == null) return null;
        return new GhostDragOverlay(topLevel, layer);
    }

    /// <summary>True while a ghost is being shown.</summary>
    public bool IsActive => _ghost != null;

    /// <summary>Snapshot <paramref name="source"/> via
    /// <see cref="RenderTargetBitmap"/>, wrap it in a styled floater,
    /// and add it to the overlay layer. This path is visibly blurry on
    /// displays above 100% scaling — Avalonia's renderer ignores
    /// non-96 DPI metadata when displaying bitmaps. Prefer
    /// <see cref="BeginWithTemplate"/> when a data template is available.
    /// Retained as a fallback for one-off visuals that aren't template-driven.</summary>
    public void Begin(Visual source, PointerEventArgs e)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        RemoveGhost();
        var size = source.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var scaling = _topLevel.RenderScaling;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(size.Width  * scaling)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
        var dpi = new Vector(96 * scaling, 96 * scaling);

        RenderTargetBitmap rtb;
        try
        {
            rtb = new RenderTargetBitmap(pixelSize, dpi);
            rtb.Render(source);
        }
        catch (Exception)
        {
            // RenderTargetBitmap can throw on headless or detached visuals.
            // Swallow and bail — tests verify lifecycle, not snapshot pixels.
            return;
        }

        var content = new Image
        {
            Source = rtb,
            Stretch = Stretch.Fill,
        };
        AttachGhost(content, size, e.GetPosition(source), e);
    }

    /// <summary>Use <paramref name="content"/> directly as the ghost
    /// visual, sized to the source's DIP bounds and wrapped in the
    /// standard shadow/opacity Border. The caller owns the content — it
    /// must be a freshly-constructed Control with no other parent. This
    /// is the escape hatch for VMs whose data templates would crash if
    /// re-instantiated (e.g. <c>AttachedSamplerViewModel</c> exposes a
    /// shared plugin Control that can't have two visual parents). The
    /// view supplies a card-shaped placeholder via
    /// <see cref="GhostCardReorderController{TCardVm}"/>'s
    /// <c>buildGhostContent</c> callback.</summary>
    public void BeginWithContent(Visual source, PointerEventArgs e, Control content)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (content == null) throw new ArgumentNullException(nameof(content));

        RemoveGhost();
        var size = source.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        AttachGhost(content, size, e.GetPosition(source), e);
    }

    /// <summary>Clone <paramref name="source"/>'s visual by instantiating
    /// <paramref name="template"/> against <paramref name="dataContext"/>
    /// and use the resulting Control as the ghost. This is the
    /// DPI-correct path: Avalonia renders the clone through its normal
    /// pipeline, so it stays crisp at any display scale. Pass the source's
    /// <see cref="ItemsControl.ItemTemplate"/> and its
    /// <c>ShortcutButtonViewModel</c> (or equivalent per-item VM) — the
    /// resulting clone has identical bindings to the live button.
    ///
    /// <para>Because the ghost is hit-test-invisible, the cloned Button's
    /// Command never fires and its <c>:pointerover</c> state never
    /// activates — so the clone shows the rest appearance even if the
    /// original was being hovered when the drag began.</para></summary>
    public void BeginWithTemplate(Visual source, PointerEventArgs e, IDataTemplate template, object dataContext)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (template == null) throw new ArgumentNullException(nameof(template));

        RemoveGhost();

        // Size the ghost to the source's LAYOUT SLOT (its immediate
        // Visual parent — the ContentPresenter for an ItemsControl
        // child), not source.Bounds itself. Background: a template's
        // outer element typically declares a Margin (the shortcut
        // Button has Margin="10"), which the layout system honors by
        // reserving space OUTSIDE source.Bounds. If we size the ghost
        // to source.Bounds and then re-instantiate the same template
        // inside, the cloned outer element's Margin shrinks the
        // visible content again — producing a ghost noticeably smaller
        // than the original. Sizing to the parent gives the cloned
        // Margin the same space it has in the live layout, so the
        // ghost renders at exactly the same size as the source. For
        // sources without a Visual parent (detached, top-level), fall
        // back to source.Bounds.
        Visual sizingRef = source.GetVisualParent() as Visual ?? source;
        var size = sizingRef.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            sizingRef = source;
            size = source.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
        }

        var content = new ContentControl
        {
            ContentTemplate = template,
            Content = dataContext,
            Width = size.Width,
            Height = size.Height,
        };
        AttachGhost(content, size, e.GetPosition(sizingRef), e);
    }

    /// <summary>Shared "wrap content + add to overlay" body for both Begin
    /// variants. Builds the outer Border with shadow / opacity / hit-test
    /// invisibility, captures the grab offset, and seeds the position
    /// via <see cref="Update"/>.</summary>
    private void AttachGhost(Control content, Size size, Point grabOffset, PointerEventArgs e)
    {
        _ghost = new Border
        {
            Width = size.Width,
            Height = size.Height,
            Opacity = UiConstants.GhostOpacity,
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = UiConstants.GhostShadowBlur,
                OffsetY = UiConstants.GhostShadowOffsetY,
                Color = Color.FromArgb(180, 0, 0, 0),
            }),
            Child = content,
        };

        _grabOffset = grabOffset;
        _layer.Children.Add(_ghost);
        Update(e);
    }

    /// <summary>Move the ghost so it stays anchored under the same point
    /// of the original element. No-op when no ghost is active.</summary>
    public void Update(PointerEventArgs e)
    {
        if (_ghost == null) return;
        var pos = e.GetPosition(_layer);
        Canvas.SetLeft(_ghost, pos.X - _grabOffset.X);
        Canvas.SetTop(_ghost, pos.Y - _grabOffset.Y);
    }

    /// <summary>Remove the ghost from the overlay. Safe to call when
    /// <see cref="IsActive"/> is false.</summary>
    public void End() => RemoveGhost();

    private void RemoveGhost()
    {
        if (_ghost == null) return;
        _layer.Children.Remove(_ghost);
        _ghost = null;
    }

    public void Dispose() => RemoveGhost();
}
