using System.Globalization;
using Microsoft.Extensions.Options;

namespace Skywatch.Worker.Options;

internal sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public int ApiId { get; set; }

    public string ApiHash { get; set; } = "";

    public string PhoneNumber { get; set; } = "";

    public string SessionPath { get; set; } = "data/session.dat";

    public string SessionKey { get; set; } = "";

    /// <summary>
    /// Answers WTelegramClient's config callback. <see langword="null"/> means "use the library default";
    /// interactive values (verification code, password) are supplied by the command that needs them.
    /// </summary>
    public string? WTelegramConfig(string what) => what switch
    {
        "api_id" => ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => ApiHash,
        "phone_number" => string.IsNullOrWhiteSpace(PhoneNumber) ? null : PhoneNumber,
        "session_pathname" => SessionPath,
        "session_key" => SessionKey, // AC-F1.4: the session file is encrypted with this key
        _ => null,
    };
}

/// <summary>Validation per run mode (plan P1a table). Failure messages name keys, never values (AC-F1.5).</summary>
internal sealed class TelegramOptionsValidator(RunMode mode) : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        List<string> failures = [];
        if (options.ApiId <= 0)
        {
            failures.Add("Telegram:ApiId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiHash))
        {
            failures.Add("Telegram:ApiHash is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SessionKey))
        {
            failures.Add("Telegram:SessionKey is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SessionPath))
        {
            failures.Add("Telegram:SessionPath is required.");
        }
        else if (mode != RunMode.Login && !File.Exists(options.SessionPath))
        {
            // AC-F1.3: `login` creates the session; every other mode needs it.
            failures.Add($"Telegram:SessionPath: session file '{Path.GetFullPath(options.SessionPath)}' not found. Run `login` first.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
