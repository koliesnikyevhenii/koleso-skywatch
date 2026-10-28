using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;

namespace Skywatch.Worker.Persistence;

/// <summary>
/// Registered before every service that uses the database: applies migrations (never <c>EnsureCreated</c>), then
/// switches the file to WAL so processing, dispatch and retention can write concurrently (with the busy timeout).
/// </summary>
internal sealed partial class DatabaseInitializer(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    IOptions<StorageOptions> storage,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(storage.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        // Persistent in the file; outside the migration transaction because SQLite cannot change it inside one.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        LogReady(logger, path);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database ready at {Path}.")]
    private static partial void LogReady(ILogger logger, string path);
}
