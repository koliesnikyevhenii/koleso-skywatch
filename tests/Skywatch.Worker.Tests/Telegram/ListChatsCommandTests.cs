using Skywatch.Worker.Telegram;
using TL;

namespace Skywatch.Worker.Tests.Telegram;

public class ListChatsCommandTests
{
    // AC-F1.2: marked chat id, type, title, public username and the noforwards flag, one chat per line.
    [Fact]
    public void AC_F1_2_list_chats_prints_marked_id_type_title_username_and_noforwards()
    {
        ChatBase[] chats =
        [
            new Channel { id = 42, title = "Test Channel", username = "test_channel", flags = Channel.Flags.broadcast | Channel.Flags.noforwards },
            new Channel { id = 43, title = "Test Supergroup", flags = Channel.Flags.megagroup },
            new Chat { id = 4242, title = "Test Group" },
        ];

        var lines = ListChatsCommand.Render(chats).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldContain("-1000000000042\tChannel\tTest Channel\t@test_channel\tnoforwards=yes");
        lines.ShouldContain("-1000000000043\tSupergroup\tTest Supergroup\t-\tnoforwards=no");
        lines.ShouldContain("-4242\tGroup\tTest Group\t-\tnoforwards=no");
    }

    // AC-F1.2: "every chat the reader account belongs to" — chats the account was removed from are not listed.
    [Fact]
    public void AC_F1_2_list_chats_skips_chats_the_account_no_longer_belongs_to()
    {
        ChatBase[] chats =
        [
            new ChannelForbidden { id = 44, title = "Banned Channel" },
            new ChatForbidden { id = 4243, title = "Kicked Group" },
            new Chat { id = 4244, title = "Left Group", flags = Chat.Flags.left },
            new Chat { id = 4245, title = "Migrated Group", flags = Chat.Flags.deactivated },
            new Channel { id = 45, title = "Left Channel", flags = Channel.Flags.broadcast | Channel.Flags.left },
        ];

        var output = ListChatsCommand.Render(chats);

        output.ShouldNotContain("-1000000000044");
        output.ShouldNotContain("-4243");
        output.ShouldNotContain("-4244");
        output.ShouldNotContain("-4245");
        output.ShouldNotContain("-1000000000045");
    }
}
