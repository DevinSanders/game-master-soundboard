using System;

namespace SoundBoard.UI.Services;

/// <summary>
/// Centralised timing / distance / size constants. Each value was
/// previously a literal at one or more call sites; pulling them here
/// makes "tune the whole app's feel" a one-line edit and removes the
/// risk of one site drifting from another (e.g. an editor tick at 100 ms
/// but a debounce at 250 ms — neither obvious from the call site).
/// </summary>
public static class UiConstants
{
    // ── Editor persistence ─────────────────────────────────────────────

    /// <summary>How long the debouncer waits for keystrokes/drags to
    /// settle before flushing edits to disk. <c>EditPersistence</c>
    /// default and the per-editor schedules all read this.</summary>
    public static readonly TimeSpan PersistDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the sampler editor pushes live config to alive
    /// ephemeral instances. Tradeoff: smaller → audio responds to slider
    /// drags faster, but more dispatcher pressure when the editor is
    /// open.</summary>
    public static readonly TimeSpan SamplerEditorTick = TimeSpan.FromMilliseconds(100);

    /// <summary>Mixer card position/seek-bar update interval.</summary>
    public static readonly TimeSpan TelemetryTick = TimeSpan.FromMilliseconds(100);

    // ── Drag-drop discrimination ───────────────────────────────────────

    /// <summary>Time the user must hold the pointer before a press
    /// promotes to a drag (OR semantics with <see cref="DragMinDistance"/>).
    /// Tuned so accidental drags during click-to-play are rare but
    /// intentional drags feel immediate.</summary>
    public const int DragMinHoldMs = 180;

    /// <summary>Pixel distance the pointer must move before a press
    /// promotes to a drag. Default for sources without interactive
    /// children.</summary>
    public const double DragMinDistance = 12.0;

    /// <summary>Larger drag-distance threshold for sources whose body
    /// contains interactive children (sliders, toggles). Reduces the
    /// chance a small stray motion off a slider thumb gets misclassified
    /// as a card-reorder gesture.</summary>
    public const double CardDragMinDistance = 18.0;

    // ── Ghost-drag overlay ─────────────────────────────────────────────

    /// <summary>Opacity of the ghost visual that follows the pointer
    /// during an intra-window drag-reorder. 1.0 reads as "the original
    /// jumped out of the grid"; very low fades into the background. 0.85
    /// keeps it recognisable while making it visually distinct from
    /// settled items.</summary>
    public const double GhostOpacity = 0.85;

    /// <summary>Blur radius (DIPs) for the drop shadow under the ghost.
    /// Visually anchors the ghost above the surface so it doesn't look
    /// like a flat overlay.</summary>
    public const double GhostShadowBlur = 12.0;

    /// <summary>Vertical offset (DIPs) of the ghost's drop shadow. Larger
    /// values read as "lifted higher off the surface."</summary>
    public const double GhostShadowOffsetY = 4.0;

    // ── Audio infrastructure ───────────────────────────────────────────

    /// <summary>Broadcast PCM-out queue cap (chunks) — consumed by bridge
    /// plugins (Discord / Zoom / Mumble / …). Beyond this the producer
    /// drops the chunk rather than block the audio thread.</summary>
    public const int BroadcastQueueCap = 50;
}
