using System.Text.RegularExpressions;
using Skywatch.Core.Filtering;
using Skywatch.Core.Normalization;

namespace Skywatch.Core.Tests.Filtering;

public class KeywordFilterTests
{
    private static readonly Lazy<KeywordFilter> _filter = new(() => new KeywordFilter(DefaultPatterns.All));

    private static string[] HitNames(string text, KeywordFilter? filter = null) =>
        [.. (filter ?? _filter.Value).Match(TextNormalizer.Normalize(text)).Hits.Select(h => h.PatternName)];

    private static readonly string[] _settlementForms =
    [
        "Краснопавлівка", "Краснопавлівки", "Краснопавлівці", "Краснопавлівку", "Краснопавлівкою", "Краснопавлівко",
        "Краснопавловка", "Краснопавловки", "Краснопавловке", "Краснопавловку", "Краснопавловкой", "Краснопавловкою",
        "Красно-Павлівка", "#Краснопавлівка",
    ];

    // Each AC-F5.1 form as written, in upper case and in mixed case.
    public static TheoryData<string> SettlementForms()
    {
        var data = new TheoryData<string>();
        foreach (var form in _settlementForms)
        {
            data.Add(form);
            data.Add(form.ToUpperInvariant());
            data.Add(string.Concat(form.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c))));
        }

        return data;
    }

    // AC-F5.1: every listed form matches Settlement, in any case.
    [Theory]
    [MemberData(nameof(SettlementForms))]
    public void AC_F5_1_settlement_form_matches_settlement(string form)
    {
        HitNames(form).ShouldContain("Settlement");
        HitNames($"Увага: {form}, будьте в укритті.").ShouldContain("Settlement");
    }

    // AC-F5.2: adjective forms match Adjective and do not match Settlement.
    [Theory]
    [InlineData("Краснопавлівське")]
    [InlineData("краснопавлівського")]
    [InlineData("Краснопавловское")]
    public void AC_F5_2_adjective_form_matches_adjective_and_not_settlement(string text)
    {
        var hits = HitNames(text);

        hits.ShouldContain("Adjective");
        hits.ShouldNotContain("Settlement");
    }

    // AC-F5.3: КП after a preposition matches Abbreviation.
    [Theory]
    [InlineData("на КП")]
    [InlineData("Над КП")]
    [InlineData("курсом на КП")]
    [InlineData("біля Кп")]
    [InlineData("2 БпЛА в КП")]
    public void AC_F5_3_abbreviation_after_preposition_matches(string text) =>
        HitNames(text).ShouldContain("Abbreviation");

    // AC-F5.4: КП without a preceding preposition, or in lower case, does not match Abbreviation.
    [Theory]
    [InlineData("КП \"Харківводоканал\"")]
    [InlineData("уражено КП")]
    [InlineData("по даним КП")]
    [InlineData("на кп")]
    public void AC_F5_4_abbreviation_without_preposition_or_in_lower_case_does_not_match(string text) =>
        HitNames(text).ShouldNotContain("Abbreviation");

    // AC-F5.5: the colloquial name matches Colloquial as a whole word.
    [Theory]
    [InlineData("на Краснопе")]
    [InlineData("Краснопе.")]
    [InlineData("(Краснопе)")]
    public void AC_F5_5_colloquial_name_matches_colloquial(string text) =>
        HitNames(text).ShouldContain("Colloquial");

    // AC-F5.5: similar words match no pattern at all.
    [Theory]
    [InlineData("Красноперекопськ")]
    [InlineData("красноперка")]
    [InlineData("краснопёрка")]
    public void AC_F5_5_similar_words_match_no_pattern(string text) =>
        HitNames(text).ShouldBeEmpty();

    // AC-F5.6: patterns never match inside another word.
    [Theory]
    [InlineData("якраснопавлівка")]
    [InlineData("якраснопавлівське")]
    [InlineData("закраснопе")]
    [InlineData("наКП")]
    public void AC_F5_6_patterns_never_match_inside_another_word(string text) =>
        HitNames(text).ShouldBeEmpty();

    // AC-F5.7: several hits are returned in configuration order, not text order.
    [Fact]
    public void AC_F5_7_multiple_hits_are_returned_in_configuration_order()
    {
        var result = _filter.Value.Match(TextNormalizer.Normalize("Краснопе: біля Краснопавлівського водосховища, на КП, у Краснопавлівці"));

        result.IsMatch.ShouldBeTrue();
        result.Hits.Select(h => h.PatternName).ShouldBe(["Settlement", "Adjective", "Abbreviation", "Colloquial"]);
        result.Hits[0].MatchedText.ShouldBe("краснопавлівці");
        result.Hits[2].MatchedText.ShouldBe("на КП");
    }

    // AC-F5.8: disabled patterns are not evaluated.
    [Fact]
    public void AC_F5_8_disabled_pattern_is_not_evaluated()
    {
        var filter = new KeywordFilter([DefaultPatterns.Settlement with { Enabled = false }, DefaultPatterns.Colloquial]);

        var result = filter.Match(TextNormalizer.Normalize("Краснопавлівка"));

        result.IsMatch.ShouldBeFalse();
        result.Hits.ShouldBeEmpty();
    }

    // AC-F5.9: CultureInvariant and a 100 ms timeout always; IgnoreCase only when CaseSensitive = false.
    [Fact]
    public void AC_F5_9_patterns_compile_culture_invariant_with_100_ms_timeout_and_ignore_case_when_insensitive()
    {
        var insensitive = KeywordFilter.Compile(DefaultPatterns.Settlement);
        var sensitive = KeywordFilter.Compile(DefaultPatterns.Abbreviation);

        insensitive.Options.HasFlag(RegexOptions.CultureInvariant).ShouldBeTrue();
        insensitive.Options.HasFlag(RegexOptions.IgnoreCase).ShouldBeTrue();
        sensitive.Options.HasFlag(RegexOptions.CultureInvariant).ShouldBeTrue();
        sensitive.Options.HasFlag(RegexOptions.IgnoreCase).ShouldBeFalse();
        insensitive.MatchTimeout.ShouldBe(TimeSpan.FromMilliseconds(100));
        sensitive.MatchTimeout.ShouldBe(TimeSpan.FromMilliseconds(100));
    }

    // AC-F5.10: a timeout is reported with the pattern name, counts as no match, and later patterns still run.
    [Fact]
    public void AC_F5_10_regex_timeout_is_reported_and_treated_as_no_match_for_that_pattern()
    {
        List<string> timedOut = [];
        var catastrophic = new PatternDefinition("Catastrophic", @"^(\w+\s?)*$", CaseSensitive: false);
        var filter = new KeywordFilter([catastrophic, DefaultPatterns.Settlement], timedOut.Add);
        var text = "Краснопавлівка " + new string('а', 5000) + "!";

        var result = Should.NotThrow(() => filter.Match(TextNormalizer.Normalize(text)));

        timedOut.ShouldBe(["Catastrophic"]);
        result.Hits.Select(h => h.PatternName).ShouldBe(["Settlement"]);
    }

    // AC-F5.11: an empty normalized text is not a match.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ​ ")]
    public void AC_F5_11_empty_text_is_not_a_match(string? text)
    {
        var result = _filter.Value.Match(TextNormalizer.Normalize(text));

        result.IsMatch.ShouldBeFalse();
        result.Hits.ShouldBeEmpty();
    }
}
