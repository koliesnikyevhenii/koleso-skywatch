using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Formatting;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// The dispatcher's wake-up signal (capacity 1). It carries no notification ids: the outbox in the database is the
/// only queue, so a signal sent while the dispatcher is busy just makes it look again.
/// </summary>
internal sealed class DispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Notify() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

/// <summary>
/// Spec F8: the single consumer of the outbox. At startup and on every signal it sends <c>Pending</c> rows one by
/// one in creation order (AC-F8.1, AC-F8.11). Each attempt re-reads the row on a new context, skips it when it is
/// no longer <c>Pending</c>, and rechecks staleness before sending (AC-F7.1).
/// </summary>
internal sealed partial class NotificationDispatchService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    INotificationSender sender,
    DispatchSignal signal,
    IOptions<NotificationOptions> options,
    TimeZoneInfo displayZone,
    TimeProvider time,
    ILogger<NotificationDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan _firstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(4);

    private readonly StalenessPolicy _staleness = new(TimeSpan.FromSeconds(options.Value.MaxMessageAgeSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LogRecoveryAsync(stoppingToken);
            while (true)
            {
                await DrainAsync(stoppingToken);
                await signal.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: rows still Pending are recovered at the next start (AC-F8.11).
        }
    }

    private async Task LogRecoveryAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.Notifications.CountAsync(n => n.Status == NotificationStatus.Pending, ct);
        if (pending > 0)
        {
            LogRecovering(logger, pending);
        }
    }

    /// <summary>Every row that is <c>Pending</c> now, in id order. Rows added meanwhile are picked up in the same pass.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        long after = 0;
        while (await NextPendingAsync(after, ct) is { } id)
        {
            after = id;
            try
            {
                await DeliverAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row stays Pending and is tried again on the next signal or start.
                LogDeliveryFailed(logger, ex, id);
            }
        }
    }

    private async Task<long?> NextPendingAsync(long after, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notifications
            .Where(n => n.Status == NotificationStatus.Pending && n.Id > after)
            .OrderBy(n => n.Id)
            .Select(n => (long?)n.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task DeliverAsync(long id, CancellationToken ct)
    {
        while (await AttemptAsync(id, ct) is { } wait)
        {
            await Task.Delay(wait, time, ct);
        }
    }

    /// <summary>One send attempt on its own context.</summary>
    /// <returns>How long to wait before the next attempt, or <see langword="null"/> when the row is done.</returns>
    private async Task<TimeSpan?> AttemptAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var notification = await db.Notifications
            .Include(n => n.Message!).ThenInclude(m => m.Source)
            .SingleOrDefaultAsync(n => n.Id == id, ct);
        if (notification is not { Status: NotificationStatus.Pending })
        {
            return null; // AC-F8.11: changed since it was read
        }

        var message = notification.Message!;
        var editedAt = notification.Kind == NotificationKind.New ? null : message.EditedAtUtc;
        var now = time.GetUtcNow().UtcDateTime;
        if (_staleness.IsStale(editedAt ?? message.PublishedAtUtc, now))
        {
            notification.Status = NotificationStatus.SkippedStale;
            await db.SaveChangesAsync(CancellationToken.None);
            LogSkippedStale(logger, id, message.Id);
            return null;
        }

        var content = new NotificationContent(
            message.MatchedPatterns?.Split(',') ?? [],
            message.Text ?? "",
            message.Source!.Name,
            message.PublishedAtUtc,
            editedAt,
            SourceLinkBuilder.Build(message.Source.TelegramChatId, message.Source.Username, message.TelegramMessageId));
        var text = NotificationFormatter.Format(content, now, displayZone);

        var result = await sender.SendAsync(new OutgoingNotification(text, SendPriority.Alert), ct);
        var wait = Apply(notification, result, now);

        // Not cancellable: a message that went out must be recorded as sent, or a restart would send it again.
        await db.SaveChangesAsync(CancellationToken.None);
        return wait;
    }

    private TimeSpan? Apply(Notification notification, SendResult result, DateTime attemptAtUtc)
    {
        switch (result)
        {
            case SendResult.Success success: // AC-F8.10
                notification.Attempts++;
                notification.Status = NotificationStatus.Sent;
                notification.SentAtUtc = time.GetUtcNow().UtcDateTime;
                notification.BotMessageId = success.BotMessageId;
                LogSent(logger, notification.Id, notification.MessageId, notification.Attempts, (notification.SentAtUtc.Value - attemptAtUtc).TotalSeconds);
                return null;

            case SendResult.RateLimited limited: // AC-F8.6: not an attempt
                LogRateLimited(logger, notification.Id, limited.RetryAfter.TotalSeconds);
                return limited.RetryAfter;

            case SendResult.Transient transient: // AC-F8.7, AC-F8.9
                notification.Attempts++;
                notification.Error = transient.Error;
                notification.PossibleDuplicate |= transient.DeliveryUnknown;
                if (notification.Attempts >= options.Value.MaxSendAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                    LogFailed(logger, notification.Id, notification.Attempts, transient.Error);
                    return null;
                }

                var backoff = Backoff(notification.Attempts);
                LogRetrying(logger, notification.Id, notification.Attempts, transient.Error, transient.DeliveryUnknown, backoff.TotalSeconds);
                return backoff;

            case SendResult.Fatal fatal: // AC-F8.8
                notification.Attempts++;
                notification.Error = fatal.Error;
                notification.Status = NotificationStatus.Failed;
                LogFatal(logger, notification.Id, fatal.Error);
                return null;

            default:
                throw new InvalidOperationException($"Unknown send result {result}.");
        }
    }

    /// <summary>AC-F8.7: 1 s, 2 s, 4 s, and 4 s for any further attempts.</summary>
    private static TimeSpan Backoff(int failedAttempts) =>
        TimeSpan.FromTicks(Math.Min(_firstBackoff.Ticks << Math.Min(failedAttempts - 1, 30), _maxBackoff.Ticks));

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovering {Count} pending notifications.")]
    private static partial void LogRecovering(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} (message {MessageId}) sent on attempt {Attempts}, send took {SendSeconds:0.0} s.")]
    private static partial void LogSent(ILogger logger, long notificationId, long messageId, int attempts, double sendSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} (message {MessageId}) is stale; skipped.")]
    private static partial void LogSkippedStale(ILogger logger, long notificationId, long messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId}: rate limited, retrying in {RetryAfterSeconds} s.")]
    private static partial void LogRateLimited(ILogger logger, long notificationId, double retryAfterSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId}: attempt {Attempts} failed ({Error}, delivery unknown: {DeliveryUnknown}); retrying in {BackoffSeconds} s.")]
    private static partial void LogRetrying(ILogger logger, long notificationId, int attempts, string error, bool deliveryUnknown, double backoffSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {NotificationId} failed after {Attempts} attempts: {Error}")]
    private static partial void LogFailed(ILogger logger, long notificationId, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Notification {NotificationId} was rejected by Telegram and will not be retried: {Error}. Check the bot token, the target chat and the bot's rights there.")]
    private static partial void LogFatal(ILogger logger, long notificationId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering notification {NotificationId} failed; it stays pending.")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, long notificationId);
}
