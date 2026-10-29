using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Persistence;

// Plan P3a: migrate at startup (no EnsureCreated), then PRAGMA journal_mode=WAL; busy timeout from Storage options.
public sealed class DatabaseStartupTests
{
    private static async Task<string> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task P3a_run_mode_startup_creates_the_database_applies_migrations_and_enables_wal()
    {
        var ct = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(Path.GetDirectoryName(TestHost.TempDatabasePath())!, Guid.NewGuid().ToString("N"), "data", "skywatch.db");
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Storage:DatabasePath"] = databasePath;
        using var host = TestHost.Build([], settings);

        await host.StartAsync(ct);
        await host.StopAsync(ct);

        File.Exists(databasePath).ShouldBeTrue();
        (await ScalarAsync(databasePath, "PRAGMA journal_mode")).ShouldBe("wal");
        int.Parse(await ScalarAsync(databasePath, "SELECT count(*) FROM __EFMigrationsHistory"), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        (await ScalarAsync(databasePath, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Sources', 'Messages', 'Notifications', 'ServiceEvents')")).ShouldBe("4");
    }

    // Plan P3a: Storage:BusyTimeoutSeconds becomes the connection string's Default Timeout.
    [Fact]
    public async Task P3a_busy_timeout_seconds_is_the_connection_default_timeout()
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Storage:BusyTimeoutSeconds"] = "7";
        using var host = TestHost.Build([], settings);

        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SkywatchDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);

        db.Database.GetDbConnection().ShouldBeOfType<SqliteConnection>().DefaultTimeout.ShouldBe(7);
    }
}
