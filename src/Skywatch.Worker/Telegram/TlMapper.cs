using TL;

namespace Skywatch.Worker.Telegram;

/// <summary>Maps TL types and ids to Skywatch values. TL types never leave <c>Worker/Telegram/</c>.</summary>
internal static class TlMapper
{
    // Bot-API-style marked ids: channels and supergroups are -100{id}, basic groups are -{id}.
    private const long _channelMarkOffset = -1_000_000_000_000L;

    public static long ChannelToMarkedId(long channelId) => _channelMarkOffset - channelId;

    public static long BasicGroupToMarkedId(long chatId) => -chatId;

    public static long ToMarkedId(ChatBase chat) =>
        chat is Channel or ChannelForbidden ? ChannelToMarkedId(chat.ID) : BasicGroupToMarkedId(chat.ID);

    public static (bool IsChannel, long PeerId) FromMarkedId(long markedId) => markedId switch
    {
        < _channelMarkOffset => (true, _channelMarkOffset - markedId),
        < 0 => (false, -markedId),
        _ => throw new ArgumentOutOfRangeException(nameof(markedId), markedId, "Chat ids are negative marked ids, as printed by list-chats."),
    };
}
