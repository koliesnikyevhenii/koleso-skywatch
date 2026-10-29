using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Spec F12 with the default retention options (unmatched text 3 days, matched text 7 days, metadata 30 days).
public sealed class RetentionServiceTests : IDisposable
{
    private const long _chatId = -1000000000042;
    private const string _text = "Текст повідомлення";
    private static readonly TimeSpan _second = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new(concurrent: true);
    private int _nextMessageId = 1;

    public void Dispose() => _db.Dispose();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private RetentionService Service(int batchSize = 500) =>
        new(_db, Microsoft.Extensions.Options.Options.Create(new StorageOptions { RetentionBatchSize = batchSize }), _time, NullLogger<RetentionService>.Instance);

    private Task RunOnceAsync(int batchSize = 500) => Service(batchSize).RunOnceAsync(TestContext.Current.CancellationToken);

    private static async Task<int> SourceIdAsync(SkywatchDbContext db)
    {
        if (await db.Sources.FirstOrDefaultAsync(TestContext.Current.CancellationToken) is { } existing)
        {
            return existing.Id;
        }

        var source = new Source { TelegramChatId = _chatId, Name = "Test source", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
        db.Sources.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return source.Id;
    }

    /// <summary>A message received <paramref name="age"/> ago, published one second before that.</summary>
    private async Task<long> AddMessageAsync(TimeSpan age, bool matched, DateTime? publishedAtUtc = null, long? duplicateOf = null)
    {
        await using var db = _db.CreateDbContext();
        var messageId = _nextMessageId++;
        var message = new Message
        {
            SourceId = await SourceIdAsync(db),
            TelegramMessageId = messageId,
            Text = _text,
            ReceivedAtUtc = Now - age,
            PublishedAtUtc = publishedAtUtc ?? Now - age - _second,
            ContentHash = $"hash-{messageId}",
            OriginChatId = _chatId,
            OriginMessageId = messageId,
            IsMatched = matched,
            MatchedPatterns = matched ? "Settlement" : null,
            IsDuplicate = duplicateOf is not null,
            DuplicateOfMessageId = duplicateOf,
            DuplicateReason = duplicateOf is not null ? DuplicateReason.Forward : null,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return message.Id;
    }

    private async Task AddNotificationAsync(long messageId, DateTime createdAtUtc)
    {
        await using var db = _db.CreateDbContext();
        db.Notifications.Add(new Notification { MessageId = messageId, Kind = NotificationKind.New, Status = NotificationStatus.Sent, CreatedAtUtc = createdAtUtc });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddServiceEventAsync(TimeSpan age)
    {
        await using var db = _db.CreateDbContext();
        db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.Reconnected, OccurredAtUtc = Now - age });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Message?> FindMessageAsync(long id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }

    // AC-F12.1: text of unmatched messages older than UnmatchedRetentionDays (3) is set to null.
    [Fact]
    public async Task AC_F12_1_text_of_unmatched_messages_older_than_unmatched_retention_days_is_set_to_null()
    {
        var older = await AddMessageAsync(TimeSpan.FromDays(3) + _second, matched: false);
        var atLimit = await AddMessageAsync(TimeSpan.FromDays(3), matched: false);
        var matchedOlder = await AddMessageAsync(TimeSpan.FromDays(3) + _second, matched: true);

        await RunOnceAsync();

        var cleared = (await FindMessageAsync(older)).ShouldNotBeNull();
        cleared.Text.ShouldBeNull();
        cleared.ContentHash.ShouldBe($"hash-1"); // metadata stays
        (await FindMessageAsync(atLimit)).ShouldNotBeNull().Text.ShouldBe(_text);
        (await FindMessageAsync(matchedOlder)).ShouldNotBeNull().Text.ShouldBe(_text);
    }

    // AC-F12.2: text of matched messages older than RetentionDays (7) is set to null.
    [Fact]
    public async Task AC_F12_2_text_of_matched_messages_older_than_retention_days_is_set_to_null()
    {
        var older = await AddMessageAsync(TimeSpan.FromDays(7) + _second, matched: true);
        var atLimit = await AddMessageAsync(TimeSpan.FromDays(7), matched: true);

        await RunOnceAsync();

        var cleared = (await FindMessageAsync(older)).ShouldNotBeNull();
        cleared.Text.ShouldBeNull();
        cleared.MatchedPatterns.ShouldBe("Settlement");
        (await FindMessageAsync(atLimit)).ShouldNotBeNull().Text.ShouldBe(_text);
    }

    // AC-F12.3: Messages, Notifications and ServiceEvents rows older than MetadataRetentionDays (30) are deleted.
    [Fact]
    public async Task AC_F12_3_messages_notifications_and_service_events_older_than_metadata_retention_days_are_deleted()
    {
        var older = await AddMessageAsync(TimeSpan.FromDays(30) + _second, matched: true);
        await AddNotificationAsync(older, Now - TimeSpan.FromDays(30));
        var atLimit = await AddMessageAsync(TimeSpan.FromDays(30), matched: true);
        await AddNotificationAsync(atLimit, Now - TimeSpan.FromDays(30));
        await AddServiceEventAsync(TimeSpan.FromDays(30) + _second);
        await AddServiceEventAsync(TimeSpan.FromDays(30));

        await RunOnceAsync();

        await using var db = _db.CreateDbContext();
        var ct = TestContext.Current.CancellationToken;
        (await db.Messages.Select(m => m.Id).ToListAsync(ct)).ShouldBe([atLimit]);
        (await db.Notifications.Select(n => n.MessageId).ToListAsync(ct)).ShouldBe([atLimit]);
        (await db.ServiceEvents.Select(e => e.OccurredAtUtc).ToListAsync(ct)).ShouldBe([Now - TimeSpan.FromDays(30)]);
    }

    // AC-F12.4: age is measured from ReceivedAtUtc, not PublishedAtUtc (and not from a notification's CreatedAtUtc).
    [Fact]
    public async Task AC_F12_4_age_for_retention_is_measured_from_received_at()
    {
        var publishedLongAgo = Now - TimeSpan.FromDays(40);
        var caughtUp = await AddMessageAsync(TimeSpan.FromDays(1), matched: false, publishedAtUtc: publishedLongAgo);
        await AddNotificationAsync(caughtUp, Now - TimeSpan.FromDays(40));

        await RunOnceAsync();

        (await FindMessageAsync(caughtUp)).ShouldNotBeNull().Text.ShouldBe(_text);
        await using var db = _db.CreateDbContext();
        (await db.Notifications.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    // Plan P3a boundary: an original older than 30 days is deleted with its notification; its younger duplicate stays,
    // with a null reference and IsDuplicate still true, and no error.
    [Fact]
    public async Task P3a_deleting_an_original_keeps_its_younger_duplicate_with_a_null_reference()
    {
        var original = await AddMessageAsync(TimeSpan.FromDays(30) + _second, matched: true);
        await AddNotificationAsync(original, Now - TimeSpan.FromDays(30));
        var duplicate = await AddMessageAsync(TimeSpan.FromDays(29), matched: true, duplicateOf: original);

        await Should.NotThrowAsync(RunOnceAsync());

        (await FindMessageAsync(original)).ShouldBeNull();
        var kept = (await FindMessageAsync(duplicate)).ShouldNotBeNull();
        kept.IsDuplicate.ShouldBeTrue();
        kept.DuplicateOfMessageId.ShouldBeNull();
        await using var db = _db.CreateDbContext();
        (await db.Notifications.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    // Plan P3a: deletion runs in batches of RetentionBatchSize, and one pass still removes everything that is due.
    [Fact]
    public async Task P3a_one_pass_removes_all_due_rows_in_batches_of_retention_batch_size()
    {
        List<long> due = [];
        for (var i = 0; i < 5; i++)
        {
            due.Add(await AddMessageAsync(TimeSpan.FromDays(31), matched: false));
            await AddServiceEventAsync(TimeSpan.FromDays(31));
        }

        for (var i = 0; i < 5; i++)
        {
            await AddMessageAsync(TimeSpan.FromDays(4), matched: false);
        }

        await RunOnceAsync(batchSize: 2);

        await using var db = _db.CreateDbContext();
        var ct = TestContext.Current.CancellationToken;
        (await db.Messages.CountAsync(ct)).ShouldBe(5);
        (await db.Messages.CountAsync(m => m.Text != null, ct)).ShouldBe(0);
        (await db.ServiceEvents.CountAsync(ct)).ShouldBe(0);
    }

    // Spec F12: the hosted service runs at startup and then hourly.
    [Fact]
    public async Task F12_retention_runs_at_startup_and_then_every_hour()
    {
        var atStart = await AddMessageAsync(TimeSpan.FromDays(31), matched: false);
        var service = Service();
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Eventually.ShouldHappen(() => FindMessageAsync(atStart).Result is null, "startup pass");

            var later = await AddMessageAsync(TimeSpan.FromDays(31), matched: false);
            _time.Advance(TimeSpan.FromHours(1));

            await Eventually.ShouldHappen(() => FindMessageAsync(later).Result is null, "hourly pass");
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Plan P3a smoke test: on a real file database (WAL, busy timeout), batched retention concurrent with inserts
    // raises no SqliteException.
    [Fact]
    public async Task P3a_batched_retention_concurrent_with_inserts_on_a_file_database_raises_no_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Storage:RetentionBatchSize"] = "10";
        using var host = TestHost.Build([], settings, time: _time);
        await host.StartAsync(ct);
        var factory = host.Services.GetRequiredService<IDbContextFactory<SkywatchDbContext>>();

        int sourceId;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            sourceId = await SourceIdAsync(db);
            for (var i = 0; i < 300; i++)
            {
                db.Messages.Add(FileMessage(sourceId, 100_000 + i, TimeSpan.FromDays(31)));
            }

            await db.SaveChangesAsync(ct);
        }

        var inserts = Task.Run(
            async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await using var db = await factory.CreateDbContextAsync(ct);
                    db.Messages.Add(FileMessage(sourceId, i, TimeSpan.Zero));
                    await db.SaveChangesAsync(ct);
                }
            },
            ct);
        var retention = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<RetentionService>().Single();
        var passes = Task.Run(
            async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await retention.RunOnceAsync(ct);
                }
            },
            ct);

        await Should.NotThrowAsync(Task.WhenAll(inserts, passes));

        await using var check = await factory.CreateDbContextAsync(ct);
        (await check.Messages.CountAsync(ct)).ShouldBe(200);
        await host.StopAsync(ct);
    }

    private Message FileMessage(int sourceId, int messageId, TimeSpan age) => new()
    {
        SourceId = sourceId,
        TelegramMessageId = messageId,
        Text = _text,
        ReceivedAtUtc = Now - age,
        PublishedAtUtc = Now - age,
        ContentHash = $"hash-{messageId}",
        OriginChatId = _chatId,
        OriginMessageId = messageId,
    };
}
