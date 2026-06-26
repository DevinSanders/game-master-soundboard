using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;
using SoundBoard.Tests.Fakes;
using System.Data;

namespace SoundBoard.Tests.Data;

/// <summary>
/// Pins the schema-migrations contract: a fresh DB has the
/// <c>SchemaVersion</c> table after Apply, applying twice is idempotent,
/// and a migration whose target column already exists (because
/// EnsureCreated built it from the EF model) gets baselined rather than
/// failing the run.
/// </summary>
public class SchemaMigrationsTests
{
    [Fact]
    public void Apply_CreatesSchemaVersionTable()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        SchemaMigrations.Apply(db);

        // After Apply, the SchemaVersion table must exist regardless of
        // whether any migrations were defined. The bootstrap is what
        // future migrations check against.
        TableExists(db, "SchemaVersion").Should().BeTrue();
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        var act = () =>
        {
            SchemaMigrations.Apply(db);
            SchemaMigrations.Apply(db);
            SchemaMigrations.Apply(db);
        };

        act.Should().NotThrow("running Apply repeatedly during startup must be safe");
    }

    [Fact]
    public void Apply_OnFreshDb_BaselinesExistingColumnsWithoutThrowing()
    {
        // Fresh in-memory DB: EnsureCreated has already built every
        // column from the EF model, so the ALTER statements in the
        // migration list hit "duplicate column name" errors. Apply must
        // recognise that as "EnsureCreated covered it" and record the
        // migration as applied, not fail the run.
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        var act = () => SchemaMigrations.Apply(db);

        act.Should().NotThrow();
    }

    [Fact]
    public void Apply_AddsIsHiddenColumn_OnLegacyShortcutPagesTable()
    {
        // Simulate a pre-migration DB: drop the IsHidden column EnsureCreated
        // already added, then run Apply and check the column is back. This
        // is the install-upgrade path — a user with an older DB launches a
        // build that introduces the column.
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        // Rebuild ShortcutPages without IsHidden by copying rows into a
        // staging table and renaming. SQLite has no DROP COLUMN; this is
        // the canonical workaround.
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ShortcutPages_legacy (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                OrderIndex INTEGER NOT NULL
            );
            INSERT INTO ShortcutPages_legacy (Id, Name, OrderIndex)
                SELECT Id, Name, OrderIndex FROM ShortcutPages;
            DROP TABLE ShortcutPages;
            ALTER TABLE ShortcutPages_legacy RENAME TO ShortcutPages;
        ");

        ColumnExists(db, "ShortcutPages", "IsHidden").Should().BeFalse("precondition");

        SchemaMigrations.Apply(db);

        ColumnExists(db, "ShortcutPages", "IsHidden").Should().BeTrue();
    }

    private static bool TableExists(SoundBoardDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@n";
        var p = cmd.CreateParameter();
        p.ParameterName = "@n";
        p.Value = name;
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() != null;
    }

    private static bool ColumnExists(SoundBoardDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        // PRAGMA table_info doesn't accept bound parameters for the table name
        // — it expects a literal identifier. Interpolating is safe here because
        // `table` is a controlled test-internal string, not user input.
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
