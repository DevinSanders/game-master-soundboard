using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;
using SoundBoard.Tests.Fakes;
using System.Data;

namespace SoundBoard.Tests.Data;

/// <summary>
/// Pins the schema-migrations contract: a fresh DB has the
/// <c>SchemaVersion</c> table after Apply, applying twice is idempotent,
/// and the list-empty case is a no-op. Once <see cref="SchemaMigrations"/>
/// gains real migration entries, parametric tests should be added covering
/// "first-run install applies all" and "incremental run skips already-applied."
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
    public void Apply_WithEmptyMigrationList_DoesNotCrash()
    {
        // The default _migrations list is empty (a fresh install gets the
        // full schema from EnsureCreated). Apply must handle this without
        // throwing — a regression here would break every app launch.
        using var fx = new SqliteInMemoryDbFixture();
        using var db = fx.CreateContext();

        var act = () => SchemaMigrations.Apply(db);

        act.Should().NotThrow();
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
}
