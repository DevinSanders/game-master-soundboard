using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;
using SoundBoard.Core.Services;

namespace SoundBoard.Tests.Fakes;

/// <summary>
/// Per-test SQLite in-memory database fixture. Uses a single
/// <see cref="SqliteConnection"/> held open for the fixture's lifetime —
/// the only way to get a shared in-memory DB across multiple EF
/// <see cref="DbContext"/> instances (each <c>new SqliteConnection</c>
/// with <c>DataSource=:memory:</c> would get its own private DB).
///
/// <para>Schema is built via <see cref="DatabaseFacade.EnsureCreated"/>,
/// matching the production startup path in <c>App.axaml.cs</c>. Schema
/// migrations from <c>SchemaMigrations.cs</c> are NOT applied here — they
/// only matter on real databases that pre-date a model change, which by
/// definition can't exist in a fresh in-memory store.</para>
///
/// <para>Use one fixture per test; do NOT share via <c>IClassFixture</c>
/// because the in-memory DB is mutated heavily and isolation is cheaper
/// than the bookkeeping to reset between tests.</para>
/// </summary>
public sealed class SqliteInMemoryDbFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SoundBoardDbContext> _options;

    public ISoundBoardDbContextFactory Factory { get; }

    public SqliteInMemoryDbFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<SoundBoardDbContext>()
            .UseSqlite(_connection)
            .Options;

        Factory = new TestDbContextFactory(_options);

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SoundBoardDbContext CreateContext() => Factory.CreateDbContext();

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class TestDbContextFactory : ISoundBoardDbContextFactory
    {
        private readonly DbContextOptions<SoundBoardDbContext> _options;
        private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

        public TestDbContextFactory(DbContextOptions<SoundBoardDbContext> options)
        {
            _options = options;
        }

        public SoundBoardDbContext CreateDbContext() => new(_options, _settings);
    }
}
