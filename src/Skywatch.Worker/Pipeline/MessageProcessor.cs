using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Deduplication;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Core.Time;
using Skywatch.Worker.Hosting;
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
    DispatchSignal dispatch,
    IOptions<MonitoringOptions> monitoring,
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
            ReplyToTelegramMessageId = message.ReplyToMessageId,
        };
        db.Messages.Add(row);

        var outcome = ProcessOutcome.Unmatched;
        FollowUpTarget? followUp = null;
        if (!result.IsMatch && (followUp = await FindFollowUpTargetAsync(db, source.Id, message, ct)) is { } target)
        {
            // AC-F13.1: the text is what gets relayed, so it is kept whatever the storage rule says.
            row.Text = message.Text;
            row.FollowUpOfMessageId = target.OriginalMessageId;
            if (!target.OverLimit)
            {
                outcome = _staleness.IsStale(row.PublishedAtUtc, time.GetUtcNow().UtcDateTime) ? ProcessOutcome.SkippedStale : ProcessOutcome.Pending;
                db.Notifications.Add(new Notification { Message = row, Kind = NotificationKind.FollowUp, Status = ToStatus(outcome), CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            }
        }
        else if (result.IsMatch)
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

        if (outcome == ProcessOutcome.Pending)
        {
            dispatch.Notify(); // after the commit, so the dispatcher finds the row
        }

        if (followUp is { } relayed)
        {
            LogFollowUp(logger, source.Name, message.ChatId, message.MessageId, relayed.OriginalMessageId, relayed.OverLimit ? "over the limit, not notified" : outcome.ToString());
        }

        if (result.IsMatch)
        {
            var patterns = string.Join(", ", result.Hits.Select(h => h.PatternName));
            var latency = (message.ReceivedAtUtc - message.PublishedAtUtc).TotalSeconds;
            LogMatch(logger, source.Name, message.ChatId, message.MessageId, patterns, latency, outcome);
        }

        return outcome;
    }

    /// <summary>
    /// AC-F13.1/F13.2: the original whose sent alert this reply continues, or <see langword="null"/>. A reply to a
    /// follow-up continues the same original.
    /// </summary>
    private async Task<FollowUpTarget?> FindFollowUpTargetAsync(SkywatchDbContext db, int sourceId, IncomingMessage message, CancellationToken ct)
    {
        if (!monitoring.Value.FollowUpsEnabled || !notifications.Value.Enabled || message.ReplyToMessageId is not { } replyTo)
        {
            return null;
        }

        var repliedTo = await db.Messages
            .Where(m => m.SourceId == sourceId && m.TelegramMessageId == replyTo)
            .Select(m => new { m.Id, m.FollowUpOfMessageId })
            .SingleOrDefaultAsync(ct);
        if (repliedTo is null)
        {
            return null;
        }

        var originalId = repliedTo.FollowUpOfMessageId ?? repliedTo.Id;
        var original = await db.Messages.Where(m => m.Id == originalId).Select(m => new { m.PublishedAtUtc }).SingleOrDefaultAsync(ct);
        if (original is null || message.PublishedAtUtc - original.PublishedAtUtc > TimeSpan.FromMinutes(monitoring.Value.FollowUpWindowMinutes))
        {
            return null;
        }

        var alerted = await db.Notifications.AnyAsync(
            n => n.MessageId == originalId && (n.Kind == NotificationKind.New || n.Kind == NotificationKind.EditNewMatch) && n.Status == NotificationStatus.Sent,
            ct);
        if (!alerted)
        {
            return null;
        }

        var notified = await db.Notifications.CountAsync(n => n.Kind == NotificationKind.FollowUp && n.Message!.FollowUpOfMessageId == originalId, ct);
        return new FollowUpTarget(originalId, OverLimit: notified >= monitoring.Value.FollowUpMaxPerMessage);
    }

    private sealed record FollowUpTarget(long OriginalMessageId, bool OverLimit);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source} ({ChatId}): message {MessageId} is a follow-up of message row {OriginalId}, {Outcome}")]
    private static partial void LogFollowUp(ILogger logger, string source, long chatId, int messageId, long originalId, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source} ({ChatId}): message {MessageId} is already stored; skipped.")]
    private static partial void LogAlreadyProcessed(ILogger logger, string source, long chatId, int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing message {MessageId} from {ChatId} failed; continuing with the next message.")]
    private static partial void LogFailed(ILogger logger, Exception exception, long chatId, int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the ProcessingError service event failed.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
