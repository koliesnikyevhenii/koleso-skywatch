using Microsoft.Extensions.Options;
using Skywatch.Core.Filtering;

namespace Skywatch.Worker.Options;

internal sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>AC-F2.1: allowlisted chats, by the marked ids `list-chats` prints. Identifying: never committed (C4).</summary>
    public List<SourceOptions> Sources { get; set; } = [];

    /// <summary>Spec F5: named patterns, evaluated in this order. Defaults live in appsettings.json.</summary>
    public List<PatternOptions> Patterns { get; set; } = [];

    /// <summary>AC-F6.4 (a): window for forward duplicates (same origin key).</summary>
    public int DuplicateWindowMinutes { get; set; } = 60;

    /// <summary>AC-F6.4 (b): window for identical-text duplicates.</summary>
    public int ContentDuplicateWindowMinutes { get; set; } = 10;

    /// <summary>AC-F6.4 (b): when <see langword="true"/>, a repeat of the same text in the same source is always sent.</summary>
    public bool ContentDuplicateAcrossSourcesOnly { get; set; } = true;

    /// <summary>Spec F9.</summary>
    public EditHandling EditHandling { get; set; } = EditHandling.NotifyOnNewMatch;

    /// <summary>AC-F9.5: quiet time after the last edit before the reply is created.</summary>
    public int EditDebounceSeconds { get; set; } = 15;
}

/// <summary>Spec F9 (decision D3).</summary>
internal enum EditHandling
{
    /// <summary>AC-F9.1: edits change nothing.</summary>
    Ignore,

    /// <summary>AC-F9.3/F9.4: a message that newly matches after an edit is notified once.</summary>
    NotifyOnNewMatch,

    /// <summary>AC-F9.5: as <see cref="NotifyOnNewMatch"/>, plus a reply when a sent message changes.</summary>
    ReplyOnChange,
}

internal sealed class PatternOptions
{
    public string Name { get; set; } = "";

    public string Regex { get; set; } = "";

    public bool CaseSensitive { get; set; }

    public bool Enabled { get; set; } = true;

    public PatternDefinition ToDefinition() => new(Name, Regex, CaseSensitive, Enabled);
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

        if (options.DuplicateWindowMinutes <= 0)
        {
            failures.Add("Monitoring:DuplicateWindowMinutes must be greater than 0.");
        }

        if (options.ContentDuplicateWindowMinutes <= 0)
        {
            failures.Add("Monitoring:ContentDuplicateWindowMinutes must be greater than 0.");
        }

        if (options.EditDebounceSeconds <= 0)
        {
            failures.Add("Monitoring:EditDebounceSeconds must be greater than 0.");
        }

        ValidatePatterns(options.Patterns, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // AC-F5.9: compiled with the same options as KeywordFilter, so a pattern that passes here also loads there.
    private static void ValidatePatterns(List<PatternOptions> patterns, List<string> failures)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            if (string.IsNullOrWhiteSpace(pattern.Name))
            {
                failures.Add($"Monitoring:Patterns:{i}:Name is required.");
            }

            if (string.IsNullOrWhiteSpace(pattern.Regex))
            {
                failures.Add($"Monitoring:Patterns:{i}:Regex is required.");
                continue;
            }

            try
            {
                KeywordFilter.Compile(pattern.ToDefinition());
            }
            catch (ArgumentException ex)
            {
                failures.Add($"Monitoring:Patterns:{i}:Regex of pattern '{pattern.Name}' is invalid: {ex.Message}");
            }
        }

        foreach (var duplicate in patterns.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            failures.Add($"Monitoring:Patterns: Name '{duplicate.Key}' is listed more than once.");
        }

        if (!patterns.Any(p => p.Enabled))
        {
            failures.Add("Monitoring:Patterns: at least one enabled pattern is required.");
        }
    }
}
