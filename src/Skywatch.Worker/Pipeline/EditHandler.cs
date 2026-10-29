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

internal enum EditOutcome
{
    /// <summary><c>EditHandling = Ignore</c> (AC-F9.1): nothing was written.</summary>
    Ignored,

    /// <summary>The content hash equals the stored one (AC-F9.6): nothing was written.</summary>
    Unchanged,

    /// <summary>The stored message was updated (AC-F9.2) and nothing is to be sent.</summary>
    Updated,

    /// <summary>A new match with notifications disabled: stored and logged only (AC-F6.7).</summary>
    MatchLogged,
    Pending,
    SkippedDuplicate,
    SkippedStale,

    /// <summary><c>ReplyOnChange</c>: a reply to the sent notification is scheduled after the debounce (AC-F9.5).</summary>
    ReplyScheduled,

    /// <summary>An exception was logged and recorded as a <c>ProcessingError</c> service event (AC-F6.8).</summary>
    Failed,
}

/// <summary>
/// Spec F9 for one edit event, on a new context. The stored message takes the new hash, match result and text
/// (AC-F9.2); a message that newly matches is notified once with the edit marker, subject to duplicate detection
/// and staleness from the edit time (AC-F9.3, AC-F7.3).
/// </summary>
internal sealed partial class EditHandler(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    KeywordFilter filter,
    DuplicateDetector duplicates,
    DispatchSignal dispatch,
    EditReplyService replies,
    IOptions<MonitoringOptions> monitoring,
    IOptions<StorageOptions> storage,
    IOptions<NotificationOptions> notifications,
    TimeProvider time,
    ILogger<EditHandler> logger)
{
    private readonly StalenessPolicy _staleness = new(TimeSpan.FromSeconds(notifications.Value.MaxMessageAgeSeconds));

    public async Task<EditOutcome> HandleAsync(IncomingMessage edit, CancellationToken ct)
    {
        if (monitoring.Value.EditHandling == EditHandling.Ignore)
        {
            return EditOutcome.Ignored;
        }

        try
        {
            return await HandleCoreAsync(edit, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AC-F6.8: never the text, and the pipeline continues with the next event.
            LogFailed(logger, ex, edit.ChatId, edit.MessageId);
            await RecordErrorAsync(edit, ex, ct);
            return EditOutcome.Failed;
        }
    }

    private async Task<EditOutcome> HandleCoreAsync(IncomingMessage edit, CancellationToken ct)
    {
        var normalized = TextNormalizer.Normalize(edit.Text);
        var hash = ContentHasher.Hash(normalized);
        var editedAt = edit.EditedAtUtc ?? edit.ReceivedAtUtc;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await db.Sources.SingleOrDefaultAsync(s => s.TelegramChatId == edit.ChatId, ct)
            ?? throw new InvalidOperationException($"Chat {edit.ChatId} is not a stored source.");
        var row = await db.Messages.SingleOrDefaultAsync(m => m.SourceId == source.Id && m.TelegramMessageId == edit.MessageId, ct);
        if (row is not null && row.ContentHash == hash)
        {
            return EditOutcome.Unchanged; // AC-F9.6
        }

        if (row is null)
        {
            // Unknown message (sent before monitoring started, or lost): stored like a new one (AC-F6.1).
            row = new Message
            {
                SourceId = source.Id,
                TelegramMessageId = edit.MessageId,
                PublishedAtUtc = edit.PublishedAtUtc,
                ReceivedAtUtc = edit.ReceivedAtUtc,
                OriginChatId = edit.Origin.ChatId,
                OriginMessageId = edit.Origin.MessageId,
            };
            db.Messages.Add(row);
        }

        var wasMatched = row.IsMatched;
        var result = filter.Match(normalized);
        row.EditedAtUtc = editedAt;
        row.ContentHash = hash;
        row.IsMatched = result.IsMatch;
        row.MatchedPatterns = result.IsMatch ? string.Join(',', result.Hits.Select(h => h.PatternName)) : null;
        row.Text = result.IsMatch || storage.Value.StoreUnmatchedText ? edit.Text : null; // AC-F9.2 with AC-F6.1

        var alert = row.Id == 0
            ? null
            : await db.Notifications.Where(n => n.MessageId == row.Id && n.Kind != NotificationKind.EditReply).OrderBy(n => n.Id).FirstOrDefaultAsync(ct);

        var outcome = EditOutcome.Updated;
        if (result.IsMatch && !wasMatched && alert is null)
        {
            // AC-F9.3. The row itself is still unmatched in the database, so it is never its own original.
            if (await duplicates.FindAsync(db, source.Id, edit.Origin, hash, edit.ReceivedAtUtc, ct) is { } duplicate)
            {
                row.IsDuplicate = true;
                row.DuplicateOfMessageId = duplicate.OriginalMessageId;
                row.DuplicateReason = duplicate.Reason;
            }

            outcome = Decide(row, editedAt);
            if (notifications.Value.Enabled)
            {
                db.Notifications.Add(new Notification { Message = row, Kind = NotificationKind.EditNewMatch, Status = ToStatus(outcome), CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            }
        }

        await db.SaveChangesAsync(ct);

        if (outcome == EditOutcome.Pending)
        {
            dispatch.Notify();
        }
        else if (outcome == EditOutcome.Updated && monitoring.Value.EditHandling == EditHandling.ReplyOnChange && alert is { Status: NotificationStatus.Sent })
        {
            // AC-F9.5: a Pending alert is sent with the latest text anyway; only a sent one gets a reply.
            if (alert.BotMessageId is null)
            {
                LogNoBotMessageId(logger, source.Name, edit.ChatId, edit.MessageId, alert.Id);
            }
            else
            {
                replies.Schedule(row.Id);
                outcome = EditOutcome.ReplyScheduled;
            }
        }

        LogEdit(logger, source.Name, edit.ChatId, edit.MessageId, result.IsMatch ? string.Join(", ", result.Hits.Select(h => h.PatternName)) : "-", outcome);
        return outcome;
    }

    /// <summary>AC-F9.3 with F6.4 and F7.3: age from the edit time; a duplicate is reported as such even when stale.</summary>
    private EditOutcome Decide(Message row, DateTime editedAtUtc) =>
        row.IsDuplicate ? EditOutcome.SkippedDuplicate
        : !notifications.Value.Enabled ? EditOutcome.MatchLogged
        : _staleness.IsStale(editedAtUtc, time.GetUtcNow().UtcDateTime) ? EditOutcome.SkippedStale
        : EditOutcome.Pending;

    private static NotificationStatus ToStatus(EditOutcome outcome) => outcome switch
    {
        EditOutcome.SkippedDuplicate => NotificationStatus.SkippedDuplicate,
        EditOutcome.SkippedStale => NotificationStatus.SkippedStale,
        _ => NotificationStatus.Pending,
    };

    private async Task RecordErrorAsync(IncomingMessage edit, Exception exception, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ServiceEvents.Add(new ServiceEvent
            {
                Kind = ServiceEventKind.ProcessingError,
                OccurredAtUtc = time.GetUtcNow().UtcDateTime,
                Detail = $"chat {edit.ChatId}, edit of message {edit.MessageId}: {exception.GetType().Name}: {exception.Message}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source} ({ChatId}): edit of message {MessageId}, matches {Patterns}, {Outcome}")]
    private static partial void LogEdit(ILogger logger, string source, long chatId, int messageId, string patterns, EditOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} ({ChatId}): message {MessageId} changed, but its sent notification {NotificationId} has no bot message id; no reply.")]
    private static partial void LogNoBotMessageId(ILogger logger, string source, long chatId, int messageId, long notificationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling the edit of message {MessageId} from {ChatId} failed; continuing with the next event.")]
    private static partial void LogFailed(ILogger logger, Exception exception, long chatId, int messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the ProcessingError service event failed.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
