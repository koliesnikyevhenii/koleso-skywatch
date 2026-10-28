using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;

namespace Skywatch.Worker.Hosting;

/// <summary>
/// Spec F12: a pass at startup, then hourly. Every statement touches at most <c>RetentionBatchSize</c> rows and
/// runs in its own short transaction on a new context, so other writers wait at most one batch.
/// </summary>
internal sealed partial class RetentionService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The next pass picks up whatever this one left.
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One retention pass over all three rules. Age is measured from <c>ReceivedAtUtc</c> / <c>OccurredAtUtc</c> (AC-F12.4).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var options = storage.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var metadataCutoff = now.AddDays(-options.MetadataRetentionDays);
        var matchedTextCutoff = now.AddDays(-options.RetentionDays);
        var unmatchedTextCutoff = now.AddDays(-options.UnmatchedRetentionDays);

        // AC-F12.3. Notifications go with their message (ON DELETE CASCADE); a younger duplicate keeps
        // IsDuplicate and loses only its reference (ON DELETE SET NULL).
        var messages = await InBatchesAsync(
            (db, n) => db.Messages.Where(m => m.ReceivedAtUtc < metadataCutoff).OrderBy(m => m.Id).Take(n).ExecuteDeleteAsync(ct),
            ct);
        var events = await InBatchesAsync(
            (db, n) => db.ServiceEvents.Where(e => e.OccurredAtUtc < metadataCutoff).OrderBy(e => e.Id).Take(n).ExecuteDeleteAsync(ct),
            ct);

        // AC-F12.2 and AC-F12.1. Only rows that still have text, so the batches end.
        var matchedTexts = await InBatchesAsync((db, n) => ClearTextAsync(db, matched: true, matchedTextCutoff, n, ct), ct);
        var unmatchedTexts = await InBatchesAsync((db, n) => ClearTextAsync(db, matched: false, unmatchedTextCutoff, n, ct), ct);

        if (messages + events + matchedTexts + unmatchedTexts > 0)
        {
            LogPass(logger, messages, events, matchedTexts, unmatchedTexts);
        }
    }

    private static Task<int> ClearTextAsync(SkywatchDbContext db, bool matched, DateTime cutoff, int batchSize, CancellationToken ct) =>
        db.Messages
            .Where(m => m.IsMatched == matched && m.Text != null && m.ReceivedAtUtc < cutoff)
            .OrderBy(m => m.Id)
            .Take(batchSize)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Text, (string?)null), ct);

    /// <summary>Runs <paramref name="batch"/> on a new context until it affects fewer rows than the batch size.</summary>
    private async Task<int> InBatchesAsync(Func<SkywatchDbContext, int, Task<int>> batch, CancellationToken ct)
    {
        var batchSize = storage.Value.RetentionBatchSize;
        var total = 0;
        int affected;
        do
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            affected = await batch(db, batchSize);
            total += affected;
        }
        while (affected == batchSize);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: deleted {Messages} messages and {Events} service events; cleared text of {MatchedTexts} matched and {UnmatchedTexts} unmatched messages.")]
    private static partial void LogPass(ILogger logger, int messages, int events, int matchedTexts, int unmatchedTexts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention pass failed; retrying at the next pass.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
