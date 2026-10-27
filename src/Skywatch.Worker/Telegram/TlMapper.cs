using Skywatch.Core.Deduplication;
using Skywatch.Worker.Options;
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

    /// <summary>Maps a new or edited chat message; <see langword="null"/> for anything else or a message without text (AC-F3.5).</summary>
    public static IncomingMessage? ToIncomingMessage(Update update, DateTime receivedAtUtc)
    {
        var (messageBase, isEdit) = update switch
        {
            UpdateNewChannelMessage u => (u.message, false),
            UpdateEditChannelMessage u => (u.message, true),
            UpdateNewMessage u => (u.message, false),
            UpdateEditMessage u => (u.message, true),
            _ => (null, false),
        };

        // Message.message holds the caption for media messages, so one field covers both (AC-F3.5).
        if (messageBase is not Message message || string.IsNullOrWhiteSpace(message.message) || ToMarkedId(message.peer_id) is not { } chatId)
        {
            return null;
        }

        // AC-F3.6: the original post when Telegram names it, else the message itself (spec §3 origin key).
        var origin = message.fwd_from is { from_id: PeerChannel original, channel_post: > 0 } forward
            ? new OriginKey(ChannelToMarkedId(original.channel_id), forward.channel_post)
            : new OriginKey(chatId, message.id);

        return new IncomingMessage(
            chatId,
            message.id,
            message.message,
            AsUtc(message.date),
            receivedAtUtc,
            origin,
            isEdit,
            isEdit ? AsUtc(message.edit_date) : null);
    }

    /// <summary>Resolves each enabled configured source against the reader account's chats (AC-F2.2, F2.3).</summary>
    public static IReadOnlyList<ResolvedSource> ResolveSources(IEnumerable<SourceOptions> configured, IEnumerable<ChatBase> chats)
    {
        Dictionary<long, (ChatBase Chat, SourceType Type, bool NoForwards)> member = [];
        foreach (var chat in chats)
        {
            // Same membership rule as list-chats: left, kicked and migrated chats are not the account's chats.
            (SourceType Type, bool NoForwards)? kind = chat switch
            {
                Channel c when !c.flags.HasFlag(Channel.Flags.left) =>
                    (c.flags.HasFlag(Channel.Flags.broadcast) ? SourceType.Channel : SourceType.Supergroup, c.flags.HasFlag(Channel.Flags.noforwards)),
                Chat g when !g.flags.HasFlag(Chat.Flags.left) && !g.flags.HasFlag(Chat.Flags.deactivated) =>
                    (SourceType.Group, g.flags.HasFlag(Chat.Flags.noforwards)),
                _ => null,
            };
            if (kind is { } k)
            {
                member[ToMarkedId(chat)] = (chat, k.Type, k.NoForwards);
            }
        }

        return
        [
            .. configured.Where(s => s.Enabled).Select(s => member.TryGetValue(s.ChatId, out var m)
                ? new ResolvedSource(s.ChatId, s.Name, m.Type, string.IsNullOrEmpty(m.Chat.MainUsername) ? null : m.Chat.MainUsername, m.NoForwards, Resolved: true)
                : new ResolvedSource(s.ChatId, s.Name, FromMarkedId(s.ChatId).IsChannel ? SourceType.Channel : SourceType.Group, null, NoForwards: false, Resolved: false)),
        ];
    }

    private static long? ToMarkedId(Peer? peer) => peer switch
    {
        PeerChannel c => ChannelToMarkedId(c.channel_id),
        PeerChat g => BasicGroupToMarkedId(g.chat_id),
        _ => null, // private chats are never sources
    };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
