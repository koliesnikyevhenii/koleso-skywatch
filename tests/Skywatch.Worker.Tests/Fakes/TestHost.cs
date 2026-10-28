using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// Builds the real Worker host with only the given in-memory settings, so a developer's User Secrets,
/// environment variables or appsettings.json never leak into a test.
/// </summary>
internal static class TestHost
{
    // Fake values only (C4). Distinctive so a leak is easy to spot in captured logs.
    public const string FakeApiHash = "fakeapihash0123456789abcdef0000";
    public const string FakeSessionKey = "fake-session-key-do-not-log";
    public const string FakePhone = "+380001112233";

    /// <summary>
    /// The update source is always a fake (by default one that never emits), so a run-mode host in a test never
    /// connects to Telegram.
    /// </summary>
    public static Action<HostApplicationBuilder> Configure(
        IDictionary<string, string?> settings,
        CapturingLoggerProvider? logs = null,
        ITelegramUpdateSource? source = null,
        TimeProvider? time = null) =>
        builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(settings);
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs);
            }

            builder.Services.AddSingleton(source ?? new FakeUpdateSource());
            if (time is not null)
            {
                builder.Services.AddSingleton(time);
            }
        };

    public static IHost Build(
        string[] args,
        IDictionary<string, string?> settings,
        CapturingLoggerProvider? logs = null,
        ITelegramUpdateSource? source = null,
        TimeProvider? time = null) =>
        Program.BuildHost(args, Configure(settings, logs, source, time));

    /// <summary>Valid run-mode settings: Telegram settings plus the given sources as <c>Monitoring:Sources</c>.</summary>
    public static Dictionary<string, string?> RunModeSettings(string sessionPath, params (long ChatId, string Name)[] sources)
    {
        var settings = TelegramSettings(sessionPath);
        for (var i = 0; i < sources.Length; i++)
        {
            settings[$"Monitoring:Sources:{i}:ChatId"] = sources[i].ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"Monitoring:Sources:{i}:Name"] = sources[i].Name;
        }

        return settings;
    }

    /// <summary>The test pattern every settings set carries, because run mode needs at least one enabled pattern (AC-F5.9).</summary>
    public const string ProbePattern = "Probe";

    /// <summary>
    /// Complete, valid Telegram settings pointing at <paramref name="sessionPath"/>, plus <see cref="ProbePattern"/>
    /// (matches the word "тест") and a fresh database file.
    /// </summary>
    public static Dictionary<string, string?> TelegramSettings(string sessionPath) => new()
    {
        ["Telegram:ApiId"] = "12345",
        ["Telegram:ApiHash"] = FakeApiHash,
        ["Telegram:PhoneNumber"] = FakePhone,
        ["Telegram:SessionPath"] = sessionPath,
        ["Telegram:SessionKey"] = FakeSessionKey,
        ["Monitoring:Patterns:0:Name"] = ProbePattern,
        ["Monitoring:Patterns:0:Regex"] = @"\bтест\b",
        ["Storage:DatabasePath"] = TempDatabasePath(),
    };

    private static readonly string _databaseDirectory = Path.Combine(Path.GetTempPath(), "skywatch-tests");

    /// <summary>
    /// A fresh database file path. Pooled SQLite connections keep the file open after the host stops, so files are
    /// not deleted per test; leftovers from earlier runs are removed instead.
    /// </summary>
    public static string TempDatabasePath() => Path.Combine(_databaseDirectory, $"{Guid.NewGuid():N}.db");

    static TestHost()
    {
        Directory.CreateDirectory(_databaseDirectory);
        foreach (var file in Directory.EnumerateFiles(_databaseDirectory).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still open by a parallel test run; a later run removes it.
            }
        }
    }

    /// <summary>Replaces every configured pattern with the given ones.</summary>
    public static void SetPatterns(Dictionary<string, string?> settings, params (string Name, string Regex, bool Enabled)[] patterns)
    {
        foreach (var key in settings.Keys.Where(k => k.StartsWith("Monitoring:Patterns:", StringComparison.Ordinal)).ToList())
        {
            settings.Remove(key);
        }

        for (var i = 0; i < patterns.Length; i++)
        {
            settings[$"Monitoring:Patterns:{i}:Name"] = patterns[i].Name;
            settings[$"Monitoring:Patterns:{i}:Regex"] = patterns[i].Regex;
            settings[$"Monitoring:Patterns:{i}:Enabled"] = patterns[i].Enabled ? "true" : "false";
        }
    }

    /// <summary>A path in the temp directory that does not exist.</summary>
    public static string MissingSessionPath() => Path.Combine(Path.GetTempPath(), $"skywatch-test-{Guid.NewGuid():N}", "session.dat");

    /// <summary>An existing (empty) session file in the temp directory; deleted on dispose.</summary>
    public static TempFile ExistingSessionFile() => new(Path.Combine(Path.GetTempPath(), $"skywatch-test-{Guid.NewGuid():N}.dat"));

    internal sealed class TempFile : IDisposable
    {
        public TempFile(string path)
        {
            Path = path;
            File.WriteAllBytes(path, []);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
