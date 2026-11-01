using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Skywatch.Core.Formatting;
using Skywatch.Core.Time;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;

namespace Skywatch.Worker.Pipeline;

/// <summary>
/// AC-F11.2: the figures for one report period, on a new context. Messages count by <c>ReceivedAtUtc</c>,
/// match notifications (not edit replies) by <c>CreatedAtUtc</c>, service events by <c>OccurredAtUtc</c>.
/// </summary>
internal sealed class DailyReportBuilder(IDbContextFactory<SkywatchDbContext> dbFactory, IOptions<MonitoringOptions> monitoring)
{
    private static readonly ServiceEventKind[] _connectionKinds =
        [ServiceEventKind.Disconnected, ServiceEventKind.Reconnected, ServiceEventKind.Started, ServiceEventKind.Stopped];

    public async Task<DailyReport> BuildAsync(ReportPeriod period, CancellationToken ct)
    {
        var (start, end) = (period.StartUtc, period.EndUtc);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ReceivedAtUtc >= start && m.ReceivedAtUtc < end)
            .Select(m => new { m.IsMatched, m.MatchedPatterns, m.IsDuplicate, m.DuplicateReason, m.PublishedAtUtc, m.ReceivedAtUtc })
            .ToListAsync(ct);
        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.CreatedAtUtc >= start && n.CreatedAtUtc < end && n.Kind != NotificationKind.EditReply && n.Kind != NotificationKind.FollowUp) // AC-F13.7
            .Select(n => new { n.Kind, n.Status, n.Label, n.SentAtUtc, n.Message!.ReceivedAtUtc, n.Message.MatchedPatterns })
            .ToListAsync(ct);

        var matched = messages.Where(m => m.IsMatched).ToList();
        var sent = notifications.Where(n => n.Status == NotificationStatus.Sent).ToList();
        var labeled = sent.Where(n => n.Label is not null).ToList();

        var matchesPerPattern = matched.SelectMany(m => Patterns(m.MatchedPatterns)).CountBy(p => p).ToDictionary();
        var labeledPerPattern = labeled
            .SelectMany(n => Patterns(n.MatchedPatterns).Select(p => (Pattern: p, Useful: n.Label == NotificationLabel.Useful)))
            .GroupBy(x => x.Pattern)
            .ToDictionary(g => g.Key, g => (Useful: g.Count(x => x.Useful), Labeled: g.Count()));

        // Configured patterns first (in config order, so a silent pattern shows 0), then names found only in the data.
        var configured = monitoring.Value.Patterns.Where(p => p.Enabled).Select(p => p.Name).ToList();
        var names = configured.Concat(matchesPerPattern.Keys.Concat(labeledPerPattern.Keys).Except(configured).Order(StringComparer.Ordinal)).Distinct();
        var patterns = names
            .Select(name => new PatternStats(
                name,
                matchesPerPattern.GetValueOrDefault(name),
                labeledPerPattern.GetValueOrDefault(name).Useful,
                labeledPerPattern.GetValueOrDefault(name).Labeled))
            .ToList();

        var (reconnects, disconnected) = await ConnectionAsync(db, start, end, ct);
        var processingErrors = await db.ServiceEvents.CountAsync(
            e => e.Kind == ServiceEventKind.ProcessingError && e.OccurredAtUtc >= start && e.OccurredAtUtc < end, ct);

        // An edit-triggered notification is sent long after the message was first received.
        var sendLatencies = sent.Where(n => n.Kind == NotificationKind.New).Select(n => n.SentAtUtc!.Value - n.ReceivedAtUtc);

        return new DailyReport(
            period,
            MessagesReceived: messages.Count,
            Matches: matched.Count,
            patterns,
            ForwardDuplicates: matched.Count(m => m.IsDuplicate && m.DuplicateReason == DuplicateReason.Forward),
            SameTextDuplicates: matched.Count(m => m.IsDuplicate && m.DuplicateReason == DuplicateReason.SameText),
            UniqueMatches: matched.Count(m => !m.IsDuplicate),
            Sent: sent.Count,
            SkippedStale: notifications.Count(n => n.Status == NotificationStatus.SkippedStale),
            Failed: notifications.Count(n => n.Status == NotificationStatus.Failed),
            LabeledUseful: labeled.Count(n => n.Label == NotificationLabel.Useful),
            LabeledFalse: labeled.Count(n => n.Label == NotificationLabel.False),
            LabeledDuplicate: labeled.Count(n => n.Label == NotificationLabel.Duplicate),
            Unlabeled: sent.Count - labeled.Count,
            ReceiveLatency: LatencyStats.From(messages.Select(m => m.ReceivedAtUtc - m.PublishedAtUtc)),
            SendLatency: LatencyStats.From(sendLatencies),
            reconnects,
            disconnected,
            processingErrors);
    }

    private static string[] Patterns(string? matchedPatterns) =>
        matchedPatterns?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>
    /// A disconnect lasts until the next reconnect, or until the service stops or starts again (it was not
    /// listening then). A disconnect open at the period start counts from the start, one still open at the end
    /// counts to the end.
    /// </summary>
    private static async Task<(int Reconnects, TimeSpan Disconnected)> ConnectionAsync(SkywatchDbContext db, DateTime start, DateTime end, CancellationToken ct)
    {
        var before = await db.ServiceEvents.AsNoTracking()
            .Where(e => _connectionKinds.Contains(e.Kind) && e.OccurredAtUtc < start)
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Select(e => (ServiceEventKind?)e.Kind)
            .FirstOrDefaultAsync(ct);
        var events = await db.ServiceEvents.AsNoTracking()
            .Where(e => _connectionKinds.Contains(e.Kind) && e.OccurredAtUtc >= start && e.OccurredAtUtc < end)
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id)
            .Select(e => new { e.Kind, e.OccurredAtUtc })
            .ToListAsync(ct);

        DateTime? since = before == ServiceEventKind.Disconnected ? start : null;
        var reconnects = 0;
        var total = TimeSpan.Zero;
        foreach (var e in events)
        {
            if (e.Kind == ServiceEventKind.Disconnected)
            {
                since ??= e.OccurredAtUtc;
                continue;
            }

            if (e.Kind == ServiceEventKind.Reconnected)
            {
                reconnects++;
            }

            if (since is { } from)
            {
                total += e.OccurredAtUtc - from;
                since = null;
            }
        }

        if (since is { } open)
        {
            total += end - open;
        }

        return (reconnects, total);
    }
}
