using Skywatch.Core.Formatting;
using Skywatch.Core.Time;

namespace Skywatch.Core.Tests.Formatting;

// Spec AC-F11.2 (report content, multi-pattern note) and AC-F11.5 (n/a without labels).
public class DailyReportFormatterTests
{
    private static readonly ReportPeriod _period = new(
        new DateOnly(2026, 10, 25),
        new DateTime(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 25, 22, 0, 0, DateTimeKind.Utc));

    private static DailyReport Report(
        int useful = 4,
        int @false = 1,
        int duplicate = 1,
        IReadOnlyList<PatternStats>? patterns = null,
        LatencyStats? receive = null,
        TimeSpan? disconnected = null) =>
        new(
            _period,
            MessagesReceived: 1234,
            Matches: 12,
            patterns ?? [new PatternStats("Settlement", 8, 2, 3), new PatternStats("Abbreviation", 3, 0, 0)],
            ForwardDuplicates: 3,
            SameTextDuplicates: 1,
            UniqueMatches: 8,
            Sent: 7,
            SkippedStale: 1,
            Failed: 0,
            LabeledUseful: useful,
            LabeledFalse: @false,
            LabeledDuplicate: duplicate,
            Unlabeled: 1,
            ReceiveLatency: receive ?? new LatencyStats(1234, TimeSpan.FromSeconds(1.24), TimeSpan.FromSeconds(4.8)),
            SendLatency: new LatencyStats(7, TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(0.9)),
            Reconnects: 2,
            Disconnected: disconnected ?? TimeSpan.FromSeconds(192),
            ProcessingErrors: 0);

    // AC-F11.2: every figure of the report, in order, as plain text.
    [Fact]
    public void AC_F11_2_report_contains_every_figure()
    {
        DailyReportFormatter.Format(Report()).ShouldBe(
            """
            📊 Звіт за 25.10.2026 (25 год)
            Повідомлень отримано: 1234
            Збігів: 12
              Settlement: 8 · точність* 67% (2/3)
              Abbreviation: 3 · точність* n/a
            Дублікати: пересилання 3 / той самий текст 1
            Унікальних збігів: 8
            Сповіщення: надіслано 7 · застарілі 1 · помилки 0
            Мітки: корисно 4 · хибне 1 · дубль 1 · без мітки 1
            Точність: 67% (4/6)
            Затримка отримання: p50 1.2 с · p95 4.8 с (n=1234)
            Затримка надсилання: p50 0.3 с · p95 0.9 с (n=7)
            Перепідключень: 2 · без з'єднання 3 хв 12 с
            Помилок обробки: 0
            * Повідомлення, що збіглося з кількома шаблонами, враховано в кожному з них.
            """.ReplaceLineEndings("\n"));
    }

    // AC-F11.5: zero labeled items → n/a, overall and per pattern; no division error.
    [Fact]
    public void AC_F11_5_precision_without_labels_is_n_a()
    {
        var text = DailyReportFormatter.Format(Report(useful: 0, @false: 0, duplicate: 0, patterns: [new PatternStats("Settlement", 5, 0, 0)]));

        text.ShouldContain("Точність: n/a");
        text.ShouldContain("Settlement: 5 · точність* n/a");
        DailyReportFormatter.Precision(0, 0).ShouldBe("n/a");
    }

    [Theory]
    [InlineData(1, 1, "100% (1/1)")]
    [InlineData(0, 3, "0% (0/3)")]
    [InlineData(1, 3, "33% (1/3)")]
    public void AC_F11_2_precision_is_useful_over_labeled(int useful, int labeled, string expected) =>
        DailyReportFormatter.Precision(useful, labeled).ShouldBe(expected);

    // AC-F11.2: no samples (e.g. nothing sent) → n/a; long outages in hours.
    [Fact]
    public void AC_F11_2_latency_without_samples_and_long_outage()
    {
        var text = DailyReportFormatter.Format(Report(receive: LatencyStats.From([]), disconnected: TimeSpan.FromMinutes(125)));

        text.ShouldContain("Затримка отримання: n/a");
        text.ShouldContain("без з'єднання 2 год 5 хв");
    }

    // AC-F11.2: nearest-rank p50 / p95.
    [Fact]
    public void AC_F11_2_percentiles_of_a_known_distribution()
    {
        var stats = LatencyStats.From(Enumerable.Range(1, 100).Reverse().Select(s => TimeSpan.FromSeconds(s)));

        stats.ShouldBe(new LatencyStats(100, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(95)));
    }

    [Fact]
    public void AC_F11_2_percentiles_of_small_samples()
    {
        LatencyStats.From([TimeSpan.FromSeconds(7)]).ShouldBe(new LatencyStats(1, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)));
        LatencyStats.From([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)]).ShouldBe(new LatencyStats(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        LatencyStats.From([]).ShouldBe(new LatencyStats(0, null, null));
    }
}
