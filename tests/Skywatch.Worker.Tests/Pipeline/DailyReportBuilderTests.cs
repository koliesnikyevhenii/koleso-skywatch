using Skywatch.Core.Formatting;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Pipeline;

// Spec AC-F11.2 on seeded data for the Kyiv day 2026-10-27 (22:00 UTC on the 26th to 22:00 UTC on the 27th).
// SQLite in-memory, fake ids only.
public sealed class DailyReportBuilderTests : IDisposable
{
    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);
    private static readonly ReportPeriod _period = ReportPeriod.ForDay(new DateOnly(2026, 10, 27), _kyiv);
    private static readonly DateTime _start = _period.StartUtc;
    private static readonly DateTime _end = _period.EndUtc;

    private readonly SqliteTestDb _db = new();
    private readonly DailyReportBuilder _builder;
    private readonly Source _source = new() { TelegramChatId = -1000000000042, Name = "Test source A", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
    private int _nextTelegramId;

    public DailyReportBuilderTests()
    {
        var monitoring = new MonitoringOptions
        {
            Patterns =
            [
                new PatternOptions { Name = "Settlement", Regex = "x" },
                new PatternOptions { Name = "Adjective", Regex = "x" },
                new PatternOptions { Name = "Abbreviation", Regex = "x" },
                new PatternOptions { Name = "Colloquial", Regex = "x" },
                new PatternOptions { Name = "Switched off", Regex = "x", Enabled = false },
            ],
        };
        _builder = new DailyReportBuilder(_db, Microsoft.Extensions.Options.Options.Create(monitoring));
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<DailyReport> BuildAsync() => _builder.BuildAsync(_period, Ct);

    /// <summary>A message received at <paramref name="receivedAtUtc"/>, published <paramref name="latencySeconds"/> earlier.</summary>
    private Message AddMessage(DateTime receivedAtUtc, double latencySeconds, string? patterns = null, DuplicateReason? duplicate = null)
    {
        using var db = _db.CreateDbContext();
        if (_source.Id == 0)
        {
            db.Sources.Add(_source);
        }
        else
        {
            db.Sources.Attach(_source);
        }

        var id = ++_nextTelegramId;
        var message = new Message
        {
            Source = _source,
            TelegramMessageId = id,
            PublishedAtUtc = receivedAtUtc.AddSeconds(-latencySeconds),
            ReceivedAtUtc = receivedAtUtc,
            ContentHash = $"hash-{id}",
            OriginChatId = _source.TelegramChatId,
            OriginMessageId = id,
            IsMatched = patterns is not null,
            MatchedPatterns = patterns,
            IsDuplicate = duplicate is not null,
            DuplicateReason = duplicate,
        };
        db.Messages.Add(message);
        db.SaveChanges();
        return message;
    }

    private void AddNotification(Message message, NotificationStatus status, NotificationKind kind = NotificationKind.New, NotificationLabel? label = null, double? sentAfterSeconds = null, DateTime? createdAtUtc = null)
    {
        using var db = _db.CreateDbContext();
        db.Notifications.Add(new Notification
        {
            MessageId = message.Id,
            Kind = kind,
            Status = status,
            CreatedAtUtc = createdAtUtc ?? message.ReceivedAtUtc,
            SentAtUtc = sentAfterSeconds is { } s ? message.ReceivedAtUtc.AddSeconds(s) : null,
            Label = label,
        });
        db.SaveChanges();
    }

    private void AddEvent(ServiceEventKind kind, DateTime atUtc)
    {
        using var db = _db.CreateDbContext();
        db.ServiceEvents.Add(new ServiceEvent { Kind = kind, OccurredAtUtc = atUtc });
        db.SaveChanges();
    }

    private void SeedMessages()
    {
        // Outside the period: just before the start and exactly at the (exclusive) end.
        var before = AddMessage(_start.AddSeconds(-1), 1, "Settlement");
        AddNotification(before, NotificationStatus.Sent, label: NotificationLabel.Useful, sentAfterSeconds: 1);
        AddMessage(_end, 1, "Settlement");

        var t = _start;
        AddMessage(t, 1);
        AddMessage(t.AddHours(1), 2);
        var useful = AddMessage(t.AddHours(2), 3, "Settlement");
        AddNotification(useful, NotificationStatus.Sent, label: NotificationLabel.Useful, sentAfterSeconds: 0.5);
        AddNotification(useful, NotificationStatus.Sent, NotificationKind.EditReply, sentAfterSeconds: 60); // not a match notification
        AddNotification(AddMessage(t.AddHours(3), 4, "Settlement,Adjective"), NotificationStatus.Sent, label: NotificationLabel.False, sentAfterSeconds: 1);
        AddNotification(AddMessage(t.AddHours(4), 5, "Adjective", DuplicateReason.Forward), NotificationStatus.SkippedDuplicate);
        AddNotification(AddMessage(t.AddHours(5), 6, "Settlement", DuplicateReason.SameText), NotificationStatus.SkippedDuplicate);
        AddNotification(AddMessage(t.AddHours(6), 200, "Colloquial"), NotificationStatus.SkippedStale);
        AddNotification(AddMessage(t.AddHours(7), 8, "Settlement"), NotificationStatus.Failed);
        AddNotification(AddMessage(t.AddHours(8), 9, "Abbreviation"), NotificationStatus.Sent, sentAfterSeconds: 2);

        // Edited into a match an hour after it arrived: counts as a match, but not in the send latency.
        AddNotification(AddMessage(t.AddHours(9), 10, "Settlement"), NotificationStatus.Sent, NotificationKind.EditNewMatch, NotificationLabel.Duplicate, sentAfterSeconds: 3600);

        // Matched while notifications were disabled, by a pattern no longer configured.
        AddMessage(_end.AddTicks(-1), 11, "Retired");
    }

    // AC-F11.2: volumes, matches per pattern, duplicates split forwards / same text, unique matches, statuses, labels.
    [Fact]
    public async Task AC_F11_2_counts_messages_matches_duplicates_statuses_and_labels()
    {
        SeedMessages();

        var report = await BuildAsync();

        report.Period.ShouldBe(_period);
        report.MessagesReceived.ShouldBe(11);
        report.Matches.ShouldBe(9);
        report.ForwardDuplicates.ShouldBe(1);
        report.SameTextDuplicates.ShouldBe(1);
        report.UniqueMatches.ShouldBe(7);
        (report.Sent, report.SkippedStale, report.Failed).ShouldBe((4, 1, 1));
        (report.LabeledUseful, report.LabeledFalse, report.LabeledDuplicate, report.Unlabeled).ShouldBe((1, 1, 1, 1));
    }

    // AC-F11.2: precision per pattern counts a message with two patterns in both; configured patterns come first
    // (a silent one shows 0, a disabled one is left out), then names found only in the data.
    [Fact]
    public async Task AC_F11_2_pattern_stats_count_a_multi_pattern_message_in_each_pattern()
    {
        SeedMessages();

        var report = await BuildAsync();

        report.Patterns.ShouldBe(
        [
            new PatternStats("Settlement", Matches: 5, Useful: 1, Labeled: 3),
            new PatternStats("Adjective", Matches: 2, Useful: 0, Labeled: 1),
            new PatternStats("Abbreviation", Matches: 1, Useful: 0, Labeled: 0),
            new PatternStats("Colloquial", Matches: 1, Useful: 0, Labeled: 0),
            new PatternStats("Retired", Matches: 1, Useful: 0, Labeled: 0),
        ]);
        DailyReportFormatter.Precision(report.LabeledUseful, report.Labeled).ShouldBe("33% (1/3)");
    }

    // AC-F11.2: p50 / p95 of ReceivedAt − PublishedAt over all messages, and of SentAt − ReceivedAt over sent New notifications.
    [Fact]
    public async Task AC_F11_2_latency_percentiles()
    {
        SeedMessages();

        var report = await BuildAsync();

        // 1 2 3 4 5 6 8 9 10 11 200 s
        report.ReceiveLatency.ShouldBe(new LatencyStats(11, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(200)));

        // 0.5 1 2 s
        report.SendLatency.ShouldBe(new LatencyStats(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    }

    // AC-F11.2: reconnect count and total disconnected time, including a disconnect open at the start, one ended by
    // a service stop, and one still open at the end; processing errors in the period.
    [Fact]
    public async Task AC_F11_2_reconnects_disconnected_time_and_processing_errors()
    {
        AddEvent(ServiceEventKind.Disconnected, _start.AddMinutes(-10));
        AddEvent(ServiceEventKind.ProcessingError, _start.AddMinutes(-1));
        AddEvent(ServiceEventKind.Reconnected, _start.AddMinutes(5)); // 300 s
        AddEvent(ServiceEventKind.Disconnected, _start.AddHours(1));
        AddEvent(ServiceEventKind.Reconnected, _start.AddHours(1).AddSeconds(30)); // 30 s
        AddEvent(ServiceEventKind.ProcessingError, _start.AddHours(1).AddSeconds(40));
        AddEvent(ServiceEventKind.Disconnected, _start.AddHours(2));
        AddEvent(ServiceEventKind.Stopped, _start.AddHours(2).AddSeconds(10)); // 10 s
        AddEvent(ServiceEventKind.Started, _start.AddHours(3));
        AddEvent(ServiceEventKind.ProcessingError, _start.AddHours(4));
        AddEvent(ServiceEventKind.Disconnected, _end.AddMinutes(-1)); // 60 s, open at the end
        AddEvent(ServiceEventKind.Disconnected, _end.AddSeconds(-30));
        AddEvent(ServiceEventKind.Reconnected, _end.AddMinutes(1));

        var report = await BuildAsync();

        report.Reconnects.ShouldBe(2);
        report.Disconnected.ShouldBe(TimeSpan.FromSeconds(400));
        report.ProcessingErrors.ShouldBe(2);
    }

    // AC-F11.2 + AC-F11.5: an empty day gives zeros, no latency samples and n/a precision, without errors.
    [Fact]
    public async Task AC_F11_5_empty_day_reports_zeros_and_n_a()
    {
        AddEvent(ServiceEventKind.Reconnected, _start.AddMinutes(-5));

        var report = await BuildAsync();

        report.MessagesReceived.ShouldBe(0);
        report.Patterns.Select(p => p.Matches).ShouldAllBe(m => m == 0);
        report.ReceiveLatency.ShouldBe(new LatencyStats(0, null, null));
        (report.Reconnects, report.Disconnected).ShouldBe((0, TimeSpan.Zero));
        DailyReportFormatter.Format(report).ShouldContain("Точність: n/a");
    }
}
