using Avalonia.Threading;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;

namespace SoundBoard.UI.Services;

/// <summary>
/// Debounced persistence helper used by the editor ViewModels (Track,
/// Preset, Playlist). Setters mutate the model + live audio synchronously
/// so the UI and playback stay responsive, then call
/// <see cref="Schedule"/> to defer the database write.
///
/// While the user is actively manipulating a control (e.g. dragging a
/// slider), the view wraps the interaction in <see cref="BeginBurst"/> /
/// <see cref="EndBurst"/>. Pending saves are held until the burst ends —
/// at which point everything is flushed in one batch. For non-burst
/// inputs (typing in a textbox, clicking a toggle) the
/// <see cref="DebounceInterval"/> timer fires shortly after the last
/// change and flushes.
///
/// Per-key last-write-wins: scheduling the same <c>key</c> replaces any
/// previously-pending save under that key. Each setter typically uses a
/// stable key like <c>"Track.Volume"</c> so a hundred slider ticks turn
/// into one DB write.
/// </summary>
public sealed class EditPersistence : IDisposable
{
    private static readonly TimeSpan DebounceInterval = UiConstants.PersistDebounce;

    private readonly DispatcherTimer _timer;
    // Stored as a field so Dispose can detach. Pre-fix this was an
    // anonymous lambda; the Dispatcher kept the timer (and the closure
    // capturing `this`) alive for the app's lifetime even after the
    // owning VM was supposedly torn down.
    private readonly EventHandler _tickHandler;
    private readonly Dictionary<string, Action> _pending = new();
    private int _burstDepth;
    private bool _disposed;

    public EditPersistence()
    {
        _timer = new DispatcherTimer { Interval = DebounceInterval };
        _tickHandler = (s, e) => Flush();
        _timer.Tick += _tickHandler;
    }

    /// <summary>Schedule a save under <paramref name="key"/>. If a save was
    /// already pending for that key it's replaced (last-write-wins). The
    /// timer is (re)started so the save fires after the debounce interval
    /// of quiet — unless a burst is in progress, in which case the save
    /// waits for <see cref="EndBurst"/>.
    /// <para>No-op after <see cref="Dispose"/> — a late slider tick that
    /// races a window close shouldn't resurrect a torn-down VM's timer.</para></summary>
    public void Schedule(string key, Action save)
    {
        if (_disposed) return;
        _pending[key] = save;
        if (_burstDepth > 0) return;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Mark the start of an interactive burst (slider drag, etc.).
    /// Pending saves are held until every matching <see cref="EndBurst"/>
    /// has been called. Reentrant — depth-counted.</summary>
    public void BeginBurst()
    {
        _burstDepth++;
        _timer.Stop();
    }

    /// <summary>End an interactive burst. When the burst depth returns to
    /// zero, any pending saves are flushed immediately.</summary>
    public void EndBurst()
    {
        if (_burstDepth > 0) _burstDepth--;
        if (_burstDepth == 0 && _pending.Count > 0) Flush();
    }

    /// <summary>Run every pending save immediately, regardless of timer or
    /// burst state. Call on window close / VM disposal so in-flight edits
    /// aren't lost if the app exits before the debounce fires.</summary>
    public void Flush()
    {
        _timer.Stop();
        if (_pending.Count == 0) return;
        var actions = new List<Action>(_pending.Values);
        _pending.Clear();
        foreach (var a in actions)
        {
            try { a(); }
            catch (Exception ex) { Log.Error("EditPersistence", "deferred save failed", ex); }
        }
    }

    /// <summary>Flush any pending saves, then detach the <see cref="DispatcherTimer"/>
    /// from the global dispatcher so the helper (and the VM that owns it)
    /// can be garbage-collected.
    /// <para>The pre-fix anonymous lambda subscription meant the timer
    /// kept the helper alive forever via <c>Dispatcher.Tick</c> →
    /// closure-capturing-<c>this</c>, and the helper kept its owning VM
    /// alive via the per-key <see cref="Action"/> delegates. Result:
    /// every Track / Bus editor open leaked one VM until process exit.</para>
    /// <para>Idempotent; safe to call multiple times.</para></summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Flush(); }
        catch (Exception ex) { Log.Warn("EditPersistence", $"Flush during Dispose threw: {ex.Message}"); }
        _timer.Stop();
        _timer.Tick -= _tickHandler;
        _pending.Clear();
    }
}
