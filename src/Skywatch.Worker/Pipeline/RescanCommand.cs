using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;

namespace Skywatch.Worker.Pipeline;

/// <summary>
/// `rescan [--pattern "&lt;regex&gt;"]` (AC-F1.6): runs the configured patterns, or one regex, over stored unmatched
/// messages that still have text, the same way the live pipeline does (normalized text). Read-only: the database
/// is opened read-only, no migration runs, nothing is sent.
/// </summary>
internal sealed class RescanCommand(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    IOptions<MonitoringOptions> monitoring,
    TimeZoneInfo displayZone)
{
    public const string OneOffPatternName = "--pattern";
    private const int _snippetLength = 120;

    /// <summary>The one-off regex is matched case-insensitively, like most configured patterns.</summary>
    public async Task<int> RunAsync(string? pattern, TextWriter output, CancellationToken ct)
    {
        var filter = pattern is null
            ? new KeywordFilter(monitoring.Value.Patterns.Select(p => p.ToDefinition()))
            : new KeywordFilter([new PatternDefinition(OneOffPatternName, pattern, CaseSensitive: false)]);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = db.Messages.AsNoTracking()
            .Where(m => !m.IsMatched && m.Text != null)
            .OrderBy(m => m.ReceivedAtUtc).ThenBy(m => m.Id)
            .Select(m => new { m.PublishedAtUtc, Source = m.Source!.Name, m.TelegramMessageId, Text = m.Text! })
            .AsAsyncEnumerable();

        await output.WriteLineAsync("Published\tSource\tMessageId\tPattern\tSnippet".AsMemory(), ct);
        var scanned = 0;
        var hits = 0;
        await foreach (var message in candidates.WithCancellation(ct))
        {
            scanned++;
            var result = filter.Match(TextNormalizer.Normalize(message.Text));
            if (!result.IsMatch)
            {
                continue;
            }

            hits++;
            var published = TimeZoneInfo.ConvertTimeFromUtc(message.PublishedAtUtc, displayZone);
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{published:yyyy-MM-dd HH:mm:ss}\t{message.Source}\t{message.TelegramMessageId}\t{string.Join(", ", result.Hits.Select(h => h.PatternName))}\t{Snippet(message.Text)}");
            await output.WriteLineAsync(line.AsMemory(), ct);
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{hits} of {scanned} unmatched messages with text match.").AsMemory(), ct);
        return 0;
    }

    /// <summary>One line: whitespace runs collapsed, cut after <see cref="_snippetLength"/> characters.</summary>
    internal static string Snippet(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= _snippetLength)
        {
            return line;
        }

        var keep = char.IsHighSurrogate(line[_snippetLength - 1]) ? _snippetLength - 1 : _snippetLength;
        return string.Concat(line.AsSpan(0, keep), "…");
    }
}
