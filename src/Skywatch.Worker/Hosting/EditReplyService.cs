using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// AC-F9.5: debounces edits per message. When a message has had no further edit for
/// <c>Monitoring:EditDebounceSeconds</c>, one <c>EditReply</c> outbox row is created and the dispatcher is woken;
/// the dispatcher sends it as a reply with the text stored at send time. The debounce is in memory: a restart
/// inside the window drops that reply (plan R21).
/// </summary>
internal sealed partial class EditReplyService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    DispatchSignal dispatch,
    IOptions<MonitoringOptions> monitoring,
    TimeProvider time,
    ILogger<EditReplyService> logger) : BackgroundService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, (ITimer Timer, int Version)> _timers = [];
    private readonly Channel<long> _due = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private int _version;

    /// <summary>Starts or restarts the debounce for <paramref name="messageId"/> (the <c>Messages.Id</c>).</summary>
    public void Schedule(long messageId)
    {
        lock (_lock)
        {
            if (_timers.Remove(messageId, out var previous))
            {
                previous.Timer.Dispose();
            }

            var version = ++_version;
            var timer = time.CreateTimer(_ => Due(messageId, version), null, TimeSpan.FromSeconds(monitoring.Value.EditDebounceSeconds), Timeout.InfiniteTimeSpan);
            _timers[messageId] = (timer, version);
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var (timer, _) in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var messageId in _due.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await CreateReplyAsync(messageId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCreateFailed(logger, ex, messageId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>A timer replaced by a later edit may still fire; only the current one counts.</summary>
    private void Due(long messageId, int version)
    {
        lock (_lock)
        {
            if (!_timers.TryGetValue(messageId, out var current) || current.Version != version)
            {
                return;
            }

            _timers.Remove(messageId);
            current.Timer.Dispose();
        }

        _due.Writer.TryWrite(messageId);
    }

    private async Task CreateReplyAsync(long messageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sent = await db.Notifications.AnyAsync(
            n => n.MessageId == messageId && n.Kind != NotificationKind.EditReply && n.Status == NotificationStatus.Sent && n.BotMessageId != null,
            ct);
        if (!sent)
        {
            return; // deleted by retention meanwhile
        }

        db.Notifications.Add(new Notification { MessageId = messageId, Kind = NotificationKind.EditReply, Status = NotificationStatus.Pending, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        dispatch.Notify();
        LogCreated(logger, messageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Edit reply for message {MessageId} queued.")]
    private static partial void LogCreated(ILogger logger, long messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queuing the edit reply for message {MessageId} failed; the reply is dropped.")]
    private static partial void LogCreateFailed(ILogger logger, Exception exception, long messageId);
}
