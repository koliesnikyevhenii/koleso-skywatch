using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Spec AC-F7.4 against SQLite in-memory with FakeNotificationSender. 27 Oct 2026 is after the DST change, so
// Kyiv is UTC+2. Fake ids only.
public sealed class StaleSummaryServiceTests : IDisposable
{
    private const long _chatId = -1000000000042;

    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);

    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new(concurrent: true);
    private readonly FakeNotificationSender _sender = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(_ => { });
    private readonly int _sourceId;
    private int _nextTelegramId;

    public StaleSummaryServiceTests()
    {
        using var db = _db.CreateDbContext();
        var source = new Source { TelegramChatId = _chatId, Name = "Test source A", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
        db.Sources.Add(source);
        db.SaveChanges();
        _sourceId = source.Id;
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private StaleSummaryService Service() => new(_db, _sender, _kyiv, _time, _loggerFactory.CreateLogger<StaleSummaryService>());

    private async Task<long> NotificationAsync(DateTime publishedAtUtc, NotificationStatus status = NotificationStatus.SkippedStale, DateTime? reportedAtUtc = null)
    {
        await using var db = _db.CreateDbContext();
        var id = ++_nextTelegramId;
        var message = new Message
        {
            SourceId = _sourceId,
            TelegramMessageId = id,
            PublishedAtUtc = publishedAtUtc,
            ReceivedAtUtc = Now,
            ContentHash = $"hash-{id}",
            OriginChatId = _chatId,
            OriginMessageId = id,
            IsMatched = true,
        };
        var notification = new Notification { Message = message, Kind = NotificationKind.New, Status = status, CreatedAtUtc = Now, StaleReportedAtUtc = reportedAtUtc };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(Ct);
        return notification.Id;
    }

    private async Task<DateTime?> ReportedAtAsync(long id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.Where(n => n.Id == id).Select(n => n.StaleReportedAtUtc).SingleAsync(Ct);
    }

    // AC-F7.4: 3 unreported stale → one summary with N = 3 and the publication time range in Kyiv time, all marked.
    // Already reported stale rows and other statuses are not counted.
    [Fact]
    public async Task AC_F7_4_unreported_stale_notifications_get_one_summary_and_are_marked_reported()
    {
        var earlier = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
        var reported = await NotificationAsync(earlier, reportedAtUtc: earlier);
        await NotificationAsync(earlier, NotificationStatus.Sent);
        await NotificationAsync(earlier, NotificationStatus.SkippedDuplicate);
        var ids = new[]
        {
            await NotificationAsync(new DateTime(2026, 10, 27, 11, 52, 0, DateTimeKind.Utc)),
            await NotificationAsync(new DateTime(2026, 10, 27, 11, 41, 30, DateTimeKind.Utc)),
            await NotificationAsync(new DateTime(2026, 10, 27, 11, 47, 0, DateTimeKind.Utc)),
        };

        await Service().RunOnceAsync(Ct);

        var sent = _sender.Sends.ShouldHaveSingleItem();
        sent.Notification.Text.ShouldBe("⚠️ Пропущено 3 застарілих збігів (публікації 13:41–13:52)");
        sent.Notification.Priority.ShouldBe(SendPriority.Background);
        foreach (var id in ids)
        {
            (await ReportedAtAsync(id)).ShouldBe(Now);
        }

        (await ReportedAtAsync(reported)).ShouldBe(earlier);
    }

    // AC-F7.4: nothing unreported → nothing sent.
    [Fact]
    public async Task AC_F7_4_nothing_unreported_sends_nothing()
    {
        await NotificationAsync(Now.AddMinutes(-10), reportedAtUtc: Now.AddMinutes(-5));
        await NotificationAsync(Now.AddMinutes(-10), NotificationStatus.Sent);

        await Service().RunOnceAsync(Ct);

        _sender.Sends.ShouldBeEmpty();
    }

    // AC-F7.4: a failed summary send marks nothing; the next 30 s tick sends it again and marks the rows.
    [Fact]
    public async Task AC_F7_4_failed_send_marks_nothing_and_is_retried_on_the_next_tick()
    {
        _sender.Script(new SendResult.Transient("502 Bad Gateway", DeliveryUnknown: false));
        var id = await NotificationAsync(Now.AddMinutes(-10));
        using var service = Service();
        await service.StartAsync(Ct);

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromSeconds(29));
        _sender.Sends.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.ShouldHappen(() => _sender.Sends.Count == 1, "first summary attempt");
        (await ReportedAtAsync(id)).ShouldBeNull();

        _time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.ShouldHappenAsync(async () => await ReportedAtAsync(id) is not null, "summary retried and marked");

        _sender.Sends.Count.ShouldBe(2);
        _sender.Texts[1].ShouldBe(_sender.Texts[0]);
        await service.StopAsync(Ct);
    }

    // AC-F7.4: the summary itself is never stale-checked: stale rows from hours ago are still summarized.
    [Fact]
    public async Task AC_F7_4_summary_is_not_stale_checked()
    {
        await NotificationAsync(Now.AddHours(-5));

        await Service().RunOnceAsync(Ct);

        _sender.Sends.ShouldHaveSingleItem().Notification.Text.ShouldStartWith("⚠️ Пропущено 1 застарілих збігів");
    }
}
