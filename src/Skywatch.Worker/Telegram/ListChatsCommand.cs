using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using TL;

namespace Skywatch.Worker.Telegram;

/// <summary>`list-chats`: prints the reader account's chats so the operator can fill <c>Monitoring:Sources</c> (AC-F1.2).</summary>
internal sealed class ListChatsCommand(IOptions<TelegramOptions> options)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var telegram = options.Value;

        // Uses the existing session only: anything that would start an interactive login fails instead.
        using var client = new WTelegram.Client(what => what is "phone_number" or "verification_code" or "password"
            ? throw new InvalidOperationException("The session is not authorized. Run `login` first.")
            : telegram.WTelegramConfig(what));

        Messages_Chats chats;
        try
        {
            await client.ConnectAsync().WaitAsync(ct);
            chats = await client.Messages_GetAllChats().WaitAsync(ct);
        }
        catch (RpcException ex) when (ex.Code == 401)
        {
            throw new InvalidOperationException($"Telegram rejected the session ({ex.Message}). Run `login` again.", ex);
        }

        Console.Write(Render(chats.chats.Values));
        return 0;
    }

    public static string Render(IEnumerable<ChatBase> chats)
    {
        var output = new StringBuilder();
        output.AppendLine("ChatId\tType\tTitle\tUsername\tNoForwards");
        foreach (var chat in chats)
        {
            (string Type, bool NoForwards)? row = chat switch
            {
                Channel c when !c.flags.HasFlag(Channel.Flags.left) =>
                    (c.flags.HasFlag(Channel.Flags.broadcast) ? "Channel" : "Supergroup", c.flags.HasFlag(Channel.Flags.noforwards)),
                Chat g when !g.flags.HasFlag(Chat.Flags.left) && !g.flags.HasFlag(Chat.Flags.deactivated) =>
                    ("Group", g.flags.HasFlag(Chat.Flags.noforwards)),
                _ => null,
            };
            if (row is not { } r)
            {
                continue;
            }

            var username = string.IsNullOrEmpty(chat.MainUsername) ? "-" : "@" + chat.MainUsername;
            output.AppendLine(CultureInfo.InvariantCulture, $"{TlMapper.ToMarkedId(chat)}\t{r.Type}\t{chat.Title}\t{username}\tnoforwards={(r.NoForwards ? "yes" : "no")}");
        }

        return output.ToString();
    }
}
