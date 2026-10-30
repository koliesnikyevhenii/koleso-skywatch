using System.Text;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using TL;

namespace Skywatch.Worker.Telegram;

/// <summary>`login`: interactive first login that writes the encrypted session file (AC-F1.1, AC-F1.4).</summary>
internal sealed class LoginCommand(IOptions<TelegramOptions> options)
{
    public async Task<int> RunAsync(WTelegramLogBridge bridge, CancellationToken ct)
    {
        var telegram = options.Value;
        var sessionPath = Path.GetFullPath(telegram.SessionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);

        using var client = new WTelegram.Client(what => Config(telegram, bridge, what));
        var user = await client.LoginUserIfNeeded().WaitAsync(ct);
        SessionFiles.ClearRejected(telegram.SessionPath); // AC-F1.8: the new session is usable again

        Console.WriteLine($"Logged in as {DisplayName(user)}. Session saved to {sessionPath}.");
        return 0;
    }

    private static string? Config(TelegramOptions telegram, WTelegramLogBridge bridge, string what)
    {
        var value = what switch
        {
            "phone_number" => telegram.WTelegramConfig(what) ?? Prompt("Phone number (international format): "),
            "verification_code" => Prompt("Verification code: "),
            "password" => PromptHidden("2FA password: "),
            _ => telegram.WTelegramConfig(what),
        };

        if (what is "phone_number" or "verification_code" or "password")
        {
            bridge.AddSecret(value);
        }

        return value;
    }

    private static string DisplayName(User user)
    {
        var name = string.Join(' ', new[] { user.first_name, user.last_name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return name.Length > 0 ? name : "@" + user.MainUsername;
    }

    private static string? Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine()?.Trim();
    }

    private static string? PromptHidden(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var input = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return input.ToString();
    }
}
