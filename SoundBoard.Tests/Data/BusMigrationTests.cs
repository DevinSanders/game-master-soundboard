using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;
using SoundBoard.Core.Models;
using SoundBoard.Tests.Fakes;
using System.Data;
using System.Linq;

namespace SoundBoard.Tests.Data;

/// <summary>
/// Acceptance tests for the buses surface. Pins:
/// <list type="bullet">
///   <item><see cref="SchemaMigrations.Apply"/> seeds exactly the three
///   built-in buses (Music / Ambient / SFX) with the pinned
///   <see cref="BuiltInBusIds"/> values and unity volume.</item>
///   <item>Re-running <see cref="SchemaMigrations.Apply"/> is idempotent
///   — no duplicate seeds.</item>
///   <item>The new model columns (Track.BusId, Preset.BusIdOverride,
///   ShortcutButton.BusIdOverride) round-trip through EF correctly.</item>
///   <item><see cref="SamplerOwnerType.Bus"/> sits at the documented
///   ordinal (4) — downstream persistence depends on the integer value
///   not drifting.</item>
/// </list>
/// <para>The bus schema (Buses table + the BusId / BusIdOverride
/// columns) is built directly from the EF model by <c>EnsureCreated()</c>
/// — no migration rows are involved on a fresh install, and the
/// migration list is currently empty. The "migration recorded in
/// SchemaVersion" assertion these tests used to make is no longer
/// meaningful; see <see cref="SchemaMigrationsTests"/> for the
/// remaining infrastructure-level contracts.</para>
/// </summary>
public class BusMigrationTests
{
    [Fact]
    public void Apply_SeedsThreeBuiltInBuses()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        SchemaMigrations.Apply(db);

        var buses = db.Buses.OrderBy(b => b.Id).ToList();
        buses.Should().HaveCount(3, "the seed must create exactly Music + Ambient + SFX");

        buses[0].Id.Should().Be(BuiltInBusIds.Music);
        buses[0].Name.Should().Be("Music");
        buses[0].IsBuiltIn.Should().BeTrue();

        buses[1].Id.Should().Be(BuiltInBusIds.Ambient);
        buses[1].Name.Should().Be("Ambient");
        buses[1].IsBuiltIn.Should().BeTrue();

        buses[2].Id.Should().Be(BuiltInBusIds.Sfx);
        buses[2].Name.Should().Be("SFX");
        buses[2].IsBuiltIn.Should().BeTrue();
    }

    [Fact]
    public void Apply_IsIdempotent_NoDuplicateSeeds()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        SchemaMigrations.Apply(db);
        SchemaMigrations.Apply(db);
        SchemaMigrations.Apply(db);

        // Seed must short-circuit on "any rows already exist?" — three calls
        // should still leave three rows, not nine.
        db.Buses.Count().Should().Be(3);
    }

    [Fact]
    public void Track_BusId_DefaultsToMusic()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();
        SchemaMigrations.Apply(db);

        // Inserting a Track without setting BusId must land it on the
        // Music bus — the model's BusId default is BuiltInBusIds.Music.
        var t = new Track { Name = "x", FilePath = "/tmp/x.wav" };
        db.Tracks.Add(t);
        db.SaveChanges();

        var reloaded = db.Tracks.AsNoTracking().Single(x => x.Id == t.Id);
        reloaded.BusId.Should().Be(BuiltInBusIds.Music);
    }

    [Fact]
    public void Preset_BusIdOverride_IsNullable_AndDefaultsNull()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();
        SchemaMigrations.Apply(db);

        var p = new Preset { Name = "scene" };
        db.Presets.Add(p);
        db.SaveChanges();

        var reloaded = db.Presets.AsNoTracking().Single(x => x.Id == p.Id);
        reloaded.BusIdOverride.Should().BeNull(
            "presets must inherit per-track bus routing unless the user opts into an override");
    }

    [Fact]
    public void ShortcutButton_BusIdOverride_IsNullable_AndDefaultsNull()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();
        SchemaMigrations.Apply(db);

        var page = new ShortcutPage { Name = "Page 1" };
        db.ShortcutPages.Add(page);
        db.SaveChanges();

        var btn = new ShortcutButton { ShortcutPageId = page.Id, Row = 0, Column = 0 };
        db.ShortcutButtons.Add(btn);
        db.SaveChanges();

        var reloaded = db.ShortcutButtons.AsNoTracking().Single(x => x.Id == btn.Id);
        reloaded.BusIdOverride.Should().BeNull();
    }

    [Fact]
    public void SamplerOwnerType_Bus_HasOrdinalFour()
    {
        // The integer is what gets persisted into SamplerAttachment.OwnerType.
        // Reordering or renumbering the enum silently corrupts every saved
        // bus FX-chain attachment on next load, so pin the value explicitly.
        ((int)SamplerOwnerType.Bus).Should().Be(4);
    }

    [Fact]
    public void Apply_SeedsBuiltInBusesWithUnityVolume()
    {
        // Bus.Volume defaults to 1.0 (unity) in the EF model; the seed
        // sets the value explicitly. Non-unity defaults would silently
        // change every track's level on first launch.
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        SchemaMigrations.Apply(db);

        db.Buses.All(b => b.Volume == 1.0f).Should().BeTrue(
            "seeded bus rows must default to unity gain");
    }
}
