using Skywatch.Core.Normalization;

namespace Skywatch.Core.Tests.Normalization;

public class TextNormalizerTests
{
    // AC-F4.1: Unicode is normalized to NFKC (full-width forms, ligatures).
    [Theory]
    [InlineData("ＫＰ １２３", "KP 123")]
    [InlineData("ﬁle", "file")]
    public void AC_F4_1_unicode_is_normalized_to_nfkc(string input, string expected) =>
        TextNormalizer.Normalize(input).CasePreserved.ShouldBe(expected);

    // AC-F4.1: a decomposed letter is composed, so it compares equal to the precomposed one.
    [Fact]
    public void AC_F4_1_decomposed_cyrillic_letter_is_composed()
    {
        var decomposed = "Краснопавлївка"; // і + combining diaeresis → ї (U+0457)
        TextNormalizer.Normalize(decomposed).CasePreserved.ShouldBe("Краснопавлївка");
    }

    // AC-F4.2: zero-width characters and the soft hyphen are removed.
    [Theory]
    [InlineData('​')]
    [InlineData('‌')]
    [InlineData('‍')]
    [InlineData('⁠')]
    [InlineData('﻿')]
    [InlineData('­')]
    public void AC_F4_2_zero_width_characters_and_soft_hyphen_are_removed(char invisible)
    {
        var text = $"Красно{invisible}павлів{invisible}ка";

        var normalized = TextNormalizer.Normalize(text);

        normalized.CasePreserved.ShouldBe("Краснопавлівка");
        normalized.Lower.ShouldBe("краснопавлівка");
    }

    // AC-F4.3: runs of whitespace (newlines, NBSP, tabs) collapse to one space; the result is trimmed.
    [Theory]
    [InlineData("  на\n\nКП  ", "на КП")]
    [InlineData("на КП", "на КП")]
    [InlineData("на\t \r\nКП  зараз", "на КП зараз")]
    public void AC_F4_3_whitespace_runs_collapse_to_one_space_and_are_trimmed(string input, string expected) =>
        TextNormalizer.Normalize(input).CasePreserved.ShouldBe(expected);

    // AC-F4.4: ё/Ё become е/Е.
    [Fact]
    public void AC_F4_4_yo_becomes_ye_in_both_variants()
    {
        var normalized = TextNormalizer.Normalize("Ёлка краснопёрка");

        normalized.CasePreserved.ShouldBe("Елка красноперка");
        normalized.Lower.ShouldBe("елка красноперка");
    }

    // AC-F4.5: Lower uses invariant lowercasing; Cyrillic І→і, Ї→ї; CasePreserved keeps the case.
    [Fact]
    public void AC_F4_5_lower_lowercases_ukrainian_letters_and_case_preserved_keeps_case()
    {
        var normalized = TextNormalizer.Normalize("КРАСНОПАВЛІВКА ЇЖАК Є Ґ");

        normalized.Lower.ShouldBe("краснопавлівка їжак є ґ");
        normalized.CasePreserved.ShouldBe("КРАСНОПАВЛІВКА ЇЖАК Є Ґ");
    }

    // AC-F4.6: і and и are not merged; Latin look-alikes are not converted (decision D10).
    [Fact]
    public void AC_F4_6_i_and_y_are_not_merged()
    {
        TextNormalizer.Normalize("і и І И").Lower.ShouldBe("і и і и");
    }

    [Fact]
    public void AC_F4_6_latin_look_alike_i_is_not_converted()
    {
        var withLatinI = "Краснопавлiвка";

        var normalized = TextNormalizer.Normalize(withLatinI);

        normalized.CasePreserved.ShouldBe(withLatinI);
        normalized.Lower.ShouldContain("i");
        normalized.Lower.ShouldNotContain("і");
    }

    // AC-F4.7: null or whitespace-only input returns empty strings without throwing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n \t​")]
    public void AC_F4_7_null_or_whitespace_input_returns_empty_strings(string? input)
    {
        var normalized = TextNormalizer.Normalize(input);

        normalized.Lower.ShouldBe("");
        normalized.CasePreserved.ShouldBe("");
    }
}
