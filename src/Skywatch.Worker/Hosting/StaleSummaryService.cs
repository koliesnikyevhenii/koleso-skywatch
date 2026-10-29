using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// AC-F7.4: every 30 s, one summary of the <c>SkippedStale</c> notifications not yet reported. Not an outbox row:
/// the summary is sent first and the rows are marked only on success, so a failed send is retried on the next tick.
/// The summary itself is never stale-checked.
/// </summary>
internal sealed partial class StaleSummaryService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    INotificationSender sender,
    TimeZoneInfo displayZone,
    TimeProvider time,
    ILogger<StaleSummaryService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<(long Id, DateTime PublishedAtUtc)> stale;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            stale = (await db.Notifications
                .Where(n => n.Status == NotificationStatus.SkippedStale && n.StaleReportedAtUtc == null)
                .Select(n => new { n.Id, n.Message!.PublishedAtUtc })
                .ToListAsync(ct))
                .ConvertAll(n => (n.Id, n.PublishedAtUtc));
        }

        if (stale.Count == 0)
        {
            return;
        }

        var text = Format(stale.Count, stale.Min(n => n.PublishedAtUtc), stale.Max(n => n.PublishedAtUtc), displayZone);
        var result = await sender.SendAsync(new OutgoingNotification(text, SendPriority.Background), ct);
        if (result is not SendResult.Success)
        {
            LogSendFailed(logger, stale.Count, result);
            return;
        }

        // Exactly the rows counted: a row that became stale after the read is reported in the next summary.
        var ids = stale.ConvertAll(n => n.Id);
        var now = time.GetUtcNow().UtcDateTime;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.Notifications
                .Where(n => ids.Contains(n.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.StaleReportedAtUtc, now), CancellationToken.None);
        }

        LogSent(logger, stale.Count);
    }

    /// <summary>AC-F7.4: <c>⚠️ Пропущено {N} застарілих збігів (публікації {HH:mm}–{HH:mm})</c> in the display zone.</summary>
    internal static string Format(int count, DateTime firstPublishedUtc, DateTime lastPublishedUtc, TimeZoneInfo zone)
    {
        var first = TimeZoneInfo.ConvertTimeFromUtc(firstPublishedUtc, zone);
        var last = TimeZoneInfo.ConvertTimeFromUtc(lastPublishedUtc, zone);
        return string.Create(CultureInfo.InvariantCulture, $"⚠️ Пропущено {count} застарілих збігів (публікації {first:HH:mm}–{last:HH:mm})");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stale summary sent for {Count} notifications.")]
    private static partial void LogSent(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale summary for {Count} notifications was not sent ({Result}); retrying on the next tick.")]
    private static partial void LogSendFailed(ILogger logger, int count, SendResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stale summary tick failed; retrying on the next tick.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
