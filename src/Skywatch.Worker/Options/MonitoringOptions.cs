using Microsoft.Extensions.Options;

namespace Skywatch.Worker.Options;

internal sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>AC-F2.1: allowlisted chats, by the marked ids `list-chats` prints. Identifying: never committed (C4).</summary>
    public List<SourceOptions> Sources { get; set; } = [];
}

internal sealed class SourceOptions
{
    public long ChatId { get; set; }

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Run mode only (plan P1a table).</summary>
internal sealed class MonitoringOptionsValidator : IValidateOptions<MonitoringOptions>
{
    public ValidateOptionsResult Validate(string? name, MonitoringOptions options)
    {
        List<string> failures = [];
        for (var i = 0; i < options.Sources.Count; i++)
        {
            var source = options.Sources[i];
            if (source.ChatId >= 0)
            {
                failures.Add($"Monitoring:Sources:{i}:ChatId must be a negative marked chat id as printed by `list-chats`.");
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                failures.Add($"Monitoring:Sources:{i}:Name is required.");
            }
        }

        foreach (var duplicate in options.Sources.GroupBy(s => s.ChatId).Where(g => g.Count() > 1))
        {
            failures.Add($"Monitoring:Sources: ChatId {duplicate.Key} is listed more than once.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
