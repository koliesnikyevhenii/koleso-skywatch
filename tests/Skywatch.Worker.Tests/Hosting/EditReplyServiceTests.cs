using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Skywatch.Core.Deduplication;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using MonitoringOptions = Skywatch.Worker.Options.MonitoringOptions;

namespace Skywatch.Worker.Tests.Hosting;

// Spec AC-F9.5 and AC-F7.3 (send time): ReplyOnChange with the default 15 s debounce. EditHandler,
// EditReplyService and NotificationDispatchService run together on SQLite in-memory with FakeNotificationSender
// and a fake clock. Fake ids only.
public sealed class EditReplyServiceTests : IDisposable
{
    private const long _chatId = -1000000000042;
    private const int _botMessageId = 555;
    private const string _alert = "Шахед курсом на Краснопавлівку";

    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);

    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new(concurrent: true);
    private readonly FakeNotificationSender _sender = new();
    private readonly DispatchSignal _signal = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(_ => { });
    private readonly EditReplyService _replies;
    private readonly NotificationDispatchService _dispatcher;
    private readonly EditHandler _handler;
    private readonly int _sourceId;

    public EditReplyServiceTests()
    {
        var monitoring = Microsoft.Extensions.Options.Options.Create(new MonitoringOptions { EditHandling = EditHandling.ReplyOnChange });
        var notifications = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = true });
        _replies = new EditReplyService(_db, _signal, monitoring, _time, _loggerFactory.CreateLogger<EditReplyService>());
        _dispatcher = new NotificationDispatchService(_db, _sender, _signal, notifications, _kyiv, _time, _loggerFactory.CreateLogger<NotificationDispatchService>());
        _handler = new EditHandler(
            _db,
            new KeywordFilter([new PatternDefinition("Settlement", "краснопавлівк", CaseSensitive: false, Enabled: true)]),
            new DuplicateDetector(monitoring),
            _signal,
            _replies,
            monitoring,
            Microsoft.Extensions.Options.Options.Create(new StorageOptions()),
            notifications,
            _time,
            _loggerFactory.CreateLogger<EditHandler>());

        using var db = _db.CreateDbContext();
        var source = new Source { TelegramChatId = _chatId, Name = "Test source A", Type = SourceType.Channel, Username = "test_channel", IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
        db.Sources.Add(source);
        db.SaveChanges();
        _sourceId = source.Id;
    }

    public void Dispose()
    {
        _replies.Dispose();
        _dispatcher.Dispose();
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>A matched message whose New notification was sent as bot message 555.</summary>
    private async Task<long> SentMessageAsync(DateTime? editedAtUtc = null)
    {
        await using var db = _db.CreateDbContext();
        var message = new Message
        {
            SourceId = _sourceId,
            TelegramMessageId = 1,
            Text = _alert,
            PublishedAtUtc = Now.AddSeconds(-30),
            ReceivedAtUtc = Now.AddSeconds(-29),
            EditedAtUtc = editedAtUtc,
            ContentHash = ContentHasher.Hash(TextNormalizer.Normalize(_alert)),
            OriginChatId = _chatId,
            OriginMessageId = 1,
            IsMatched = true,
            MatchedPatterns = "Settlement",
        };
        db.Notifications.Add(new Notification
        {
            Message = message,
            Kind = NotificationKind.New,
            Status = NotificationStatus.Sent,
            Attempts = 1,
            CreatedAtUtc = Now.AddSeconds(-29),
            SentAtUtc = Now.AddSeconds(-28),
            BotMessageId = _botMessageId,
        });
        await db.SaveChangesAsync(Ct);
        return message.Id;
    }

    private IncomingMessage Edit(string text) =>
        new(_chatId, 1, text, Now.AddSeconds(-30), Now, new OriginKey(_chatId, 1), IsEdit: true, EditedAtUtc: Now);

    private async Task<List<Notification>> RepliesAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKind.EditReply).ToListAsync(Ct);
    }

    private async Task StartAsync()
    {
        await _replies.StartAsync(Ct);
        await _dispatcher.StartAsync(Ct);
    }

    private async Task StopAsync()
    {
        await _dispatcher.StopAsync(Ct);
        await _replies.StopAsync(Ct);
    }

    // AC-F9.5: a sent message edited 3 times within 10 s → exactly one EditReply 15 s after the last edit, sent by
    // the dispatcher as a reply to the original bot message, with the final text and the edit marker.
    [Fact]
    public async Task AC_F9_5_three_edits_within_10_s_give_one_reply_15_s_after_the_last_edit()
    {
        await SentMessageAsync();
        await StartAsync();

        (await _handler.HandleAsync(Edit(_alert + " 1"), Ct)).ShouldBe(EditOutcome.ReplyScheduled);
        _time.Advance(TimeSpan.FromSeconds(4));
        await _handler.HandleAsync(Edit(_alert + " 2"), Ct);
        _time.Advance(TimeSpan.FromSeconds(6));
        await _handler.HandleAsync(Edit(_alert + " фінал"), Ct);

        _time.Advance(TimeSpan.FromMilliseconds(14_999));
        (await RepliesAsync()).ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.ShouldHappen(() => !_sender.Sends.IsEmpty, "reply sent");
        await Eventually.ShouldHappenAsync(async () => (await RepliesAsync()).All(r => r.Status == NotificationStatus.Sent), "reply recorded");

        var reply = (await RepliesAsync()).ShouldHaveSingleItem();
        reply.BotMessageId.ShouldNotBeNull();
        var sent = _sender.Sends.ShouldHaveSingleItem();
        sent.ReplyTo.ShouldBe(_botMessageId);
        sent.Notification.Priority.ShouldBe(SendPriority.Alert);
        sent.Notification.LabelNotificationId.ShouldBeNull(); // AC-F10.1: the reply carries no buttons
        sent.Notification.Text.ShouldContain(_alert + " фінал");
        sent.Notification.Text.ShouldContain("🏷 КП · ✏️ оновлено · ⚠️ Увага");
        sent.Notification.Text.ShouldContain("затримка 15 с");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await RepliesAsync()).Count.ShouldBe(1);
        await StopAsync();
    }

    // AC-F9.5 + AC-F7.3: an EditReply whose edit is older than 120 s at send time → SkippedStale, nothing sent.
    [Fact]
    public async Task AC_F7_3_edit_reply_older_than_120_s_from_the_edit_at_send_time_is_skipped_stale()
    {
        var messageId = await SentMessageAsync(editedAtUtc: Now.AddSeconds(-121));
        await using (var db = _db.CreateDbContext())
        {
            db.Notifications.Add(new Notification { MessageId = messageId, Kind = NotificationKind.EditReply, Status = NotificationStatus.Pending, CreatedAtUtc = Now });
            await db.SaveChangesAsync(Ct);
        }

        await StartAsync();
        await Eventually.ShouldHappenAsync(async () => (await RepliesAsync()).Single().Status != NotificationStatus.Pending, "reply handled");

        (await RepliesAsync()).Single().Status.ShouldBe(NotificationStatus.SkippedStale);
        _sender.Sends.ShouldBeEmpty();
        await StopAsync();
    }
}
