using System.Globalization;
using System.Text;
using Skywatch.Core.Time;

namespace Skywatch.Core.Formatting;

/// <summary>AC-F11.2: the figures of one daily report. Counts are for messages and notifications created in the period.</summary>
public sealed record DailyReport(
    ReportPeriod Period,
    int MessagesReceived,
    int Matches,
    IReadOnlyList<PatternStats> Patterns,
    int ForwardDuplicates,
    int SameTextDuplicates,
    int UniqueMatches,
    int Sent,
    int SkippedStale,
    int Failed,
    int LabeledUseful,
    int LabeledFalse,
    int LabeledDuplicate,
    int Unlabeled,
    LatencyStats ReceiveLatency,
    LatencyStats SendLatency,
    int Reconnects,
    TimeSpan Disconnected,
    int ProcessingErrors)
{
    public int Labeled => LabeledUseful + LabeledFalse + LabeledDuplicate;
}

/// <summary>
/// One pattern: matched messages, and the labeled notifications about them. A message that matched several
/// patterns counts toward each of them.
/// </summary>
public sealed record PatternStats(string Name, int Matches, int Useful, int Labeled);

/// <summary>Nearest-rank percentiles; <see langword="null"/> without samples.</summary>
public sealed record LatencyStats(int Count, TimeSpan? P50, TimeSpan? P95)
{
    public static LatencyStats From(IEnumerable<TimeSpan> samples)
    {
        var sorted = samples.Order().ToArray();
        return new LatencyStats(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    /// <summary>The smallest sample with at least <paramref name="p"/> of all samples at or below it.</summary>
    private static TimeSpan? Percentile(TimeSpan[] sorted, double p) =>
        sorted.Length == 0 ? null : sorted[Math.Max((int)Math.Ceiling(p * sorted.Length) - 1, 0)];
}

/// <summary>AC-F11.2: the plain-text daily report (no parse mode). Strings are Ukrainian.</summary>
public static class DailyReportFormatter
{
    public const string MultiPatternNote = "* Повідомлення, що збіглося з кількома шаблонами, враховано в кожному з них.";

    public static string Format(DailyReport report)
    {
        var text = new StringBuilder();
        Line(text, $"📊 Звіт за {report.Period.Day:dd.MM.yyyy} ({report.Period.Length.TotalHours:0} год)");
        Line(text, $"Повідомлень отримано: {report.MessagesReceived}");
        Line(text, $"Збігів: {report.Matches}");
        foreach (var pattern in report.Patterns)
        {
            Line(text, $"  {pattern.Name}: {pattern.Matches} · точність* {Precision(pattern.Useful, pattern.Labeled)}");
        }

        Line(text, $"Дублікати: пересилання {report.ForwardDuplicates} / той самий текст {report.SameTextDuplicates}");
        Line(text, $"Унікальних збігів: {report.UniqueMatches}");
        Line(text, $"Сповіщення: надіслано {report.Sent} · застарілі {report.SkippedStale} · помилки {report.Failed}");
        Line(text, $"Мітки: корисно {report.LabeledUseful} · хибне {report.LabeledFalse} · дубль {report.LabeledDuplicate} · без мітки {report.Unlabeled}");
        Line(text, $"Точність: {Precision(report.LabeledUseful, report.Labeled)}");
        Line(text, $"Затримка отримання: {Latency(report.ReceiveLatency)}");
        Line(text, $"Затримка надсилання: {Latency(report.SendLatency)}");
        Line(text, $"Перепідключень: {report.Reconnects} · без з'єднання {Duration(report.Disconnected)}");
        Line(text, $"Помилок обробки: {report.ProcessingErrors}");
        text.Append(MultiPatternNote);
        return text.ToString();
    }

    /// <summary>AC-F11.5: useful ÷ labeled, or <c>n/a</c> with nothing labeled.</summary>
    public static string Precision(int useful, int labeled) =>
        labeled == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * useful / labeled:0}% ({useful}/{labeled})");

    private static string Latency(LatencyStats stats) =>
        stats is { P50: { } p50, P95: { } p95 }
            ? string.Create(CultureInfo.InvariantCulture, $"p50 {p50.TotalSeconds:0.0} с · p95 {p95.TotalSeconds:0.0} с (n={stats.Count})")
            : "n/a";

    private static string Duration(TimeSpan duration)
    {
        var total = (long)duration.TotalSeconds;
        return total < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{total} с")
            : total < 3600
                ? string.Create(CultureInfo.InvariantCulture, $"{total / 60} хв {total % 60} с")
                : string.Create(CultureInfo.InvariantCulture, $"{total / 3600} год {total % 3600 / 60} хв");
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');
}
