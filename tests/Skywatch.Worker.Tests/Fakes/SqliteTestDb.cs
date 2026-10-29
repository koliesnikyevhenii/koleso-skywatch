using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Skywatch.Worker.Persistence;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// A migrated SQLite in-memory database kept alive by one open connection, so unique indexes and foreign keys
/// behave as in production. Never the EF InMemory provider.
/// </summary>
internal sealed class SqliteTestDb : IDbContextFactory<SkywatchDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SkywatchDbContext> _options;

    /// <param name="concurrent">
    /// For a background service running beside the test: every context opens its own connection to a named shared
    /// in-memory database, because one <see cref="SqliteConnection"/> must not be used from two threads at once.
    /// </param>
    public SqliteTestDb(bool concurrent = false)
    {
        var connectionString = concurrent ? $"Data Source=skywatch-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared" : "Data Source=:memory:";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        var builder = new DbContextOptionsBuilder<SkywatchDbContext>();
        _options = (concurrent ? builder.UseSqlite(connectionString) : builder.UseSqlite(_connection)).Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public SkywatchDbContext CreateDbContext() => new(_options);

    /// <summary>A context on a host's database file, for assertions after (or while) the host runs.</summary>
    public static SkywatchDbContext OpenFile(string databasePath) =>
        new(new DbContextOptionsBuilder<SkywatchDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options);

    public void Dispose() => _connection.Dispose();
}
