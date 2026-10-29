using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Skywatch.Worker.Options;

internal sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DatabasePath { get; set; } = "data/skywatch.db";

    public bool StoreUnmatchedText { get; set; } = true;

    /// <summary>AC-F12.2: text of matched messages is cleared after this many days.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>AC-F12.1: text of unmatched messages is cleared after this many days.</summary>
    public int UnmatchedRetentionDays { get; set; } = 3;

    /// <summary>AC-F12.3: messages, notifications and service events are deleted after this many days.</summary>
    public int MetadataRetentionDays { get; set; } = 30;

    /// <summary>How long a connection waits for a lock held by another writer (connection string <c>Default Timeout</c>).</summary>
    public int BusyTimeoutSeconds { get; set; } = 30;

    /// <summary>Rows per retention statement, so each retention transaction stays short.</summary>
    public int RetentionBatchSize { get; set; } = 500;

    public string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = BusyTimeoutSeconds }.ToString();
}

/// <summary>Spec §6. A zero retention would clear text or delete rows right away. Failure messages name keys.</summary>
internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            failures.Add("Storage:DatabasePath is required.");
        }

        AddIfNotPositive(failures, "Storage:RetentionDays", options.RetentionDays);
        AddIfNotPositive(failures, "Storage:UnmatchedRetentionDays", options.UnmatchedRetentionDays);
        AddIfNotPositive(failures, "Storage:MetadataRetentionDays", options.MetadataRetentionDays);
        AddIfNotPositive(failures, "Storage:BusyTimeoutSeconds", options.BusyTimeoutSeconds);
        AddIfNotPositive(failures, "Storage:RetentionBatchSize", options.RetentionBatchSize);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddIfNotPositive(List<string> failures, string key, int value)
    {
        if (value <= 0)
        {
            failures.Add($"{key} must be greater than 0.");
        }
    }
}
