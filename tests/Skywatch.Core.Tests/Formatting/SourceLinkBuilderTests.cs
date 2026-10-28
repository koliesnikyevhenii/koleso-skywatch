using Skywatch.Core.Formatting;

namespace Skywatch.Core.Tests.Formatting;

// AC-F8.3. Fake ids only (-1000000000NNN).
public class SourceLinkBuilderTests
{
    [Fact]
    public void AC_F8_3_public_channel_or_supergroup_links_by_username() =>
        SourceLinkBuilder.Build(-1000000000042, "test_channel", 17).ShouldBe("https://t.me/test_channel/17");

    // Internal id = the marked id without the -100 prefix.
    [Theory]
    [InlineData(-1000000000042, "https://t.me/c/42/17")]
    [InlineData(-1000000000123, "https://t.me/c/123/17")]
    public void AC_F8_3_private_channel_or_supergroup_links_by_internal_id(long markedChatId, string expected)
    {
        SourceLinkBuilder.Build(markedChatId, null, 17).ShouldBe(expected);
        SourceLinkBuilder.Build(markedChatId, " ", 17).ShouldBe(expected);
    }

    [Fact]
    public void AC_F8_3_basic_group_has_no_link() =>
        SourceLinkBuilder.Build(-4242, null, 17).ShouldBeNull();
}
