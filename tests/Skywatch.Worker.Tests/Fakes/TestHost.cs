using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    public static Action<HostApplicationBuilder> Configure(IDictionary<string, string?> settings, CapturingLoggerProvider? logs = null) =>
        builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(settings);
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs);
            }
        };

    public static IHost Build(string[] args, IDictionary<string, string?> settings, CapturingLoggerProvider? logs = null) =>
        Program.BuildHost(args, Configure(settings, logs));

    /// <summary>Complete, valid Telegram settings pointing at <paramref name="sessionPath"/>.</summary>
    public static Dictionary<string, string?> TelegramSettings(string sessionPath) => new()
    {
        ["Telegram:ApiId"] = "12345",
        ["Telegram:ApiHash"] = FakeApiHash,
        ["Telegram:PhoneNumber"] = FakePhone,
        ["Telegram:SessionPath"] = sessionPath,
        ["Telegram:SessionKey"] = FakeSessionKey,
    };

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
