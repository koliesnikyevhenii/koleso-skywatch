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

    /// <summary>AC-F7.2: a message older than this is never sent.</summary>
    public int MaxMessageAgeSeconds { get; set; } = 120;
}

/// <summary>Run mode only. Failure messages name keys.</summary>
internal sealed class NotificationOptionsValidator : IValidateOptions<NotificationOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationOptions options) => options.MaxMessageAgeSeconds > 0
        ? ValidateOptionsResult.Success
        : ValidateOptionsResult.Fail("Notifications:MaxMessageAgeSeconds must be greater than 0.");
}
