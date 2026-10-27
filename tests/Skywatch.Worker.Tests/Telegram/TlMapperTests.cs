using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Tests.Telegram;

// AC-F1.2 / AC-F2.1: chats are identified by Bot-API-style marked ids.
// Fake ids only: -1000000000NNN is allowlisted in .gitleaks.toml.
public class TlMapperTests
{
    [Theory]
    [InlineData(42L, -1000000000042L)]
    [InlineData(1L, -1000000000001L)]
    [InlineData(999L, -1000000000999L)]
    public void AC_F2_1_channel_id_maps_to_marked_id_with_minus_100_prefix(long channelId, long markedId)
    {
        TlMapper.ChannelToMarkedId(channelId).ShouldBe(markedId);
        TlMapper.FromMarkedId(markedId).ShouldBe((true, channelId));
    }

    [Theory]
    [InlineData(4242L, -4242L)]
    [InlineData(1L, -1L)]
    [InlineData(999_999_999_999L, -999_999_999_999L)]
    public void AC_F2_1_basic_group_id_maps_to_negated_marked_id(long chatId, long markedId)
    {
        TlMapper.BasicGroupToMarkedId(chatId).ShouldBe(markedId);
        TlMapper.FromMarkedId(markedId).ShouldBe((false, chatId));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(12345L)]
    public void AC_F2_1_non_chat_marked_id_is_rejected(long markedId) =>
        Should.Throw<ArgumentOutOfRangeException>(() => TlMapper.FromMarkedId(markedId));
}
