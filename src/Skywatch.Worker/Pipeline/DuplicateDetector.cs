using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Skywatch.Core.Deduplication;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;

namespace Skywatch.Worker.Pipeline;

internal sealed record DuplicateMatch(long OriginalMessageId, DuplicateReason Reason);

/// <summary>
/// AC-F6.4: finds the earlier matched, non-duplicate message a new match duplicates. Only originals are candidates,
/// so a duplicate of a duplicate points to the first non-duplicate. The origin key is checked first.
/// </summary>
internal sealed class DuplicateDetector(IOptions<MonitoringOptions> monitoring)
{
    public async Task<DuplicateMatch?> FindAsync(
        SkywatchDbContext db,
        int sourceId,
        OriginKey origin,
        string contentHash,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        var options = monitoring.Value;
        var originals = db.Messages.Where(m => m.IsMatched && !m.IsDuplicate);

        var forwardFrom = receivedAtUtc.AddMinutes(-options.DuplicateWindowMinutes);
        var forward = await originals
            .Where(m => m.OriginChatId == origin.ChatId && m.OriginMessageId == origin.MessageId && m.ReceivedAtUtc >= forwardFrom)
            .OrderBy(m => m.Id)
            .Select(m => (long?)m.Id)
            .FirstOrDefaultAsync(ct);
        if (forward is { } forwardOf)
        {
            return new DuplicateMatch(forwardOf, DuplicateReason.Forward);
        }

        var sameTextFrom = receivedAtUtc.AddMinutes(-options.ContentDuplicateWindowMinutes);
        var sameText = originals.Where(m => m.ContentHash == contentHash && m.ReceivedAtUtc >= sameTextFrom);
        if (options.ContentDuplicateAcrossSourcesOnly)
        {
            sameText = sameText.Where(m => m.SourceId != sourceId);
        }

        var sameTextOf = await sameText.OrderBy(m => m.Id).Select(m => (long?)m.Id).FirstOrDefaultAsync(ct);
        return sameTextOf is { } id ? new DuplicateMatch(id, DuplicateReason.SameText) : null;
    }
}
