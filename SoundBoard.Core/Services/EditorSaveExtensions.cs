using System;
using Microsoft.EntityFrameworkCore;

namespace SoundBoard.Core.Services;

/// <summary>
/// Helpers for the editor save pattern:
/// open a fresh context, <c>Find</c> a tracked entity by id, mutate the
/// columns we care about, save. Never <c>db.Set&lt;T&gt;().Update(detached)</c>
/// because that re-attaches the entire navigation graph and conflicts with
/// other tracked entities in the same SaveChanges.
///
/// <para>Every editor VM (TrackEditor, PresetEditor, PlaylistsViewModel,
/// PresetTrackCardViewModel, ShortcutsViewModel, etc.) had a near-identical
/// 5-line block; the helper collapses it to one line at every call site.</para>
/// </summary>
public static class EditorSaveExtensions
{
    /// <summary>Find a tracked <typeparamref name="T"/> by integer primary
    /// key, run <paramref name="mutate"/> against it, and save. Returns
    /// false (no-op) if the row was deleted between the editor opening
    /// and the save firing — that's a legitimate race when two windows
    /// touch the same entity. Throws nothing extra: any EF exception
    /// during SaveChanges propagates so the caller's debouncer can log it
    /// the same as before.</summary>
    public static bool EditorSave<T>(
        this ISoundBoardDbContextFactory factory,
        int id,
        Action<T> mutate) where T : class
    {
        using var db = factory.CreateDbContext();
        var tracked = db.Set<T>().Find(id);
        if (tracked == null) return false;
        mutate(tracked);
        db.SaveChanges();
        return true;
    }
}
