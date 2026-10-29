using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skywatch.Core.Formatting;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Spec F7.1 (send time and startup recovery) and F8.1, F8.6–F8.11: the dispatcher against SQLite in-memory with
// FakeNotificationSender and a fake clock. Defaults: max message age 120 s, 3 attempts. Fake ids only.
public sealed class NotificationDispatchServiceTests : IDisposable
{
    private const long _chatId = -1000000000042;
    private const string _sourceName = "Test source A";

    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);

    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new(concurrent: true);
    private readonly FakeNotificationSender _sender = new();
    private readonly DispatchSignal _signal = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _sourceId;
    private int _nextTelegramId;

    public NotificationDispatchServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        using var db = _db.CreateDbContext();
        var source = new Source { TelegramChatId = _chatId, Name = _sourceName, Type = SourceType.Channel, Username = "test_channel", IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
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

    private NotificationDispatchService Dispatcher() =>
        new(
            _db,
            _sender,
            _signal,
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = true }),
            _kyiv,
            _time,
            _loggerFactory.CreateLogger<NotificationDispatchService>());

    /// <summary>A matched message published <paramref name="ageSeconds"/> ago with a <c>Pending</c> notification.</summary>
    private async Task<long> PendingAsync(string text = "Шахед на Краснопавлівку", double ageSeconds = 1)
    {
        await using var db = _db.CreateDbContext();
        var id = ++_nextTelegramId;
        var message = new Message
        {
            SourceId = _sourceId,
            TelegramMessageId = id,
            Text = text,
            PublishedAtUtc = Now.AddSeconds(-ageSeconds),
            ReceivedAtUtc = Now,
            ContentHash = $"hash-{id}",
            OriginChatId = _chatId,
            OriginMessageId = id,
            IsMatched = true,
            MatchedPatterns = "Settlement",
        };
        var notification = new Notification { Message = message, Kind = NotificationKind.New, Status = NotificationStatus.Pending, CreatedAtUtc = Now };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(Ct);
        return notification.Id;
    }

    private async Task<Notification> RowAsync(long id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id, Ct);
    }

    private async Task SetStatusAsync(long id, NotificationStatus status)
    {
        await using var db = _db.CreateDbContext();
        await db.Notifications.Where(n => n.Id == id).ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, status), Ct);
    }

    private async Task<NotificationDispatchService> StartAsync()
    {
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);
        return dispatcher;
    }

    private async Task NotPendingAsync(params long[] ids)
    {
        foreach (var id in ids)
        {
            await Eventually.ShouldHappenAsync(async () => (await RowAsync(id)).Status != NotificationStatus.Pending, $"notification {id} handled");
        }
    }

    private Task SendsAsync(int count) => Eventually.ShouldHappen(() => _sender.Sends.Count >= count, $"{count} sends");

    // AC-F8.10: success → Sent with SentAtUtc and BotMessageId; the text is the F8.2 format with the source link.
    [Fact]
    public async Task AC_F8_10_success_stores_sent_status_sent_at_and_bot_message_id()
    {
        var id = await PendingAsync("Шахед на Краснопавлівку", ageSeconds: 4);
        using var dispatcher = await StartAsync();

        await NotPendingAsync(id);

        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.SentAtUtc.ShouldBe(Now);
        row.BotMessageId.ShouldBe(101);
        row.Attempts.ShouldBe(1);
        var sent = _sender.Sends.ShouldHaveSingleItem();
        sent.Notification.Priority.ShouldBe(SendPriority.Alert);
        sent.Notification.LabelNotificationId.ShouldBe(id); // AC-F10.1
        sent.Notification.Text.ShouldBe(NotificationFormatter.Format(
            new NotificationContent(["Settlement"], "Шахед на Краснопавлівку", _sourceName, Now.AddSeconds(-4), null, "https://t.me/test_channel/1"),
            Now,
            _kyiv));
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.6: 429 with retry_after = 3 → waits 3 s, then sends; the 429 is not an attempt.
    [Fact]
    public async Task AC_F8_6_rate_limited_waits_retry_after_and_does_not_count_an_attempt()
    {
        _sender.Script(new SendResult.RateLimited(TimeSpan.FromSeconds(3)));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromMilliseconds(2999));
        _sender.Sends.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await NotPendingAsync(id);

        _sender.Sends.Count.ShouldBe(2);
        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.Attempts.ShouldBe(1);
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.7: transient ×2 then success → Sent, Attempts = 3, after waits of 1 s and 2 s.
    [Fact]
    public async Task AC_F8_7_transient_twice_then_success_is_sent_after_backoff_of_1_and_2_seconds()
    {
        _sender.Script(new SendResult.Transient("502 Bad Gateway", DeliveryUnknown: false), new SendResult.Transient("502 Bad Gateway", DeliveryUnknown: false));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        _sender.Sends.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await SendsAsync(2);

        await _time.TimersCreatedAtLeast(2);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        _sender.Sends.Count.ShouldBe(2);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await NotPendingAsync(id);

        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.Attempts.ShouldBe(3);
        row.PossibleDuplicate.ShouldBeFalse();
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.7: transient on all 3 attempts → Failed with the error stored.
    [Fact]
    public async Task AC_F8_7_transient_on_every_attempt_is_failed_with_the_error()
    {
        _sender.Script(
            new SendResult.Transient("first", DeliveryUnknown: false),
            new SendResult.Transient("second", DeliveryUnknown: false),
            new SendResult.Transient("503 Service Unavailable", DeliveryUnknown: false));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await _time.TimersCreatedAtLeast(2);
        _time.Advance(TimeSpan.FromSeconds(2));
        await NotPendingAsync(id);

        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Attempts.ShouldBe(3);
        row.Error.ShouldBe("503 Service Unavailable");
        _sender.Sends.Count.ShouldBe(3);
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("503 Service Unavailable"));
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.8: 400/403 → Failed immediately, no retry, and a Critical log.
    [Theory]
    [InlineData("Bad Request: chat not found")]
    [InlineData("Forbidden: bot was kicked from the group chat")]
    public async Task AC_F8_8_client_error_is_failed_immediately_with_a_critical_log(string error)
    {
        _sender.Script(new SendResult.Fatal(error));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await NotPendingAsync(id);

        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Attempts.ShouldBe(1);
        row.Error.ShouldBe(error);
        _sender.Sends.Count.ShouldBe(1);
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains(error));
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.9: a timeout with unknown delivery → retried and flagged PossibleDuplicate.
    [Fact]
    public async Task AC_F8_9_timeout_with_unknown_delivery_is_retried_and_flagged_possible_duplicate()
    {
        _sender.Script(new SendResult.Transient("Request timed out", DeliveryUnknown: true));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await NotPendingAsync(id);

        var row = await RowAsync(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.Attempts.ShouldBe(2);
        row.PossibleDuplicate.ShouldBeTrue();
        await dispatcher.StopAsync(Ct);
    }

    // AC-F7.1 (before every attempt): the message turns stale during the backoff → SkippedStale, no further send.
    [Fact]
    public async Task AC_F7_1_message_that_becomes_stale_between_attempts_is_skipped_stale()
    {
        _sender.Script(new SendResult.Transient("502 Bad Gateway", DeliveryUnknown: false));
        var id = await PendingAsync(ageSeconds: 119.5);
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await NotPendingAsync(id);

        (await RowAsync(id)).Status.ShouldBe(NotificationStatus.SkippedStale);
        _sender.Sends.Count.ShouldBe(1);
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.11 + AC-F7.1 (startup recovery): 3 Pending rows at startup, one stale → 2 sent in creation order, 1 skipped.
    [Fact]
    public async Task AC_F8_11_pending_rows_at_startup_are_sent_in_creation_order_after_the_staleness_check()
    {
        var a = await PendingAsync("A Краснопавлівка", ageSeconds: 10);
        var b = await PendingAsync("B Краснопавлівка", ageSeconds: 121);
        var c = await PendingAsync("C Краснопавлівка", ageSeconds: 5);
        using var dispatcher = await StartAsync();

        await NotPendingAsync(a, b, c);

        _sender.Texts.Select(t => t.Contains("A Краснопавлівка") ? "A" : t.Contains("C Краснопавлівка") ? "C" : "?").ShouldBe(["A", "C"]);
        (await RowAsync(a)).Status.ShouldBe(NotificationStatus.Sent);
        (await RowAsync(b)).Status.ShouldBe(NotificationStatus.SkippedStale);
        (await RowAsync(c)).Status.ShouldBe(NotificationStatus.Sent);
        _logs.Lines.ShouldContain(l => l.Contains("Recovering 3 pending notifications"));
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.1: notifications created A, B, C after startup and one wake-up signal → sent A, B, C.
    [Fact]
    public async Task AC_F8_1_notifications_are_sent_sequentially_in_creation_order()
    {
        using var dispatcher = await StartAsync();
        var a = await PendingAsync("A Краснопавлівка");
        var b = await PendingAsync("B Краснопавлівка");
        var c = await PendingAsync("C Краснопавлівка");

        _signal.Notify();
        await NotPendingAsync(a, b, c);

        _sender.Texts.Select(t => t.Contains("A Краснопавлівка") ? "A" : t.Contains("B Краснопавлівка") ? "B" : "C").ShouldBe(["A", "B", "C"]);
        await dispatcher.StopAsync(Ct);
    }

    // AC-F8.11: the dispatcher re-reads the status before each send; a row changed while it waited is not sent again.
    [Fact]
    public async Task AC_F8_11_row_whose_status_changed_after_it_was_read_is_skipped()
    {
        _sender.Script(new SendResult.RateLimited(TimeSpan.FromSeconds(3)));
        var id = await PendingAsync();
        using var dispatcher = await StartAsync();

        await _time.TimersCreatedAtLeast(1);
        await SetStatusAsync(id, NotificationStatus.Failed);
        _time.Advance(TimeSpan.FromSeconds(3));
        var next = await PendingAsync("Next Краснопавлівка");
        _signal.Notify();
        await NotPendingAsync(next);

        _sender.Sends.Count.ShouldBe(2);
        _sender.Texts[1].ShouldContain("Next Краснопавлівка");
        (await RowAsync(id)).Status.ShouldBe(NotificationStatus.Failed);
        await dispatcher.StopAsync(Ct);
    }

    // P4b: rows that are not Pending (already sent, duplicates, stale at creation) are never sent.
    [Fact]
    public async Task AC_F8_11_only_pending_rows_are_sent()
    {
        var sent = await PendingAsync("Sent Краснопавлівка");
        await SetStatusAsync(sent, NotificationStatus.Sent);
        var duplicate = await PendingAsync("Duplicate Краснопавлівка");
        await SetStatusAsync(duplicate, NotificationStatus.SkippedDuplicate);
        var pending = await PendingAsync("Pending Краснопавлівка");
        using var dispatcher = await StartAsync();

        await NotPendingAsync(pending);

        _sender.Texts.ShouldHaveSingleItem().ShouldContain("Pending Краснопавлівка");
        await dispatcher.StopAsync(Ct);
    }
}
