using Skywatch.Core.Deduplication;
using Skywatch.Core.Normalization;

namespace Skywatch.Core.Tests.Deduplication;

public class ContentHasherTests
{
    // AC-F6.3: lowercase hex SHA-256 of the UTF-8 bytes of NormalizedText.Lower (FIPS 180-2 test vectors).
    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void AC_F6_3_hash_is_lowercase_hex_sha256_of_utf8_lower(string lower, string expected) =>
        ContentHasher.Hash(new NormalizedText(lower, lower.ToUpperInvariant())).ShouldBe(expected);

    // AC-F6.3: only Lower is hashed, so the case-preserved variant does not change the hash.
    [Fact]
    public void AC_F6_3_case_preserved_variant_is_not_hashed() =>
        ContentHasher.Hash(new NormalizedText("abc", "ABC")).ShouldBe(ContentHasher.Hash(new NormalizedText("abc", "aBc")));

    // AC-F6.3 with F4: texts that differ only by zero-width characters, whitespace or case hash the same.
    [Theory]
    [InlineData("Шахед на Краснопавлівку", "Шахед на Красно​павлівку")]
    [InlineData("Шахед на Краснопавлівку", "  Шахед   на\nКраснопавлівку ")]
    [InlineData("Шахед на Краснопавлівку", "ШАХЕД НА КРАСНОПАВЛІВКУ")]
    public void AC_F6_3_texts_differing_only_by_invisible_characters_whitespace_or_case_hash_the_same(string a, string b) =>
        ContentHasher.Hash(TextNormalizer.Normalize(b)).ShouldBe(ContentHasher.Hash(TextNormalizer.Normalize(a)));

    [Fact]
    public void AC_F6_3_different_texts_hash_differently() =>
        ContentHasher.Hash(TextNormalizer.Normalize("Шахед на Краснопавлівку"))
            .ShouldNotBe(ContentHasher.Hash(TextNormalizer.Normalize("Ракета на Краснопавлівку")));
}
