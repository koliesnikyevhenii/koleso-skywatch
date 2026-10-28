using System.Text.Json;

namespace Skywatch.Core.Tests.Samples;

/// <summary>Anonymized alert texts with the pattern names they must hit. Shared with the Worker patterns-from-config test.</summary>
public sealed record AlertSample(string Text, string[] Patterns)
{
    public static string FilePath { get; } = Path.Combine(RepoRoot.Path, "tests", "Skywatch.Core.Tests", "Samples", "alert-samples.json");

    public static IReadOnlyList<AlertSample> Load() =>
        JsonSerializer.Deserialize<AlertSample[]>(File.ReadAllText(FilePath), JsonSerializerOptions.Web)!;

    public static TheoryData<string, string[]> TheoryRows()
    {
        var rows = new TheoryData<string, string[]>();
        foreach (var sample in Load())
        {
            rows.Add(sample.Text, sample.Patterns);
        }

        return rows;
    }
}
