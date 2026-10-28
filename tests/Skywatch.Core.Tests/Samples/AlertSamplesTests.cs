using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Core.Tests.Filtering;

namespace Skywatch.Core.Tests.Samples;

public class AlertSamplesTests
{
    private static readonly KeywordFilter _filter = new(DefaultPatterns.All);

    // Spec F4 + F5 end to end: each sample hits exactly the expected patterns, in configuration order.
    [Theory]
    [MemberData(nameof(AlertSample.TheoryRows), MemberType = typeof(AlertSample))]
    public void AC_F5_sample_alert_text_hits_expected_patterns(string text, string[] patterns) =>
        _filter.Match(TextNormalizer.Normalize(text)).Hits.Select(h => h.PatternName).ShouldBe(patterns);
}
