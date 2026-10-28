using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Deduplication;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Pipeline;

internal enum ProcessOutcome
{
    /// <summary>The <c>(source, message id)</c> is already stored (AC-F6.2); nothing was written.</summary>
    AlreadyProcessed,
    Unmatched,

    /// <summary>A match with notifications disabled: stored and logged only (AC-F6.7).</summary>
    MatchLogged,
    Pending,
    SkippedDuplicate,
    SkippedStale,

    /// <summary>An exception was logged and recorded as a <c>ProcessingError</c> service event (AC-F6.8).</summary>
    Failed,
}

/// <summary>
/// Spec F6 for one new message: normalize → filter → hash → dedup → staleness, then the message row and its
/// notification in one transaction (AC-F6.6), on a new context per message.
/// </summary>
internal sealed partial class MessageProcessor(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    KeywordFilter filter,
    DuplicateDetector duplicates,
    IOptions<StorageOptions> storage,
    IOptions<NotificationOptions> notifications,
    TimeProvider time,
    ILogger<MessageProcessor> logger)
{
    private const int _sqliteConstraint = 19;
    private const int _sqliteConstraintUnique = 2067;

    private readonly StalenessPolicy _staleness = new(TimeSpan.FromSeconds(notifications.Value.MaxMessageAgeSeconds));

    public async Task<ProcessOutcome> ProcessAsync(IncomingMessage message, CancellationToken ct)
    {
        try
        {
            return await ProcessCoreAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AC-F6.8: never the text, and the pipeline continues with the next message.
            LogFailed(logger, ex, message.ChatId, message.MessageId);
            await RecordErrorAsync(message, ex, ct);
            return ProcessOutcome.Failed;
        }
    }

    private async Task<ProcessOutcome> ProcessCoreAsync(IncomingMessage message, CancellationToken ct)
    {
        var normalized = TextNormalizer.Normalize(message.Text);
        var result = filter.Match(normalized);
        var hash = ContentHasher.Hash(normalized);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Sources.SingleOrDefaultAsync(s => s.TelegramChatId == message.ChatId, ct)
            ?? throw new InvalidOperationException($"Chat {message.ChatId} is not a stored source.");

        var row = new Message
        {
            SourceId = source.Id,
            TelegramMessageId = message.MessageId,
            Text = result.IsMatch || storage.Value.StoreUnmatchedText ? message.Text : null, // AC-F6.1
            PublishedAtUtc = message.PublishedAtUtc,
            ReceivedAtUtc = message.ReceivedAtUtc,
            EditedAtUtc = message.EditedAtUtc,
            ContentHash = hash,
            OriginChatId = message.Origin.ChatId,
            OriginMessageId = message.Origin.MessageId,
            IsMatched = result.IsMatch,
            MatchedPatterns = result.IsMatch ? string.Join(',', result.Hits.Select(h => h.PatternName)) : null,
        };
        db.Messages.Add(row);

        var outcome = ProcessOutcome.Unmatched;
        if (result.IsMatch)
        {
            // AC-F6.5: a duplicate is stored and marked, and its notification is never sent.
            if (await duplicates.FindAsync(db, source.Id, message.Origin, hash, message.ReceivedAtUtc, ct) is { } duplicate)
            {
                row.IsDuplicate = true;
                row.DuplicateOfMessageId = duplicate.OriginalMessageId;
                row.DuplicateReason = duplicate.Reason;
            }

            outcome = Decide(row);
            if (notifications.Value.Enabled)
            {
                db.Notifications.Add(new Notification { Message = row, Kind = NotificationKind.New, Status = ToStatus(outcome), CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: _sqliteConstraint, SqliteExtendedErrorCode: _sqliteConstraintUnique })
        {
            // AC-F6.2: also covers a concurrent insert of the same key between our read and write.
            LogAlreadyProcessed(logger, source.Name, message.ChatId, message.MessageId);
            return ProcessOutcome.AlreadyProcessed;
        }

        if (result.IsMatch)
        {
            var patterns = string.Join(", ", result.Hits.Select(h => h.PatternName));
            var latency = (message.ReceivedAtUtc - message.PublishedAtUtc).TotalSeconds;
            LogMatch(logger, source.Name, message.ChatId, message.MessageId, patterns, latency, outcome);
        }

        return outcome;
    }

    /// <summary>F7.1 at creation time; a duplicate is reported as such even when it is also stale.</summary>
    private ProcessOutcome Decide(Message row) =>
        row.IsDuplicate ? ProcessOutcome.SkippedDuplicate
        : !notifications.Value.Enabled ? ProcessOutcome.MatchLogged
        : _staleness.IsStale(row.PublishedAtUtc, time.GetUtcNow().UtcDateTime) ? ProcessOutcome.SkippedStale
        : ProcessOutcome.Pending;

    private static NotificationStatus ToStatus(ProcessOutcome outcome) => outcome switch
    {
        ProcessOutcome.SkippedDuplicate => NotificationStatus.SkippedDuplicate,
        ProcessOutcome.SkippedStale => NotificationStatus.SkippedStale,
        _ => NotificationStatus.Pending,
    };

    private async Task RecordErrorAsync(IncomingMessage message, Exception exception, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ServiceEvents.Add(new ServiceEvent
            {
                Kind = ServiceEventKind.ProcessingError,
                OccurredAtUtc = time.GetUtcNow().UtcDateTime,
                Detail = $"chat {message.ChatId}, message {message.MessageId}: {exception.GetType().Name}: {exception.Message}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source} ({ChatId}): message {MessageId} matched {Patterns}, latency {LatencySeconds:0.0} s, {Outcome}")]
    private static partial void LogMatch(ILogger logger, string source, long chatId, int messageId, string patterns, double latencySeconds, ProcessOutcome outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source} ({ChatId}): message {MessageId} is already stored; skipped.")]
    private static partial void LogAlreadyProcessed(ILogger logger, string source, long chatId, int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing message {MessageId} from {ChatId} failed; continuing with the next message.")]
    private static partial void LogFailed(ILogger logger, Exception exception, long chatId, int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the ProcessingError service event failed.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
