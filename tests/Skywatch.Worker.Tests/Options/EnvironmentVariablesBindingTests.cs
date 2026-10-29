using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using Telegram.Bot;

namespace Skywatch.Worker.Tests.Options;

// Plan P7: on the VPS every setting comes from environment variables (deploy/.env, `__` as the section separator,
// `__0` for list items). The variables carry a unique prefix so parallel tests cannot see them. Fake values only.
public sealed class EnvironmentVariablesBindingTests : IDisposable
{
    private readonly string _prefix = $"SKYWATCH_TEST_{Guid.NewGuid():N}_";
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();
    private readonly string _databasePath = TestHost.TempDatabasePath();
    private readonly Dictionary<string, string> _variables;

    public EnvironmentVariablesBindingTests()
    {
        _variables = new()
        {
            ["Telegram__ApiId"] = "12345",
            ["Telegram__ApiHash"] = TestHost.FakeApiHash,
            ["Telegram__PhoneNumber"] = TestHost.FakePhone,
            ["Telegram__SessionKey"] = TestHost.FakeSessionKey,
            ["Telegram__SessionPath"] = _session.Path,
            ["Monitoring__Sources__0__ChatId"] = "-1000000000042",
            ["Monitoring__Sources__0__Name"] = "Test source A",
            ["Monitoring__Sources__1__ChatId"] = "-1000000000043",
            ["Monitoring__Sources__1__Name"] = "Test source B",
            ["Monitoring__Patterns__0__Name"] = TestHost.ProbePattern,
            ["Monitoring__Patterns__0__Regex"] = @"\bтест\b",
            ["Notifications__Enabled"] = "true",
            ["Notifications__BotToken"] = "123456:fake-bot-token-do-not-log",
            ["Notifications__TargetChatId"] = "-1000000000777",
            ["Notifications__LabelerUserIds__0"] = "1001",
            ["Notifications__LabelerUserIds__1"] = "1002",
            ["Storage__DatabasePath"] = _databasePath,
            ["Storage__StoreUnmatchedText"] = "true",
            ["Storage__UnmatchedRetentionDays"] = "8",
            ["Reporting__DailyReportLocalTime"] = "08:30",
        };
        foreach (var (key, value) in _variables)
        {
            Environment.SetEnvironmentVariable(_prefix + key, value);
        }
    }

    public void Dispose()
    {
        foreach (var key in _variables.Keys)
        {
            Environment.SetEnvironmentVariable(_prefix + key, null);
        }

        _session.Dispose();
    }

    [Fact]
    public void P7_settings_bind_from_environment_variables()
    {
        using var host = Program.BuildHost([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddEnvironmentVariables(_prefix);
            builder.Services.AddSingleton<ITelegramUpdateSource>(new FakeUpdateSource());
            builder.Services.AddSingleton<ITelegramBotClient>(new FakeTelegramBotClient());
        });
        T Value<T>()
            where T : class => host.Services.GetRequiredService<IOptions<T>>().Value;

        var telegram = Value<TelegramOptions>();
        (telegram.ApiId, telegram.ApiHash, telegram.SessionKey, telegram.SessionPath).ShouldBe((12345, TestHost.FakeApiHash, TestHost.FakeSessionKey, _session.Path));
        Value<MonitoringOptions>().Sources.Select(s => (s.ChatId, s.Name)).ShouldBe([(-1000000000042L, "Test source A"), (-1000000000043L, "Test source B")]);
        var notifications = Value<NotificationOptions>();
        (notifications.Enabled, notifications.BotToken, notifications.TargetChatId).ShouldBe((true, "123456:fake-bot-token-do-not-log", -1000000000777L));
        notifications.LabelerUserIds.ShouldBe([1001L, 1002L]);
        var storage = Value<StorageOptions>();
        (storage.DatabasePath, storage.StoreUnmatchedText, storage.UnmatchedRetentionDays).ShouldBe((_databasePath, true, 8));
        Value<ReportingOptions>().ReportTime.ShouldBe(new TimeOnly(8, 30));
    }
}
