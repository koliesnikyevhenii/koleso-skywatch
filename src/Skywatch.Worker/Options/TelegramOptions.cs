using System.Globalization;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Options;

internal sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public int ApiId { get; set; }

    public string ApiHash { get; set; } = "";

    public string PhoneNumber { get; set; } = "";

    public string SessionPath { get; set; } = "data/session.dat";

    public string SessionKey { get; set; } = "";

    public string UpdateStatePath { get; set; } = "data/updates.state";

    public int UpdateStateSaveIntervalSeconds { get; set; } = 60;

    /// <summary>AC-F1.9: the device the reader client presents, the same on every host and in every run mode.</summary>
    public string DeviceModel { get; set; } = "Skywatch";

    public string SystemVersion { get; set; } = "Server";

    public string AppVersion { get; set; } = "1.0";

    public string LangCode { get; set; } = "uk";

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
        "device_model" => OrDefault(DeviceModel),
        "system_version" => OrDefault(SystemVersion),
        "app_version" => OrDefault(AppVersion),
        "system_lang_code" or "lang_code" => OrDefault(LangCode),
        _ => null,
    };

    private static string? OrDefault(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Validation per run mode (plan P1a table). Failure messages name keys, never values (AC-F1.5).</summary>
internal sealed class TelegramOptionsValidator(RunMode mode) : IValidateOptions<TelegramOptions>
{
    /// <summary>
    /// AC-F1.9: WTelegramClient decodes the key from hex and uses it as the AES key of the session file, so only
    /// 16, 24 or 32 bytes work. Anything else fails only at the first login or start, deep inside the library.
    /// </summary>
    internal static readonly int[] SessionKeyHexLengths = [32, 48, 64];

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

        if (mode == RunMode.Run && string.IsNullOrWhiteSpace(options.PhoneNumber))
        {
            // AC-F1.3: WTelegramClient checks on resume that the session belongs to this number.
            failures.Add("Telegram:PhoneNumber is required: it is checked against the session on every start.");
        }

        if (string.IsNullOrWhiteSpace(options.SessionKey))
        {
            failures.Add("Telegram:SessionKey is required.");
        }
        else if (!IsValidSessionKey(options.SessionKey))
        {
            failures.Add("Telegram:SessionKey must be a hex string of 32, 48 or 64 characters, e.g. `openssl rand -hex 32`.");
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
        else if (mode != RunMode.Login && SessionFiles.IsMarkedRejected(options.SessionPath))
        {
            // AC-F1.8: no reconnect loop with a session Telegram already rejected (R23).
            failures.Add($"Telegram:SessionPath: Telegram rejected this session earlier (marker '{SessionFiles.RejectedMarkerPath(options.SessionPath)}'). Run `login` again.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidSessionKey(string key) =>
        SessionKeyHexLengths.Contains(key.Length) && key.All(char.IsAsciiHexDigit);
}
