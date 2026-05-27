using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Pins the <see cref="EditorSaveExtensions.EditorSave{T}"/> contract: open
/// a fresh context, <c>Find</c> the row by id, run the mutation, save. The
/// helper replaces ~15 near-identical blocks across editor VMs; a regression
/// here would silently turn slider edits into no-ops, which is exactly the
/// kind of silent data-loss bug the detached-graph hazard (mutate via
/// Find, never Update a detached graph) is meant to prevent.
/// </summary>
public class EditorSaveExtensionsTests
{
    [Fact]
    public void EditorSave_RunsMutationAndPersists()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using (var seed = fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Id = 1, Name = "Original", FilePath = "x.mp3" });
            seed.SaveChanges();
        }

        var ok = fx.Factory.EditorSave<Track>(1, t => t.Name = "Renamed");

        ok.Should().BeTrue();
        using var assert = fx.CreateContext();
        assert.Tracks.Find(1)!.Name.Should().Be("Renamed");
    }

    [Fact]
    public void EditorSave_MissingRow_ReturnsFalseAndDoesNothing()
    {
        // Legitimate race: row was deleted between editor open and save fire.
        // Helper must no-op, not throw — otherwise the EditPersistence
        // debouncer would log a noisy error every time a user deletes
        // something while another editor is still open on it.
        using var fx = new SqliteInMemoryDbFixture();

        var ok = fx.Factory.EditorSave<Track>(999, t => t.Name = "Should never run");

        ok.Should().BeFalse();
    }

    [Fact]
    public void EditorSave_OpensFreshContext_PerCall()
    {
        // Each save must use its own context — a long-lived shared context
        // would accumulate tracked entities and eventually throw "instance
        // with same key already tracked." This test verifies isolation by
        // running two saves back-to-back against the same id.
        using var fx = new SqliteInMemoryDbFixture();
        using (var seed = fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Id = 2, Name = "v0", FilePath = "y.mp3" });
            seed.SaveChanges();
        }

        fx.Factory.EditorSave<Track>(2, t => t.Name = "v1");
        fx.Factory.EditorSave<Track>(2, t => t.Name = "v2");

        using var assert = fx.CreateContext();
        assert.Tracks.Find(2)!.Name.Should().Be("v2");
    }

    [Fact]
    public void EditorSave_DoesNotAttachNavigationGraph()
    {
        // The whole reason this helper exists: editor VMs hold detached
        // graphs (Preset → PresetTracks → Track). If the helper attached
        // the graph, the second save in the same context would throw. We
        // mutate one column on a Preset whose Tracks aren't even loaded by
        // the caller — the helper should care only about the Preset row.
        using var fx = new SqliteInMemoryDbFixture();
        using (var seed = fx.CreateContext())
        {
            seed.Tracks.Add(new Track { Id = 10, Name = "T1", FilePath = "a.mp3" });
            seed.Presets.Add(new Preset { Id = 5, Name = "P1" });
            seed.PresetTracks.Add(new PresetTrack { Id = 100, PresetId = 5, TrackId = 10, Order = 0 });
            seed.SaveChanges();
        }

        var act = () => fx.Factory.EditorSave<Preset>(5, p => p.Name = "P1-renamed");
        act.Should().NotThrow();

        using var assert = fx.CreateContext();
        assert.Presets.Find(5)!.Name.Should().Be("P1-renamed");
        // The PresetTrack row is untouched (no spurious save through the graph).
        assert.PresetTracks.Find(100)!.Order.Should().Be(0);
    }
}
