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

namespace Skywatch.Worker.Tests.Pipeline;

// Spec F9 and AC-F7.3 on SQLite in-memory with a fake clock. Defaults: max message age 120 s, same-text window
// 10 min across sources only. Fake ids only.
public sealed class EditHandlerTests : IDisposable
{
    private const long _sourceA = -1000000000042;
    private const long _sourceB = -1000000000043;
    private const string _sourceAName = "Test source A";
    private const string _alert = "Шахед курсом на Краснопавлівку";
    private const string _unmatched = "Шахед курсом на північ";

    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new();
    private readonly DispatchSignal _signal = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<long, int> _sourceIds = [];

    public EditHandlerTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        using var db = _db.CreateDbContext();
        foreach (var (chatId, name) in new[] { (_sourceA, _sourceAName), (_sourceB, "Test source B") })
        {
            var source = new Source { TelegramChatId = chatId, Name = name, Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
            db.Sources.Add(source);
            db.SaveChanges();
            _sourceIds[chatId] = source.Id;
        }
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static string Hash(string text) => ContentHasher.Hash(TextNormalizer.Normalize(text));

    private (EditHandler Handler, EditReplyService Replies) Handler(
        EditHandling mode = EditHandling.NotifyOnNewMatch,
        bool storeUnmatchedText = true,
        bool notificationsEnabled = true)
    {
        var monitoring = Microsoft.Extensions.Options.Options.Create(new MonitoringOptions { EditHandling = mode });
        var replies = new EditReplyService(_db, _signal, monitoring, _time, _loggerFactory.CreateLogger<EditReplyService>());
        var handler = new EditHandler(
            _db,
            new KeywordFilter([new PatternDefinition("Settlement", "краснопавлівк", CaseSensitive: false, Enabled: true)]),
            new DuplicateDetector(monitoring),
            _signal,
            replies,
            monitoring,
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoreUnmatchedText = storeUnmatchedText }),
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = notificationsEnabled }),
            _time,
            _loggerFactory.CreateLogger<EditHandler>());
        return (handler, replies);
    }

    /// <summary>A stored message published 60 s ago, received 1 s later; matched when its text matches.</summary>
    private async Task<long> StoredAsync(long chatId, int id, string text, NotificationStatus? notification = null, int? botMessageId = null)
    {
        await using var db = _db.CreateDbContext();
        var matched = text.Contains("Краснопавлівк", StringComparison.Ordinal);
        var message = new Message
        {
            SourceId = _sourceIds[chatId],
            TelegramMessageId = id,
            Text = text,
            PublishedAtUtc = Now.AddSeconds(-60),
            ReceivedAtUtc = Now.AddSeconds(-59),
            ContentHash = Hash(text),
            OriginChatId = chatId,
            OriginMessageId = id,
            IsMatched = matched,
            MatchedPatterns = matched ? "Settlement" : null,
        };
        db.Messages.Add(message);
        if (notification is { } status)
        {
            db.Notifications.Add(new Notification { Message = message, Kind = NotificationKind.New, Status = status, CreatedAtUtc = Now.AddSeconds(-59), BotMessageId = botMessageId });
        }

        await db.SaveChangesAsync(Ct);
        return message.Id;
    }

    /// <summary>An edit event received now; the edit happened <paramref name="editAgeSeconds"/> ago.</summary>
    private IncomingMessage Edit(long chatId, int id, string text, double editAgeSeconds = 1) =>
        new(chatId, id, text, Now.AddSeconds(-60), Now, new OriginKey(chatId, id), IsEdit: true, EditedAtUtc: Now.AddSeconds(-editAgeSeconds));

    private async Task<Message> RowAsync(long chatId, int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Messages.AsNoTracking().SingleAsync(m => m.SourceId == _sourceIds[chatId] && m.TelegramMessageId == id, Ct);
    }

    private async Task<List<Notification>> NotificationsAsync(long chatId, int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.Message!.SourceId == _sourceIds[chatId] && n.Message.TelegramMessageId == id)
            .OrderBy(n => n.Id)
            .ToListAsync(Ct);
    }

    // AC-F9.1: Ignore → an edit adding a mention changes nothing.
    [Fact]
    public async Task AC_F9_1_ignore_mode_changes_nothing()
    {
        await StoredAsync(_sourceA, 1, _unmatched);

        (await Handler(EditHandling.Ignore).Handler.HandleAsync(Edit(_sourceA, 1, _alert), Ct)).ShouldBe(EditOutcome.Ignored);

        var row = await RowAsync(_sourceA, 1);
        (row.Text, row.IsMatched, row.EditedAtUtc, row.ContentHash).ShouldBe((_unmatched, false, (DateTime?)null, Hash(_unmatched)));
        (await NotificationsAsync(_sourceA, 1)).ShouldBeEmpty();
    }

    // AC-F9.3 + F9.2: unmatched → matched → the row is updated and an EditNewMatch notification is created and
    // the dispatcher woken.
    [Fact]
    public async Task AC_F9_3_unmatched_message_edited_to_match_gets_an_edit_new_match_notification()
    {
        await StoredAsync(_sourceA, 1, _unmatched);
        var waiting = _signal.WaitAsync(Ct).AsTask();

        var edit = Edit(_sourceA, 1, _alert);
        (await Handler().Handler.HandleAsync(edit, Ct)).ShouldBe(EditOutcome.Pending);

        var row = await RowAsync(_sourceA, 1);
        (row.Text, row.IsMatched, row.MatchedPatterns, row.EditedAtUtc, row.ContentHash).ShouldBe((_alert, true, "Settlement", edit.EditedAtUtc, Hash(_alert)));
        var notification = (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem();
        (notification.Kind, notification.Status).ShouldBe((NotificationKind.EditNewMatch, NotificationStatus.Pending));
        await waiting;
    }

    // AC-F9.4: matched → matched (already notified) → no new notification.
    [Theory]
    [InlineData(nameof(EditHandling.NotifyOnNewMatch))]
    [InlineData(nameof(EditHandling.ReplyOnChange))]
    public async Task AC_F9_4_edit_of_an_already_notified_message_creates_no_notification(string modeName)
    {
        var mode = Enum.Parse<EditHandling>(modeName);
        await StoredAsync(_sourceA, 1, _alert, NotificationStatus.Pending);

        (await Handler(mode).Handler.HandleAsync(Edit(_sourceA, 1, _alert + " знову"), Ct)).ShouldBe(EditOutcome.Updated);

        (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem().Kind.ShouldBe(NotificationKind.New);
        (await RowAsync(_sourceA, 1)).Text.ShouldBe(_alert + " знову");
    }

    // AC-F9.4: matched → unmatched → matched again; the message was notified before, so no second notification.
    [Fact]
    public async Task AC_F9_4_rematch_after_the_mention_was_removed_creates_no_second_notification()
    {
        await StoredAsync(_sourceA, 1, _alert, NotificationStatus.Sent, botMessageId: 555);
        var handler = Handler().Handler;

        (await handler.HandleAsync(Edit(_sourceA, 1, _unmatched), Ct)).ShouldBe(EditOutcome.Updated);
        (await handler.HandleAsync(Edit(_sourceA, 1, _alert + " знову"), Ct)).ShouldBe(EditOutcome.Updated);

        (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem().Kind.ShouldBe(NotificationKind.New);
    }

    // AC-F9.3: only a message that previously did not match is notified; one stored as matched while notifications
    // were disabled stays without a notification.
    [Fact]
    public async Task AC_F9_3_edit_of_a_message_that_already_matched_creates_no_notification()
    {
        await StoredAsync(_sourceA, 1, _alert);

        (await Handler().Handler.HandleAsync(Edit(_sourceA, 1, _alert + " знову"), Ct)).ShouldBe(EditOutcome.Updated);

        (await NotificationsAsync(_sourceA, 1)).ShouldBeEmpty();
    }

    // AC-F9.3: an edit of an unknown message that matches → stored and notified.
    [Fact]
    public async Task AC_F9_3_edit_of_an_unknown_message_that_matches_is_stored_and_notified()
    {
        var edit = Edit(_sourceA, 7, _alert);

        (await Handler().Handler.HandleAsync(edit, Ct)).ShouldBe(EditOutcome.Pending);

        var row = await RowAsync(_sourceA, 7);
        (row.Text, row.IsMatched, row.PublishedAtUtc, row.ReceivedAtUtc, row.EditedAtUtc).ShouldBe((_alert, true, edit.PublishedAtUtc, edit.ReceivedAtUtc, edit.EditedAtUtc));
        (await NotificationsAsync(_sourceA, 7)).ShouldHaveSingleItem().Kind.ShouldBe(NotificationKind.EditNewMatch);
    }

    // AC-F9.3 with F6.4: the edited text duplicates a recent match from another source → SkippedDuplicate.
    [Fact]
    public async Task AC_F9_3_edit_whose_new_text_duplicates_a_recent_match_is_skipped_duplicate()
    {
        var original = await StoredAsync(_sourceB, 1, _alert, NotificationStatus.Sent, botMessageId: 555);
        await StoredAsync(_sourceA, 2, _unmatched);

        (await Handler().Handler.HandleAsync(Edit(_sourceA, 2, _alert), Ct)).ShouldBe(EditOutcome.SkippedDuplicate);

        var row = await RowAsync(_sourceA, 2);
        (row.IsDuplicate, row.DuplicateOfMessageId, row.DuplicateReason).ShouldBe((true, (long?)original, (DuplicateReason?)DuplicateReason.SameText));
        (await NotificationsAsync(_sourceA, 2)).ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.SkippedDuplicate);
    }

    // AC-F9.3 with F7.3: staleness from the edit time: 121 s → SkippedStale, 120 s → Pending. The message was
    // published 260 s ago, so by publication age both would be stale.
    [Theory]
    [InlineData(121, nameof(NotificationStatus.SkippedStale))]
    [InlineData(120, nameof(NotificationStatus.Pending))]
    public async Task AC_F7_3_edit_new_match_staleness_is_measured_from_the_edit_time(int editAgeSeconds, string expectedStatus)
    {
        var expected = Enum.Parse<NotificationStatus>(expectedStatus);
        await StoredAsync(_sourceA, 1, _unmatched);
        _time.Advance(TimeSpan.FromSeconds(200));

        await Handler().Handler.HandleAsync(Edit(_sourceA, 1, _alert, editAgeSeconds), Ct);

        (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem().Status.ShouldBe(expected);
    }

    // AC-F9.6: identical content hash → ignored in every mode, nothing written.
    [Theory]
    [InlineData(nameof(EditHandling.NotifyOnNewMatch))]
    [InlineData(nameof(EditHandling.ReplyOnChange))]
    public async Task AC_F9_6_edit_with_an_identical_hash_is_ignored(string modeName)
    {
        var mode = Enum.Parse<EditHandling>(modeName);
        await StoredAsync(_sourceA, 1, _alert, NotificationStatus.Sent, botMessageId: 555);
        var (handler, _) = Handler(mode);

        // Normalization makes these the same text (case and spacing).
        (await handler.HandleAsync(Edit(_sourceA, 1, "  шахед КУРСОМ на  Краснопавлівку "), Ct)).ShouldBe(EditOutcome.Unchanged);

        var row = await RowAsync(_sourceA, 1);
        (row.Text, row.EditedAtUtc).ShouldBe((_alert, (DateTime?)null));
        _time.TimersCreated.ShouldBe(0);
    }

    // AC-F9.2 with F6.1: matched → unmatched with StoreUnmatchedText = false → text dropped, match result cleared.
    [Fact]
    public async Task AC_F9_2_edit_to_unmatched_text_follows_the_text_storage_rule()
    {
        await StoredAsync(_sourceA, 1, _alert, NotificationStatus.Sent, botMessageId: 555);

        (await Handler(storeUnmatchedText: false).Handler.HandleAsync(Edit(_sourceA, 1, _unmatched), Ct)).ShouldBe(EditOutcome.Updated);

        var row = await RowAsync(_sourceA, 1);
        (row.Text, row.IsMatched, row.MatchedPatterns, row.ContentHash).ShouldBe(((string?)null, false, (string?)null, Hash(_unmatched)));
    }

    // AC-F9.2 with F6.1 and F6.7: with notifications disabled a new match is stored and logged only.
    [Fact]
    public async Task AC_F9_2_new_match_with_notifications_disabled_is_stored_without_a_notification()
    {
        await StoredAsync(_sourceA, 1, _unmatched);

        (await Handler(notificationsEnabled: false).Handler.HandleAsync(Edit(_sourceA, 1, _alert), Ct)).ShouldBe(EditOutcome.MatchLogged);

        (await RowAsync(_sourceA, 1)).IsMatched.ShouldBeTrue();
        (await NotificationsAsync(_sourceA, 1)).ShouldBeEmpty();
    }

    // AC-F9.5: ReplyOnChange schedules a reply only for a Sent alert; NotifyOnNewMatch never does.
    [Theory]
    [InlineData(nameof(EditHandling.ReplyOnChange), nameof(NotificationStatus.Sent), nameof(EditOutcome.ReplyScheduled), 1)]
    [InlineData(nameof(EditHandling.NotifyOnNewMatch), nameof(NotificationStatus.Sent), nameof(EditOutcome.Updated), 0)]
    [InlineData(nameof(EditHandling.ReplyOnChange), nameof(NotificationStatus.Pending), nameof(EditOutcome.Updated), 0)]
    [InlineData(nameof(EditHandling.ReplyOnChange), nameof(NotificationStatus.Failed), nameof(EditOutcome.Updated), 0)]
    public async Task AC_F9_5_reply_is_scheduled_only_for_a_sent_notification_in_reply_on_change(
        string modeName,
        string statusName,
        string expectedOutcome,
        int timers)
    {
        var (mode, status, expected) = (Enum.Parse<EditHandling>(modeName), Enum.Parse<NotificationStatus>(statusName), Enum.Parse<EditOutcome>(expectedOutcome));
        await StoredAsync(_sourceA, 1, _alert, status, botMessageId: status == NotificationStatus.Sent ? 555 : null);
        var (handler, replies) = Handler(mode);

        (await handler.HandleAsync(Edit(_sourceA, 1, _alert + " знову"), Ct)).ShouldBe(expected);

        _time.TimersCreated.ShouldBe(timers);
        _logs.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
        replies.Dispose();
    }

    // AC-F9.5: a Sent alert without a bot message id → no reply and a Warning.
    [Fact]
    public async Task AC_F9_5_sent_notification_without_bot_message_id_gets_no_reply_and_a_warning()
    {
        await StoredAsync(_sourceA, 1, _alert, NotificationStatus.Sent, botMessageId: null);

        (await Handler(EditHandling.ReplyOnChange).Handler.HandleAsync(Edit(_sourceA, 1, _alert + " знову"), Ct)).ShouldBe(EditOutcome.Updated);

        _time.TimersCreated.ShouldBe(0);
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("no bot message id"));
    }
}
