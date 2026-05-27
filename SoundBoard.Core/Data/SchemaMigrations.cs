using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.Data;

namespace SoundBoard.Core.Data;

/// <summary>
/// Versioned, append-only schema migrations for the user's library DB. Each
/// migration has a monotonically-increasing integer version; the
/// <c>SchemaVersion</c> table records which versions have been applied.
///
/// <para>On a fresh install <c>EnsureCreated()</c> builds every table from
/// the EF model. The migration list is empty today — the app has not yet
/// shipped, so there are no pre-existing user databases that would need
/// patching. <see cref="Apply"/> still runs to:
/// <list type="bullet">
///   <item>Create the <c>SchemaVersion</c> bookkeeping table.</item>
///   <item>Seed the three built-in buses (Music, Ambient, SFX) — EF
///   doesn't seed data, so this stays in the migration runner even
///   though the schema itself is built by EnsureCreated.</item>
/// </list></para>
///
/// <para><b>Adding a migration post-launch:</b> append a new entry to
/// <see cref="_migrations"/>. Versions must be strictly increasing and
/// never re-used. The Apply loop handles "version already applied"
/// and "column / table already exists" cases — the latter records the
/// version as baselined so a database built fresh from the current EF
/// model doesn't error on a redundant ALTER.</para>
///
/// <para>Lives in <c>SoundBoard.Core.Data</c> alongside the DbContext rather
/// than the UI layer — future Core consumers (the library import path,
/// CLI tools, future headless modes) need to apply migrations without
/// dragging Avalonia in.</para>
/// </summary>
public static class SchemaMigrations
{
    /// <summary>
    /// Bootstrap baseline. Leaves headroom for inserting earlier migrations
    /// without renumbering. It's just an integer.
    /// </summary>
    private const int Baseline = 1000;

    // The migration list is currently empty: the app has not yet shipped
    // to end users, so every database is built fresh from the current EF
    // model via EnsureCreated() and there are no installed copies whose
    // schema would need patching.
    //
    // The Buses table, Track.BusId column, Preset.BusIdOverride,
    // ShortcutButton.BusIdOverride, and Bus.Volume — all of which used
    // to be migrations 1001 and 1002 — are now part of the baseline EF
    // model (see Models/*.cs). EnsureCreated() materialises them
    // directly; no ALTER TABLE step required.
    //
    // The first post-launch schema change must go here as
    // (Baseline + 1, "description", "ALTER TABLE ..."). Versions must
    // be strictly increasing and never re-used. The Apply machinery
    // below already handles "version already applied" and "column
    // already exists" gracefully — see Apply's catch block.
    private static readonly List<(int Version, string Description, string Sql)> _migrations = new()
    {
        // ── Add new migrations below this line ─────────────────────────────
    };

    public static void Apply(SoundBoardDbContext db)
    {
        EnsureVersionTable(db);
        var applied = ReadAppliedVersions(db);

        foreach (var m in _migrations)
        {
            if (applied.Contains(m.Version)) continue;
            try
            {
                db.Database.ExecuteSqlRaw(m.Sql);
                RecordVersion(db, m.Version, m.Description, alreadyApplied: false);
                Log.Info("Db", $"Applied migration {m.Version}: {m.Description}");
            }
            catch (Exception ex)
            {
                // A column/table that genuinely already exists means a fresh
                // EnsureCreated() already covers this version — record it as
                // applied and move on. Anything else, fail loudly; silent
                // migration failures are how you end up with mysteriously-
                // missing columns later.
                if (IsAlreadyExistsError(ex))
                {
                    RecordVersion(db, m.Version, m.Description, alreadyApplied: true);
                    Log.Warn("Db", $"Migration {m.Version} target already exists; recorded as applied.");
                }
                else
                {
                    Log.Error("Db", $"Migration {m.Version} failed: {m.Description}", ex);
                    throw;
                }
            }
        }

        SeedBuiltInBuses(db);
    }

    /// <summary>Seed the three built-in buses (Music, Ambient, SFX) if the
    /// Buses table is empty. Ids 1/2/3 are pinned via explicit INSERTs so
    /// the audio engine and migration code can hard-code
    /// <see cref="BuiltInBusIds.DefaultForNewTracks"/> at compile time.
    /// Idempotent: re-running just checks "any rows?" and does nothing if
    /// already seeded.</summary>
    private static void SeedBuiltInBuses(SoundBoardDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Buses";
        var count = Convert.ToInt32(check.ExecuteScalar());
        if (count > 0) return;

        db.Database.ExecuteSqlRaw(@"
            INSERT INTO Buses (Id, Name, ""Order"", Color, IsBuiltIn, Volume) VALUES
                (1, 'Music',   0, NULL, 1, 1.0),
                (2, 'Ambient', 10, NULL, 1, 1.0),
                (3, 'SFX',     20, NULL, 1, 1.0);
        ");
        Log.Info("Db", "Seeded built-in buses (Music, Ambient, SFX).");
    }

    private static void EnsureVersionTable(SoundBoardDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER NOT NULL PRIMARY KEY,
                Description TEXT NOT NULL,
                AppliedAt TEXT NOT NULL
            )");
    }

    private static HashSet<int> ReadAppliedVersions(SoundBoardDbContext db)
    {
        var result = new HashSet<int>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version FROM SchemaVersion";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }

    private static void RecordVersion(SoundBoardDbContext db, int version, string description, bool alreadyApplied)
    {
        var note = alreadyApplied ? $"{description} (baselined)" : description;
        db.Database.ExecuteSqlInterpolated(
            $@"INSERT OR IGNORE INTO SchemaVersion (Version, Description, AppliedAt) VALUES ({version}, {note}, {DateTime.UtcNow:o})");
    }

    private static bool IsAlreadyExistsError(Exception ex)
    {
        // SQLite throws "duplicate column name" / "table … already exists"
        // — both are SqliteException with substring matches in the message.
        var msg = ex.Message;
        return msg.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }
}
