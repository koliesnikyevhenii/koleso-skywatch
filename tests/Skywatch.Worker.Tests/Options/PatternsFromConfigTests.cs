using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;
using Skywatch.Worker.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// The default patterns as committed in appsettings.json, bound the way the host binds them.
// Guards against JSON escaping mistakes: "\b" in JSON is a backspace, the regex needs "\\b".
public class PatternsFromConfigTests
{
    private static readonly MonitoringOptions _committed = LoadCommitted();

    private static MonitoringOptions LoadCommitted()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot.Find(), "src", "Skywatch.Worker", "appsettings.json"), optional: false)
            .Build();
        var options = new MonitoringOptions();
        config.GetSection(MonitoringOptions.SectionName).Bind(options);
        return options;
    }

    private static string[] HitNames(string text) =>
        [.. new KeywordFilter(_committed.Patterns.Select(p => p.ToDefinition())).Match(TextNormalizer.Normalize(text)).Hits.Select(h => h.PatternName)];

    // Spec F5 default patterns table: names, order and case sensitivity.
    [Fact]
    public void AC_F5_committed_config_has_the_default_patterns_in_order()
    {
        _committed.Patterns.Select(p => (p.Name, p.CaseSensitive, p.Enabled)).ShouldBe(
        [
            ("Settlement", false, true),
            ("Adjective", false, true),
            ("Abbreviation", true, true),
            ("Colloquial", false, true),
        ]);
    }

    // AC-F5.1 through the committed config.
    [Theory]
    [InlineData("Краснопавлівка")]
    [InlineData("КРАСНОПАВЛОВКЕ")]
    [InlineData("Красно-Павлівка")]
    [InlineData("#Краснопавлівкою")]
    public void AC_F5_1_committed_settlement_pattern_matches(string text) =>
        HitNames(text).ShouldContain("Settlement");

    // AC-F5.3 / AC-F5.4 through the committed config (case-sensitive pattern, word boundaries).
    [Theory]
    [InlineData("курсом на КП", true)]
    [InlineData("біля Кп", true)]
    [InlineData("на кп", false)]
    [InlineData("уражено КП", false)]
    [InlineData("наКП", false)]
    public void AC_F5_3_committed_abbreviation_pattern_matches_only_after_a_preposition(string text, bool matches) =>
        HitNames(text).Contains("Abbreviation").ShouldBe(matches);

    // Shared sample set (tests/Skywatch.Core.Tests/Samples) through the committed config.
    [Theory]
    [MemberData(nameof(Samples))]
    public void AC_F5_committed_patterns_hit_expected_patterns_for_sample_alerts(string text, string[] patterns) =>
        HitNames(text).ShouldBe(patterns);

    public static TheoryData<string, string[]> Samples()
    {
        var path = Path.Combine(RepoRoot.Find(), "tests", "Skywatch.Core.Tests", "Samples", "alert-samples.json");
        var rows = new TheoryData<string, string[]>();
        foreach (var sample in JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(path), JsonSerializerOptions.Web)!)
        {
            rows.Add(sample.Text, sample.Patterns);
        }

        return rows;
    }

    private sealed record Sample(string Text, string[] Patterns);
}
