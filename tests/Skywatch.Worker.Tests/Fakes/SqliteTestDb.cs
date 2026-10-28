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
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<SkywatchDbContext> _options;

    public SqliteTestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SkywatchDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public SkywatchDbContext CreateDbContext() => new(_options);

    /// <summary>A context on a host's database file, for assertions after (or while) the host runs.</summary>
    public static SkywatchDbContext OpenFile(string databasePath) =>
        new(new DbContextOptionsBuilder<SkywatchDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options);

    public void Dispose() => _connection.Dispose();
}
