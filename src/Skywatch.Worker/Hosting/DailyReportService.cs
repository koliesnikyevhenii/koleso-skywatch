using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Formatting;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// Spec F11: at <c>Reporting:DailyReportLocalTime</c> the report for the previous local day. Not an outbox row: the
/// report is sent first (low priority) and a <c>DailyReportSent</c> service event naming the day is written only on
/// success; a failed send is retried with backoff. On start, the latest due day is reported if it was not yet
/// (AC-F11.4), so a restart never reports a day twice and a missed report time is caught up once. Days that
/// ended before the service ever started (no earlier <c>Started</c> event) are not reported.
/// </summary>
internal sealed partial class DailyReportService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    DailyReportBuilder builder,
    INotificationSender? sender,
    IOptions<ReportingOptions> reporting,
    TimeZoneInfo displayZone,
    TimeProvider time,
    ILogger<DailyReportService> logger) : BackgroundService
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = FirstRetry;
        while (true)
        {
            bool done;
            try
            {
                done = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRunFailed(logger, ex);
                done = false;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var untilNext = ReportPeriod.NextReportTimeUtc(now, reporting.Value.ReportTime, displayZone) - now;
            TimeSpan wait;
            if (done)
            {
                retry = FirstRetry;
                wait = untilNext;
            }
            else
            {
                wait = retry < untilNext ? retry : untilNext;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }

            try
            {
                await Task.Delay(wait, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Reports the latest due day unless it is reported already. False when the send failed.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var period = ReportPeriod.LatestDue(time.GetUtcNow().UtcDateTime, reporting.Value.ReportTime, displayZone);
        var day = period.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.ServiceEvents.AnyAsync(e => e.Kind == ServiceEventKind.DailyReportSent && e.Detail == day, ct))
            {
                return true;
            }

            // A day before the very first start has nothing to report (and no report was missed).
            if (!await db.ServiceEvents.AnyAsync(e => e.Kind == ServiceEventKind.Started && e.OccurredAtUtc < period.EndUtc, ct))
            {
                LogNotRunning(logger, day);
                return true;
            }
        }

        var text = DailyReportFormatter.Format(await builder.BuildAsync(period, ct));
        if (sender is null)
        {
            LogReport(logger, day, text); // AC-F11.3
        }
        else
        {
            var result = await sender.SendAsync(new OutgoingNotification(text, SendPriority.Background), ct);
            if (result is not SendResult.Success)
            {
                LogSendFailed(logger, day, result);
                return false;
            }

            LogSent(logger, day);
        }

        // Not cancellable: a report that went out must be recorded, or a restart would send it again.
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.DailyReportSent, OccurredAtUtc = time.GetUtcNow().UtcDateTime, Detail = day });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily report for {Day} (notifications are disabled):\n{Report}")]
    private static partial void LogReport(ILogger logger, string day, string report);

    [LoggerMessage(Level = LogLevel.Information, Message = "No daily report for {Day}: the service had not started before that day ended.")]
    private static partial void LogNotRunning(ILogger logger, string day);

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily report for {Day} sent.")]
    private static partial void LogSent(ILogger logger, string day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Daily report for {Day} was not sent ({Result}); retrying with backoff.")]
    private static partial void LogSendFailed(ILogger logger, string day, SendResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Building or recording the daily report failed; retrying with backoff.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
