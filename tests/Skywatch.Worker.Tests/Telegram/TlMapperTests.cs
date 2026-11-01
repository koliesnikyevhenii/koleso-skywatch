using Skywatch.Core.Deduplication;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using TL;

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

// Spec F3: TL updates become IncomingMessage. Hand-built TL objects, fake ids only.
public class TlMapperMessageTests
{
    private static readonly DateTime _published = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _received = _published.AddSeconds(3);

    private static Message ChannelPost(int id, string text, long channelId = 42) =>
        new() { id = id, peer_id = new PeerChannel { channel_id = channelId }, date = _published, message = text };

    // AC-F3.5: the text used for a message is its text.
    [Fact]
    public void AC_F3_5_channel_text_message_maps_to_incoming_message_with_ids_and_utc_times()
    {
        var message = TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = ChannelPost(7, "Тестовий текст") }, _received);

        message.ShouldNotBeNull();
        message.ChatId.ShouldBe(-1000000000042L);
        message.MessageId.ShouldBe(7);
        message.Text.ShouldBe("Тестовий текст");
        message.PublishedAtUtc.ShouldBe(_published);
        message.PublishedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        message.ReceivedAtUtc.ShouldBe(_received);
        message.IsEdit.ShouldBeFalse();
        message.EditedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void AC_F3_5_basic_group_message_maps_with_marked_group_id()
    {
        var update = new UpdateNewMessage
        {
            message = new Message { id = 8, peer_id = new PeerChat { chat_id = 4242 }, date = _published, message = "Текст у групі" },
        };

        var message = TlMapper.ToIncomingMessage(update, _received);

        message.ShouldNotBeNull();
        message.ChatId.ShouldBe(-4242L);
        message.MessageId.ShouldBe(8);
    }

    // AC-F3.5: for media messages the text is the caption.
    [Fact]
    public void AC_F3_5_media_caption_is_used_as_text()
    {
        var post = ChannelPost(9, "Підпис до фото");
        post.media = new MessageMediaPhoto();

        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = post }, _received)!.Text.ShouldBe("Підпис до фото");
    }

    // AC-F3.5: messages with neither text nor caption are discarded.
    [Fact]
    public void AC_F3_5_media_without_caption_is_discarded()
    {
        var post = ChannelPost(10, "");
        post.media = new MessageMediaPhoto();

        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = post }, _received).ShouldBeNull();
    }

    [Fact]
    public void AC_F3_5_service_messages_private_messages_and_other_updates_are_discarded()
    {
        var service = new MessageService { id = 11, peer_id = new PeerChannel { channel_id = 42 }, date = _published, action = new MessageActionChatEditTitle { title = "x" } };
        var privateMessage = new Message { id = 12, peer_id = new PeerUser { user_id = 777 }, date = _published, message = "Привіт" };

        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = service }, _received).ShouldBeNull();
        TlMapper.ToIncomingMessage(new UpdateNewMessage { message = privateMessage }, _received).ShouldBeNull();
        TlMapper.ToIncomingMessage(new UpdateUserStatus { user_id = 777 }, _received).ShouldBeNull();
    }

    // AC-F3.6: for forwarded messages the forward origin (chatId, messageId) is captured.
    [Fact]
    public void AC_F3_6_forwarded_channel_post_captures_forward_origin()
    {
        var post = ChannelPost(13, "Переслане");
        post.fwd_from = new MessageFwdHeader
        {
            flags = MessageFwdHeader.Flags.has_from_id | MessageFwdHeader.Flags.has_channel_post,
            from_id = new PeerChannel { channel_id = 77 },
            channel_post = 555,
            date = _published.AddMinutes(-1),
        };

        var message = TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = post }, _received);

        message!.Origin.ShouldBe(new OriginKey(-1000000000077L, 555));
    }

    // Spec §3 origin key: "else the message itself".
    [Fact]
    public void AC_F3_6_non_forwarded_message_origin_is_the_message_itself()
    {
        var message = TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = ChannelPost(14, "Оригінал") }, _received);

        message!.Origin.ShouldBe(new OriginKey(-1000000000042L, 14));
    }

    // AC-F3.6 "when Telegram provides it": a forward from a user carries no original post.
    [Fact]
    public void AC_F3_6_forward_without_original_post_falls_back_to_the_message_itself()
    {
        var post = ChannelPost(15, "Переслано від користувача");
        post.fwd_from = new MessageFwdHeader { flags = MessageFwdHeader.Flags.has_from_id, from_id = new PeerUser { user_id = 777 }, date = _published };

        var message = TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = post }, _received);

        message!.Origin.ShouldBe(new OriginKey(-1000000000042L, 15));
    }

    // AC-F3.9: UpdateEditChannelMessage and UpdateEditMessage → IsEdit = true and EditedAtUtc set.
    [Fact]
    public void AC_F3_9_edited_channel_message_maps_with_is_edit_and_edited_at()
    {
        var edited = _published.AddMinutes(2);
        var post = ChannelPost(16, "Виправлений текст");
        post.flags = Message.Flags.has_edit_date;
        post.edit_date = edited;

        var message = TlMapper.ToIncomingMessage(new UpdateEditChannelMessage { message = post }, _received);

        message.ShouldNotBeNull();
        message.IsEdit.ShouldBeTrue();
        message.EditedAtUtc.ShouldBe(edited);
        message.PublishedAtUtc.ShouldBe(_published);
    }

    [Fact]
    public void AC_F3_9_edited_basic_group_message_maps_with_is_edit_and_edited_at()
    {
        var edited = _published.AddMinutes(3);
        var update = new UpdateEditMessage
        {
            message = new Message
            {
                id = 17,
                peer_id = new PeerChat { chat_id = 4242 },
                date = _published,
                message = "Виправлено",
                flags = Message.Flags.has_edit_date,
                edit_date = edited,
            },
        };

        var message = TlMapper.ToIncomingMessage(update, _received);

        message.ShouldNotBeNull();
        message.ChatId.ShouldBe(-4242L);
        message.IsEdit.ShouldBeTrue();
        message.EditedAtUtc.ShouldBe(edited);
    }
}

// AC-F2.2/F2.3: enabled configured sources against the reader account's chats. Fake ids only.
public class TlMapperSourceTests
{
    // AC-F2.2: each enabled source is resolved with type, username and the noforwards flag.
    [Fact]
    public void AC_F2_2_enabled_sources_are_resolved_with_type_username_and_noforwards()
    {
        SourceOptions[] configured =
        [
            new() { ChatId = -1000000000042, Name = "Configured channel" },
            new() { ChatId = -1000000000043, Name = "Configured supergroup" },
            new() { ChatId = -4242, Name = "Configured group" },
        ];
        ChatBase[] chats =
        [
            new Channel { id = 42, title = "Title A", username = "test_channel", flags = Channel.Flags.broadcast | Channel.Flags.noforwards },
            new Channel { id = 43, title = "Title B", flags = Channel.Flags.megagroup },
            new Chat { id = 4242, title = "Title C" },
        ];

        var resolved = TlMapper.ResolveSources(configured, chats);

        resolved.ShouldBe(
        [
            new ResolvedSource(-1000000000042, "Configured channel", SourceType.Channel, "test_channel", NoForwards: true, Resolved: true),
            new ResolvedSource(-1000000000043, "Configured supergroup", SourceType.Supergroup, null, NoForwards: false, Resolved: true),
            new ResolvedSource(-4242, "Configured group", SourceType.Group, null, NoForwards: false, Resolved: true),
        ]);
    }

    // AC-F2.3: an enabled source the account cannot reach is unresolved (and later skipped with a Warning).
    [Fact]
    public void AC_F2_3_source_missing_or_left_is_unresolved_and_disabled_sources_are_not_resolved()
    {
        SourceOptions[] configured =
        [
            new() { ChatId = -1000000000044, Name = "Missing channel" },
            new() { ChatId = -1000000000045, Name = "Left channel" },
            new() { ChatId = -1000000000046, Name = "Banned channel" },
            new() { ChatId = -1000000000047, Name = "Disabled channel", Enabled = false },
        ];
        ChatBase[] chats =
        [
            new Channel { id = 45, title = "Left", flags = Channel.Flags.broadcast | Channel.Flags.left },
            new ChannelForbidden { id = 46, title = "Banned" },
            new Channel { id = 47, title = "Disabled", flags = Channel.Flags.broadcast },
        ];

        var resolved = TlMapper.ResolveSources(configured, chats);

        resolved.Select(s => (s.ChatId, s.Name, s.Resolved)).ShouldBe(
        [
            (-1000000000044L, "Missing channel", false),
            (-1000000000045L, "Left channel", false),
            (-1000000000046L, "Banned channel", false),
        ]);
    }

    private static readonly DateTime _replyReceived = new(2026, 11, 1, 9, 21, 3, DateTimeKind.Utc);

    private static Message Post(int id, string text) =>
        new() { id = id, peer_id = new PeerChannel { channel_id = 42 }, date = _replyReceived.AddSeconds(-3), message = text };

    // AC-F13.1: a reply in the same chat carries the replied-to message id.
    [Fact]
    public void AC_F13_1_reply_in_the_same_chat_carries_the_replied_to_id()
    {
        var post = Post(9, "Не фіксується");
        post.reply_to = new MessageReplyHeader { flags = MessageReplyHeader.Flags.has_reply_to_msg_id, reply_to_msg_id = 7 };

        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = post }, _replyReceived).ShouldNotBeNull().ReplyToMessageId.ShouldBe(7);
    }

    // AC-F13.1: a reply that names another chat, or no reply at all, carries no id.
    [Fact]
    public void AC_F13_1_reply_to_another_chat_or_no_reply_carries_no_id()
    {
        var crossChat = Post(9, "Текст");
        crossChat.reply_to = new MessageReplyHeader
        {
            flags = MessageReplyHeader.Flags.has_reply_to_msg_id | MessageReplyHeader.Flags.has_reply_to_peer_id,
            reply_to_msg_id = 7,
            reply_to_peer_id = new PeerChannel { channel_id = 43 },
        };

        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = crossChat }, _replyReceived).ShouldNotBeNull().ReplyToMessageId.ShouldBeNull();
        TlMapper.ToIncomingMessage(new UpdateNewChannelMessage { message = Post(10, "Текст") }, _replyReceived).ShouldNotBeNull().ReplyToMessageId.ShouldBeNull();
    }
}
