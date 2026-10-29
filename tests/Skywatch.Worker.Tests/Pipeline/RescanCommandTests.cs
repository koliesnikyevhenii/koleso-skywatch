using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using Skywatch.Worker.Tests.Telegram;

namespace Skywatch.Worker.Tests.Pipeline;

// Spec AC-F1.6: `rescan [--pattern "<regex>"]` over stored unmatched text. The command runs on SQLite in-memory;
// the run mode runs through Program.RunAsync on a database file (it installs the static WTelegramClient log bridge,
// hence the collection). Fake ids only.
[Collection(WTelegramStaticLog.Name)]
public sealed class RescanCommandTests : IDisposable
{
    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);

    // 14:00 Kyiv (UTC+2).
    private static readonly DateTime _published = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDb _db = new();
    private int _nextTelegramId;

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MonitoringOptions Monitoring() => new()
    {
        Patterns =
        [
            new PatternOptions { Name = "Settlement", Regex = @"\bкрасно[-\s]?павл(?:івк|івц|овк)\w*" },
            new PatternOptions { Name = "Switched off", Regex = @"\bтиша\b", Enabled = false },
        ],
    };

    private static void Seed(Skywatch.Worker.Persistence.SkywatchDbContext db, ref int nextId, string? text, bool matched = false)
    {
        var source = db.Sources.Local.FirstOrDefault() ?? db.Sources.FirstOrDefault()
            ?? new Source { TelegramChatId = -1000000000042, Name = "Test source A", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
        var id = ++nextId;
        db.Messages.Add(new Message
        {
            Source = source,
            TelegramMessageId = id,
            Text = text,
            PublishedAtUtc = _published.AddMinutes(id),
            ReceivedAtUtc = _published.AddMinutes(id),
            ContentHash = $"hash-{id}",
            OriginChatId = source.TelegramChatId,
            OriginMessageId = id,
            IsMatched = matched,
            MatchedPatterns = matched ? "Settlement" : null,
        });
        db.SaveChanges();
    }

    private void SeedDefault()
    {
        using var db = _db.CreateDbContext();
        Seed(db, ref _nextTelegramId, "тиша, все спокійно");
        Seed(db, ref _nextTelegramId, "Шахед   на\nКраснопавлівку");
        Seed(db, ref _nextTelegramId, "Шахед на Краснопавлівку", matched: true); // already matched: not rescanned
        Seed(db, ref _nextTelegramId, null); // text cleared by retention
    }

    private async Task<string[]> RunAsync(string? pattern = null)
    {
        using var output = new StringWriter();
        var exitCode = await new RescanCommand(_db, Microsoft.Extensions.Options.Options.Create(Monitoring()), _kyiv).RunAsync(pattern, output, Ct);
        exitCode.ShouldBe(0);
        return output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
    }

    // AC-F1.6: an unmatched stored text that the configured patterns match now is printed with date (display
    // zone), source, message id, pattern and a one-line snippet; matched and text-less messages are skipped.
    [Fact]
    public async Task AC_F1_6_configured_patterns_find_stored_unmatched_text()
    {
        SeedDefault();

        var lines = await RunAsync();

        lines.ShouldBe(
        [
            "Published\tSource\tMessageId\tPattern\tSnippet",
            "2026-10-27 14:02:00\tTest source A\t2\tSettlement\tШахед на Краснопавлівку",
            "1 of 2 unmatched messages with text match.",
        ]);
    }

    // AC-F1.6: --pattern replaces the configured patterns with one case-insensitive regex.
    [Fact]
    public async Task AC_F1_6_one_off_pattern_replaces_the_configured_patterns()
    {
        SeedDefault();

        var lines = await RunAsync(@"\bТИША\b");

        lines.Length.ShouldBe(3);
        lines[1].ShouldBe("2026-10-27 14:01:00\tTest source A\t1\t--pattern\tтиша, все спокійно");
    }

    // AC-F1.6: a snippet is one line, cut after 120 characters without splitting a surrogate pair.
    [Fact]
    public void AC_F1_6_snippet_is_one_line_and_cut()
    {
        RescanCommand.Snippet("a\r\n  b\tc").ShouldBe("a b c");
        RescanCommand.Snippet(new string('x', 120)).ShouldBe(new string('x', 120));
        RescanCommand.Snippet(new string('x', 121)).ShouldBe(new string('x', 120) + "…");
        RescanCommand.Snippet(new string('x', 119) + "🚀🚀").ShouldBe(new string('x', 119) + "…");
    }

    [Theory]
    [InlineData(new[] { "rescan" }, true, null)]
    [InlineData(new[] { "rescan", "--pattern", "краснопе" }, true, "краснопе")]
    [InlineData(new[] { "rescan", "--pattern" }, false, null)]
    [InlineData(new[] { "rescan", "--pattern", " " }, false, null)]
    [InlineData(new[] { "rescan", "--regex", "x" }, false, null)]
    [InlineData(new[] { "rescan", "--pattern", "x", "extra" }, false, null)]
    public void AC_F1_6_rescan_arguments(string[] args, bool valid, string? pattern)
    {
        Program.ParseRunMode(args).ShouldBe(RunMode.Rescan);
        Program.TryParseRescanPattern(args, out var parsed).ShouldBe(valid);
        parsed.ShouldBe(pattern);
    }

    // AC-F1.6 run mode: needs no Telegram settings or session; exits 0; leaves the database file byte-for-byte unchanged.
    [Fact]
    public async Task AC_F1_6_rescan_mode_is_read_only_and_needs_no_telegram_settings()
    {
        var path = TestHost.TempDatabasePath();
        await using (var db = SqliteTestDb.OpenFile(path))
        {
            await db.Database.MigrateAsync(Ct);
            Seed(db, ref _nextTelegramId, "Шахед на Краснопавлівку");
            Seed(db, ref _nextTelegramId, "тиша");
        }

        var before = SHA256.HashData(await File.ReadAllBytesAsync(path, Ct));
        var logs = new CapturingLoggerProvider();

        var exitCode = await Program.RunAsync(["rescan"], TestHost.Configure(RescanSettings(path), logs), Ct);

        exitCode.ShouldBe(0, string.Join("\n", logs.Lines));
        (SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))).ShouldBe(before);
    }

    // AC-F1.6 "applies no migrations": without a database there is nothing to scan; no file is created.
    [Fact]
    public async Task AC_F1_6_rescan_mode_without_a_database_fails_and_creates_nothing()
    {
        var path = TestHost.TempDatabasePath();
        var logs = new CapturingLoggerProvider();

        var exitCode = await Program.RunAsync(["rescan"], TestHost.Configure(RescanSettings(path), logs), Ct);

        exitCode.ShouldNotBe(0);
        File.Exists(path).ShouldBeFalse();
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task AC_F1_6_rescan_mode_with_invalid_arguments_exits_2()
    {
        var logs = new CapturingLoggerProvider();

        (await Program.RunAsync(["rescan", "--pattern"], TestHost.Configure(RescanSettings(TestHost.TempDatabasePath()), logs), Ct)).ShouldBe(2);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("--pattern"));
    }

    private static Dictionary<string, string?> RescanSettings(string databasePath) => new()
    {
        ["Monitoring:Patterns:0:Name"] = "Settlement",
        ["Monitoring:Patterns:0:Regex"] = @"\bкрасно[-\s]?павл(?:івк|івц|овк)\w*",
        ["Storage:DatabasePath"] = databasePath,
    };
}
