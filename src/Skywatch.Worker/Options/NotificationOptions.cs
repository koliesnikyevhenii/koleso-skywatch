using Microsoft.Extensions.Options;

namespace Skywatch.Worker.Options;

internal sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// AC-F6.6/F6.7: with <see langword="false"/>, matches are only logged and no notification rows are created.
    /// With <see langword="true"/>, <c>Pending</c> rows wait in the outbox until the dispatcher (P4b) sends them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Secret (C4, C5): User Secrets or an environment variable, never logged.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>C2: the private test group, the only chat the bot ever sends to. Identifying: never committed.</summary>
    public long TargetChatId { get; set; }

    /// <summary>AC-F10.2/F10.4: the Telegram user ids allowed to label. Identifying: never committed.</summary>
    public long[] LabelerUserIds { get; set; } = [];

    /// <summary>AC-F7.2: a message older than this is never sent.</summary>
    public int MaxMessageAgeSeconds { get; set; } = 120;

    /// <summary>AC-F8.7: total attempts for network errors, timeouts and 5xx.</summary>
    public int MaxSendAttempts { get; set; } = 3;

    /// <summary>AC-F8.12: IANA id, resolved by <see cref="TimeZoneResolver"/>.</summary>
    public string DisplayTimeZone { get; set; } = TimeZoneResolver.Kyiv;
}

/// <summary>Run mode only (spec §6). Failure messages name keys, never values.</summary>
internal sealed class NotificationOptionsValidator(Func<string, TimeZoneInfo?> findTimeZone) : IValidateOptions<NotificationOptions>
{
    public NotificationOptionsValidator()
        : this(TimeZoneResolver.FindSystemTimeZone)
    {
    }

    public ValidateOptionsResult Validate(string? name, NotificationOptions options)
    {
        List<string> failures = [];
        if (options.Enabled && string.IsNullOrWhiteSpace(options.BotToken))
        {
            failures.Add("Notifications:BotToken is required when Notifications:Enabled is true.");
        }

        if (options.Enabled && options.TargetChatId == 0)
        {
            failures.Add("Notifications:TargetChatId is required when Notifications:Enabled is true.");
        }
        else if (options.TargetChatId > 0)
        {
            // C2: a group id is negative; a positive id would be a private chat with a user.
            failures.Add("Notifications:TargetChatId must be the negative id of the private test group.");
        }

        if (options.MaxMessageAgeSeconds <= 0)
        {
            failures.Add("Notifications:MaxMessageAgeSeconds must be greater than 0.");
        }

        if (options.MaxSendAttempts <= 0)
        {
            failures.Add("Notifications:MaxSendAttempts must be greater than 0.");
        }

        if (TimeZoneResolver.Resolve(options.DisplayTimeZone, findTimeZone) is null)
        {
            failures.Add($"Notifications:DisplayTimeZone '{options.DisplayTimeZone}' is not a time zone known to this system.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
