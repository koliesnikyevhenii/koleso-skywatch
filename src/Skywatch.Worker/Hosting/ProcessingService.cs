using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// The single consumer of the update source: resolve and upsert sources, read events, save the update state, and
/// record lifecycle and connection changes as service events (AC-F3.7). New messages go to
/// <see cref="MessageProcessor"/>; edits are logged and ignored until P5.
/// </summary>
internal sealed partial class ProcessingService(
    ITelegramUpdateSource source,
    MessageProcessor processor,
    IDbContextFactory<SkywatchDbContext> dbFactory,
    IOptions<TelegramOptions> telegram,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<ProcessingService> logger) : BackgroundService
{
    private static readonly TimeSpan _clockSkewCheckInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentQueue<(DateTime PublishedUtc, DateTime ReceivedUtc)> _latencies = new();
    private Dictionary<long, string> _sourceNames = [];

    /// <summary>Set when the service stopped the host because it cannot run (AC-F1.3); the run mode exits non-zero.</summary>
    public bool Failed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var clockSkewTimer = time.CreateTimer(_ => CheckClockSkew(), null, _clockSkewCheckInterval, _clockSkewCheckInterval);

        IReadOnlyList<ResolvedSource> sources;
        try
        {
            sources = await source.ResolveSourcesAsync(stoppingToken);
        }
        catch (TelegramSessionRejectedException ex)
        {
            LogSessionRejected(logger, ex.Message);
            Fail();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorType = ex.GetType().Name;
            LogStartFailed(logger, errorType, ex.Message);
            Fail();
            return;
        }

        LogSources(sources);

        // Not cancellable: a stop during startup must still reach the loop below, which drains the channel (N4).
        await UpsertSourcesAsync(sources, CancellationToken.None);
        await RecordAsync(ServiceEventKind.Started, time.GetUtcNow().UtcDateTime, CancellationToken.None);

        var saveInterval = TimeSpan.FromSeconds(telegram.Value.UpdateStateSaveIntervalSeconds);
        var lastSave = time.GetUtcNow();

        // On shutdown the source stops intake and still yields what is queued, so this loop drains the channel.
        await foreach (var sourceEvent in source.ReadAllAsync(stoppingToken))
        {
            await HandleAsync(sourceEvent);

            // Single consumer: once the event above is handled, nothing is in flight.
            if (time.GetUtcNow() - lastSave >= saveInterval && await source.TrySaveStateAsync(CancellationToken.None))
            {
                lastSave = time.GetUtcNow();
            }
        }

        // Shutdown: the channel is drained, so the final save cannot skip an event. Not cancellable on purpose.
        await source.TrySaveStateAsync(CancellationToken.None);
        await RecordAsync(ServiceEventKind.Stopped, time.GetUtcNow().UtcDateTime, CancellationToken.None);
    }

    /// <summary>
    /// AC-F2.2: resolved sources are inserted or updated in place. Configuration is the source of truth for
    /// <c>IsEnabled</c>: a stored source is enabled exactly when it is among the enabled configured sources.
    /// </summary>
    private async Task UpsertSourcesAsync(IReadOnlyList<ResolvedSource> sources, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Sources.ToDictionaryAsync(s => s.TelegramChatId, ct);
        var enabled = sources.Select(s => s.ChatId).ToHashSet();
        foreach (var row in stored.Values)
        {
            row.IsEnabled = enabled.Contains(row.TelegramChatId);
        }

        foreach (var s in sources.Where(s => s.Resolved))
        {
            if (!stored.TryGetValue(s.ChatId, out var row))
            {
                row = new Source { TelegramChatId = s.ChatId, IsEnabled = true, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
                db.Sources.Add(row);
            }

            row.Name = s.Name;
            row.Type = s.Type;
            row.Username = s.Username;
            row.NoForwards = s.NoForwards;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RecordAsync(ServiceEventKind kind, DateTime occurredAtUtc, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ServiceEvents.Add(new ServiceEvent { Kind = kind, OccurredAtUtc = occurredAtUtc });
        await db.SaveChangesAsync(ct);
    }

    private void LogSources(IReadOnlyList<ResolvedSource> sources)
    {
        _sourceNames = sources.ToDictionary(s => s.ChatId, s => s.Name);
        foreach (var s in sources)
        {
            if (!s.Resolved)
            {
                LogSourceUnresolved(logger, s.Name, s.ChatId);
            }
            else if (s.NoForwards)
            {
                LogSourceNoForwards(logger, s.Name, s.ChatId);
            }
        }

        var resolved = sources.Count(s => s.Resolved);
        LogListening(logger, resolved);
    }

    private async Task HandleAsync(SourceEvent sourceEvent)
    {
        switch (sourceEvent)
        {
            case IncomingMessage m:
                if (!m.IsEdit)
                {
                    _latencies.Enqueue((m.PublishedAtUtc, m.ReceivedAtUtc));
                }

                // Never the text itself: only its length.
                var latency = (m.ReceivedAtUtc - m.PublishedAtUtc).TotalSeconds;
                var name = _sourceNames.GetValueOrDefault(m.ChatId, "?");
                LogMessage(logger, name, m.ChatId, m.MessageId, m.Text.Length, latency, m.IsEdit);

                // Not cancellable: messages read during shutdown are still processed while the channel drains (N4).
                if (!m.IsEdit)
                {
                    await processor.ProcessAsync(m, CancellationToken.None);
                }

                break;
            // Not cancellable: an event read during shutdown is still recorded while the channel drains.
            case ConnectionStateChanged { State: ConnectionState.Disconnected } c:
                LogDisconnected(logger, c.AtUtc);
                await RecordAsync(ServiceEventKind.Disconnected, c.AtUtc, CancellationToken.None);
                break;
            case ConnectionStateChanged c:
                LogReconnected(logger, c.AtUtc);
                await RecordAsync(ServiceEventKind.Reconnected, c.AtUtc, CancellationToken.None);
                break;
        }
    }

    /// <summary>AC-F3.8: hourly, over live (non-edit) messages received in the last hour. No Telegram requests.</summary>
    private void CheckClockSkew()
    {
        var cutoff = time.GetUtcNow().UtcDateTime - _clockSkewCheckInterval;
        while (_latencies.TryPeek(out var oldest) && oldest.ReceivedUtc < cutoff)
        {
            _latencies.TryDequeue(out _);
        }

        var result = ClockSkewEstimator.Evaluate(_latencies.ToArray());
        switch (result.Status)
        {
            case ClockSkewStatus.Suspect:
                LogClockSkewSuspect(logger, result.MinimumLatency!.Value.TotalSeconds);
                break;
            case ClockSkewStatus.Normal:
                LogClockSkewNormal(logger, result.MinimumLatency!.Value.TotalSeconds);
                break;
            default:
                LogClockSkewNoData(logger);
                break;
        }
    }

    private void Fail()
    {
        Failed = true;
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Error} Run `login` again.")]
    private static partial void LogSessionRejected(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The Telegram listener could not start: {ErrorType}: {Error}")]
    private static partial void LogStartFailed(ILogger logger, string errorType, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source '{Name}' ({ChatId}) cannot be resolved by the reader account and is skipped.")]
    private static partial void LogSourceUnresolved(ILogger logger, string name, long chatId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source '{Name}' ({ChatId}) has noforwards set.")]
    private static partial void LogSourceNoForwards(ILogger logger, string name, long chatId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening to {Count} sources.")]
    private static partial void LogListening(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source} ({ChatId}): message {MessageId}, {Length} chars, latency {LatencySeconds:0.0} s, edit={IsEdit}")]
    private static partial void LogMessage(ILogger logger, string source, long chatId, int messageId, int length, double latencySeconds, bool isEdit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram disconnected at {AtUtc:O}.")]
    private static partial void LogDisconnected(ILogger logger, DateTime atUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram reconnected at {AtUtc:O}.")]
    private static partial void LogReconnected(ILogger logger, DateTime atUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Possible clock skew: minimum live latency in the last hour is {MinimumSeconds:0.0} s (expected −2…10 s). Check NTP.")]
    private static partial void LogClockSkewSuspect(ILogger logger, double minimumSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clock skew check: minimum live latency in the last hour is {MinimumSeconds:0.0} s.")]
    private static partial void LogClockSkewNormal(ILogger logger, double minimumSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clock skew check skipped: no live messages in the last hour.")]
    private static partial void LogClockSkewNoData(ILogger logger);
}
