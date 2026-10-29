using Microsoft.EntityFrameworkCore;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Persistence;

// Spec §7: all timestamps are UTC DateTime, stored with a converter that guarantees DateTimeKind.Utc on read.
public sealed class UtcDateTimeConverterTests : IDisposable
{
    private static readonly DateTime _at = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<int> AddSourceAsync()
    {
        await using var db = _db.CreateDbContext();
        var source = new Source { TelegramChatId = -1000000000042, Name = "Test source", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = _at };
        db.Sources.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return source.Id;
    }

    private async Task AddMessageAsync(int sourceId, int id, DateTime receivedAtUtc, DateTime? editedAtUtc = null)
    {
        await using var db = _db.CreateDbContext();
        db.Messages.Add(new Message
        {
            SourceId = sourceId,
            TelegramMessageId = id,
            PublishedAtUtc = receivedAtUtc,
            ReceivedAtUtc = receivedAtUtc,
            EditedAtUtc = editedAtUtc,
            ContentHash = $"hash-{id}",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Spec7_timestamps_read_back_as_utc()
    {
        var sourceId = await AddSourceAsync();
        await AddMessageAsync(sourceId, 1, _at, editedAtUtc: _at.AddMinutes(1));

        await using var db = _db.CreateDbContext();
        var message = await db.Messages.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var source = await db.Sources.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        message.ReceivedAtUtc.ShouldBe(_at);
        message.ReceivedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        message.PublishedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        message.EditedAtUtc.ShouldNotBeNull().Kind.ShouldBe(DateTimeKind.Utc);
        source.CreatedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    // Plan P3a: range queries on ReceivedAtUtc are translated to SQL (no client evaluation exception).
    [Fact]
    public async Task Spec7_range_query_on_received_at_is_translated_to_sql()
    {
        var sourceId = await AddSourceAsync();
        await AddMessageAsync(sourceId, 1, _at.AddHours(-2));
        await AddMessageAsync(sourceId, 2, _at.AddHours(-1));
        await AddMessageAsync(sourceId, 3, _at);

        await using var db = _db.CreateDbContext();
        var from = _at.AddHours(-1);
        var query = db.Messages.Where(m => m.ReceivedAtUtc >= from && m.ReceivedAtUtc < _at).Select(m => m.TelegramMessageId);

        query.ToQueryString().ShouldContain("WHERE");
        (await query.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([2]);
    }

    // CLAUDE.md convention: the converter enforces Kind=Utc, so a local or unspecified time cannot be persisted by mistake.
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Convention_non_utc_timestamp_is_rejected_on_write(DateTimeKind kind)
    {
        await using var db = _db.CreateDbContext();
        db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.Started, OccurredAtUtc = DateTime.SpecifyKind(_at, kind) });

        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }
}
