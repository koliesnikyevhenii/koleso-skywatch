using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Core.Deduplication;
using Skywatch.Core.Filtering;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using MonitoringOptions = Skywatch.Worker.Options.MonitoringOptions;

namespace Skywatch.Worker.Tests.Pipeline;

// Spec F6 and F7.1 (creation time) on SQLite in-memory with FakeTimeProvider. Defaults: forward window 60 min,
// same-text window 10 min across sources only, max message age 120 s. Fake ids only.
public sealed class MessageProcessorTests : IDisposable
{
    private const long _sourceA = -1000000000042;
    private const long _sourceB = -1000000000043;
    private const long _sourceC = -1000000000044;
    private const long _unknownChat = -1000000000099;
    private const string _sourceAName = "Test source A";
    private const string _pattern = "Settlement";
    private const string _alert = "Шахед курсом на Краснопавлівку";
    private const string _unmatched = "Тиша в районі";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly DispatchSignal _signal = new();

    public MessageProcessorTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        using var db = _db.CreateDbContext();
        foreach (var (chatId, name) in new[] { (_sourceA, _sourceAName), (_sourceB, "Test source B"), (_sourceC, "Test source C") })
        {
            db.Sources.Add(new Source { TelegramChatId = chatId, Name = name, Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch });
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private MessageProcessor Processor(
        bool notificationsEnabled = true,
        bool storeUnmatchedText = true,
        bool acrossSourcesOnly = true) =>
        new(
            _db,
            new KeywordFilter([new PatternDefinition(_pattern, "краснопавлівк", CaseSensitive: false, Enabled: true)]),
            new DuplicateDetector(Microsoft.Extensions.Options.Options.Create(new MonitoringOptions { ContentDuplicateAcrossSourcesOnly = acrossSourcesOnly })),
            _signal,
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoreUnmatchedText = storeUnmatchedText }),
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = notificationsEnabled }),
            _time,
            _loggerFactory.CreateLogger<MessageProcessor>());

    /// <summary>A new message received now, published <paramref name="ageSeconds"/> earlier.</summary>
    private IncomingMessage Message(long chatId, int id, string text = _alert, OriginKey? origin = null, int ageSeconds = 1) =>
        new(chatId, id, text, Now.AddSeconds(-ageSeconds), Now, origin ?? new OriginKey(chatId, id), IsEdit: false, EditedAtUtc: null);

    private static Task<ProcessOutcome> ProcessAsync(MessageProcessor processor, IncomingMessage message) =>
        processor.ProcessAsync(message, TestContext.Current.CancellationToken);

    private async Task<Message> RowAsync(long chatId, int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Messages.AsNoTracking().SingleAsync(m => m.Source!.TelegramChatId == chatId && m.TelegramMessageId == id, TestContext.Current.CancellationToken);
    }

    private async Task<List<Notification>> NotificationsAsync(long chatId, int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.Message!.Source!.TelegramChatId == chatId && n.Message.TelegramMessageId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync<T>(Func<SkywatchDbContext, IQueryable<T>> set)
    {
        await using var db = _db.CreateDbContext();
        return await set(db).CountAsync(TestContext.Current.CancellationToken);
    }

    // AC-F6.2: the same (source, message id) twice → one row and one notification.
    [Fact]
    public async Task AC_F6_2_same_message_processed_twice_creates_one_row_and_one_notification()
    {
        var processor = Processor();

        (await ProcessAsync(processor, Message(_sourceA, 1))).ShouldBe(ProcessOutcome.Pending);
        (await ProcessAsync(processor, Message(_sourceA, 1))).ShouldBe(ProcessOutcome.AlreadyProcessed);

        (await CountAsync(s => s.Messages)).ShouldBe(1);
        (await NotificationsAsync(_sourceA, 1)).Count.ShouldBe(1);
    }

    // AC-F6.2: a concurrent writer inserted the same key first → the unique-constraint violation is handled as
    // "already processed" (no error event), and the next message is processed on a fresh context.
    [Fact]
    public async Task AC_F6_2_unique_violation_from_a_concurrent_insert_is_already_processed_and_the_next_message_is_processed()
    {
        await using (var db = _db.CreateDbContext())
        {
            var sourceId = await db.Sources.Where(s => s.TelegramChatId == _sourceA).Select(s => s.Id).SingleAsync(TestContext.Current.CancellationToken);
            db.Messages.Add(new Message { SourceId = sourceId, TelegramMessageId = 1, ContentHash = "other", PublishedAtUtc = Now, ReceivedAtUtc = Now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var processor = Processor();

        (await ProcessAsync(processor, Message(_sourceA, 1))).ShouldBe(ProcessOutcome.AlreadyProcessed);
        (await ProcessAsync(processor, Message(_sourceA, 2))).ShouldBe(ProcessOutcome.Pending);

        (await NotificationsAsync(_sourceA, 1)).ShouldBeEmpty();
        (await RowAsync(_sourceA, 1)).ContentHash.ShouldBe("other");
        (await CountAsync(s => s.ServiceEvents)).ShouldBe(0);
    }

    // AC-F6.1: every message with text is stored with its metadata; unmatched text only with StoreUnmatchedText.
    [Theory]
    [InlineData(true, _unmatched)]
    [InlineData(false, null)]
    public async Task AC_F6_1_unmatched_text_is_stored_only_when_store_unmatched_text_is_true(bool storeUnmatchedText, string? expectedText)
    {
        var message = Message(_sourceA, 1, _unmatched, origin: new OriginKey(_sourceB, 77), ageSeconds: 3);

        (await ProcessAsync(Processor(storeUnmatchedText: storeUnmatchedText), message)).ShouldBe(ProcessOutcome.Unmatched);

        var row = await RowAsync(_sourceA, 1);
        row.Text.ShouldBe(expectedText);
        row.IsMatched.ShouldBeFalse();
        row.MatchedPatterns.ShouldBeNull();
        row.ContentHash.ShouldBe(ContentHasher.Hash(Skywatch.Core.Normalization.TextNormalizer.Normalize(_unmatched)));
        (row.OriginChatId, row.OriginMessageId).ShouldBe((_sourceB, 77));
        (row.PublishedAtUtc, row.ReceivedAtUtc, row.EditedAtUtc).ShouldBe((message.PublishedAtUtc, message.ReceivedAtUtc, (DateTime?)null));
        (await CountAsync(s => s.Notifications)).ShouldBe(0);
    }

    // AC-F6.1: a matched message keeps its text even with StoreUnmatchedText=false, plus the matched pattern names.
    [Fact]
    public async Task AC_F6_1_matched_text_and_pattern_names_are_stored()
    {
        await ProcessAsync(Processor(storeUnmatchedText: false), Message(_sourceA, 1));

        var row = await RowAsync(_sourceA, 1);
        row.Text.ShouldBe(_alert);
        row.IsMatched.ShouldBeTrue();
        row.MatchedPatterns.ShouldBe(_pattern);
        row.IsDuplicate.ShouldBeFalse();
    }

    // AC-F6.6: a matched, non-duplicate, fresh message gets a Pending notification of kind New.
    [Fact]
    public async Task AC_F6_6_matched_message_gets_a_pending_notification()
    {
        (await ProcessAsync(Processor(), Message(_sourceA, 1))).ShouldBe(ProcessOutcome.Pending);

        var notification = (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem();
        (notification.Kind, notification.Status, notification.Attempts, notification.CreatedAtUtc).ShouldBe((NotificationKind.New, NotificationStatus.Pending, 0, Now));
    }

    // AC-F6.4 (a) + F6.5: original, then its forward in another source within 60 min → the forward is a duplicate.
    [Fact]
    public async Task AC_F6_4_forward_of_an_earlier_original_within_the_window_is_a_forward_duplicate()
    {
        var processor = Processor();
        var origin = new OriginKey(_sourceA, 1);
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(60));

        (await ProcessAsync(processor, Message(_sourceB, 5, "Переслано: " + _alert, origin))).ShouldBe(ProcessOutcome.SkippedDuplicate);

        var original = await RowAsync(_sourceA, 1);
        var forward = await RowAsync(_sourceB, 5);
        (forward.IsDuplicate, forward.DuplicateOfMessageId, forward.DuplicateReason).ShouldBe((true, (long?)original.Id, (DuplicateReason?)DuplicateReason.Forward));
        (await NotificationsAsync(_sourceB, 5)).ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.SkippedDuplicate);
        original.IsDuplicate.ShouldBeFalse();
    }

    // AC-F6.4 (a): forward first, original later → the original (arriving second) is the duplicate.
    [Fact]
    public async Task AC_F6_4_when_the_forward_arrives_first_the_original_is_the_duplicate()
    {
        var processor = Processor();
        var origin = new OriginKey(_sourceA, 1);
        await ProcessAsync(processor, Message(_sourceB, 5, "Переслано: " + _alert, origin));
        _time.Advance(TimeSpan.FromSeconds(5));

        (await ProcessAsync(processor, Message(_sourceA, 1))).ShouldBe(ProcessOutcome.SkippedDuplicate);

        var original = await RowAsync(_sourceA, 1);
        original.DuplicateOfMessageId.ShouldBe((await RowAsync(_sourceB, 5)).Id);
        original.DuplicateReason.ShouldBe(DuplicateReason.Forward);
    }

    // AC-F6.4 (a): outside the 60-minute window the forward is unique.
    [Fact]
    public async Task AC_F6_4_forward_after_the_window_is_not_a_duplicate()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));

        (await ProcessAsync(processor, Message(_sourceB, 5, "Переслано: " + _alert, new OriginKey(_sourceA, 1)))).ShouldBe(ProcessOutcome.Pending);
    }

    // AC-F6.4 (b): identical text from a different source within 10 min → SameText duplicate.
    [Fact]
    public async Task AC_F6_4_same_text_from_another_source_within_the_window_is_a_same_text_duplicate()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(10));

        (await ProcessAsync(processor, Message(_sourceB, 5, "  ШАХЕД курсом на  Краснопавлівку"))).ShouldBe(ProcessOutcome.SkippedDuplicate);

        var duplicate = await RowAsync(_sourceB, 5);
        (duplicate.DuplicateOfMessageId, duplicate.DuplicateReason).ShouldBe(((long?)(await RowAsync(_sourceA, 1)).Id, (DuplicateReason?)DuplicateReason.SameText));
    }

    // AC-F6.4 (b): the same pair after 10 minutes → both unique.
    [Fact]
    public async Task AC_F6_4_same_text_after_the_window_is_not_a_duplicate()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        (await ProcessAsync(processor, Message(_sourceB, 5))).ShouldBe(ProcessOutcome.Pending);
        (await RowAsync(_sourceB, 5)).IsDuplicate.ShouldBeFalse();
    }

    // AC-F6.4 (b): a repeat in the same source is always sent when ContentDuplicateAcrossSourcesOnly=true,
    // and is a duplicate when it is false.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AC_F6_4_same_text_repeated_in_the_same_source_depends_on_across_sources_only(bool acrossSourcesOnly, bool duplicate)
    {
        var processor = Processor(acrossSourcesOnly: acrossSourcesOnly);
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(2));

        (await ProcessAsync(processor, Message(_sourceA, 2))).ShouldBe(duplicate ? ProcessOutcome.SkippedDuplicate : ProcessOutcome.Pending);
    }

    // AC-F6.4: the origin key is checked first, so a forward that also has identical text is reason Forward.
    [Fact]
    public async Task AC_F6_4_origin_key_is_checked_before_content_hash()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));

        await ProcessAsync(processor, Message(_sourceB, 5, _alert, new OriginKey(_sourceA, 1)));

        (await RowAsync(_sourceB, 5)).DuplicateReason.ShouldBe(DuplicateReason.Forward);
    }

    // AC-F6.4: only matched messages take part; an unmatched message with the same origin is not an original.
    [Fact]
    public async Task AC_F6_4_unmatched_earlier_message_is_not_an_original()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1, _unmatched));

        (await ProcessAsync(processor, Message(_sourceB, 5, _alert, new OriginKey(_sourceA, 1)))).ShouldBe(ProcessOutcome.Pending);
    }

    // Plan P3b: a duplicate of a duplicate points to the first non-duplicate.
    [Fact]
    public async Task AC_F6_5_duplicate_of_a_duplicate_points_to_the_first_non_duplicate()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync(processor, Message(_sourceB, 5));
        _time.Advance(TimeSpan.FromMinutes(1));

        await ProcessAsync(processor, Message(_sourceC, 9));

        var original = await RowAsync(_sourceA, 1);
        (await RowAsync(_sourceB, 5)).DuplicateOfMessageId.ShouldBe(original.Id);
        (await RowAsync(_sourceC, 9)).DuplicateOfMessageId.ShouldBe(original.Id);
    }

    // AC-F6.4: only non-duplicates are originals. The original is outside the window and only its duplicate is
    // inside it → the later copy is unique.
    [Fact]
    public async Task AC_F6_4_a_duplicate_is_never_an_original()
    {
        var processor = Processor();
        await ProcessAsync(processor, Message(_sourceA, 1));
        _time.Advance(TimeSpan.FromMinutes(9));
        await ProcessAsync(processor, Message(_sourceB, 5));
        _time.Advance(TimeSpan.FromMinutes(6));

        (await ProcessAsync(processor, Message(_sourceC, 9))).ShouldBe(ProcessOutcome.Pending);
    }

    // AC-F6.7: notifications disabled → no notification rows (not even for duplicates); the match is logged at
    // Information with the source name and pattern names, never the text.
    [Fact]
    public async Task AC_F6_7_notifications_disabled_creates_no_rows_and_logs_the_match()
    {
        var processor = Processor(notificationsEnabled: false);

        (await ProcessAsync(processor, Message(_sourceA, 1))).ShouldBe(ProcessOutcome.MatchLogged);
        await ProcessAsync(processor, Message(_sourceB, 5));

        (await CountAsync(s => s.Notifications)).ShouldBe(0);
        (await CountAsync(s => s.Messages)).ShouldBe(2);
        var entry = _logs.Entries.Where(e => e.Message.Contains("message 1 matched")).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(_sourceAName);
        entry.Message.ShouldContain(_pattern);
        _logs.Lines.ShouldNotContain(l => l.Contains(_alert));
    }

    // Plan P4b: the dispatcher is woken after the commit of a Pending notification, and only then.
    [Fact]
    public async Task P4b_pending_notification_wakes_the_dispatcher_after_commit()
    {
        var processor = Processor();
        var waiting = _signal.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        (await ProcessAsync(processor, Message(_sourceA, 1, ageSeconds: 300))).ShouldBe(ProcessOutcome.SkippedStale);
        (await ProcessAsync(processor, Message(_sourceA, 2, _unmatched))).ShouldBe(ProcessOutcome.Unmatched);
        waiting.IsCompleted.ShouldBeFalse();

        (await ProcessAsync(processor, Message(_sourceA, 3))).ShouldBe(ProcessOutcome.Pending);

        await waiting;
        (await NotificationsAsync(_sourceA, 3)).ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Pending);
    }

    // AC-F7.1 (creation time) with the AC-F7.2 default: age 121 s → SkippedStale; 120 s → Pending.
    [Theory]
    [InlineData(121, true)]
    [InlineData(120, false)]
    public async Task AC_F7_1_stale_message_at_creation_is_skipped_stale(int ageSeconds, bool stale)
    {
        (await ProcessAsync(Processor(), Message(_sourceA, 1, ageSeconds: ageSeconds))).ShouldBe(stale ? ProcessOutcome.SkippedStale : ProcessOutcome.Pending);

        (await NotificationsAsync(_sourceA, 1)).ShouldHaveSingleItem().Status.ShouldBe(stale ? NotificationStatus.SkippedStale : NotificationStatus.Pending);
    }

    // AC-F6.8: an exception while processing one message → Error log, ProcessingError service event, and the next
    // message is processed.
    [Fact]
    public async Task AC_F6_8_exception_is_recorded_as_processing_error_and_the_next_message_is_processed()
    {
        var processor = Processor();

        (await ProcessAsync(processor, Message(_unknownChat, 1))).ShouldBe(ProcessOutcome.Failed);
        (await ProcessAsync(processor, Message(_sourceA, 2))).ShouldBe(ProcessOutcome.Pending);

        await using var db = _db.CreateDbContext();
        var error = await db.ServiceEvents.SingleAsync(TestContext.Current.CancellationToken);
        (error.Kind, error.OccurredAtUtc).ShouldBe((ServiceEventKind.ProcessingError, Now));
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("message 1"));
        _logs.Lines.ShouldNotContain(l => l.Contains(_alert));
    }
}
