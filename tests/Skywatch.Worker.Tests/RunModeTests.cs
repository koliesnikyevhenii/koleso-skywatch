using Microsoft.Extensions.Logging;
using Skywatch.Worker.Tests.Fakes;
using Skywatch.Worker.Tests.Telegram;

namespace Skywatch.Worker.Tests;

// Program.RunAsync installs the WTelegramClient log bridge (static), so these tests do not run in parallel with it.
[Collection(WTelegramStaticLog.Name)]
public class RunModeTests
{
    // AC-F1.3: with no argument and no session file, the service logs a Critical telling the operator
    // to run `login` and exits with a non-zero code (it never prompts).
    [Fact]
    public async Task AC_F1_3_run_without_session_file_logs_critical_with_login_and_exits_non_zero()
    {
        var logs = new CapturingLoggerProvider();
        var settings = TestHost.TelegramSettings(TestHost.MissingSessionPath());

        var exitCode = await Program.RunAsync([], TestHost.Configure(settings, logs), TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("login"));
    }

    // AC-F1.5 (startup part): a configuration failure is reported without echoing secret values.
    [Fact]
    public async Task AC_F1_5_invalid_configuration_log_contains_no_secret_values()
    {
        var logs = new CapturingLoggerProvider();
        var settings = TestHost.TelegramSettings(TestHost.MissingSessionPath());

        await Program.RunAsync(["list-chats"], TestHost.Configure(settings, logs), TestContext.Current.CancellationToken);

        logs.Entries.ShouldNotBeEmpty();
        logs.Entries.ShouldAllBe(e =>
            !e.Message.Contains(TestHost.FakeApiHash)
            && !e.Message.Contains(TestHost.FakeSessionKey)
            && !e.Message.Contains(TestHost.FakePhone));
    }

    // Plan P1a run-mode dispatch: an unknown first argument does not silently fall back to the run mode.
    [Fact]
    public async Task P1a_unknown_run_mode_exits_non_zero_with_critical()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();

        var exitCode = await Program.RunAsync(["logn"], TestHost.Configure(TestHost.TelegramSettings(session.Path), logs), TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("logn"));
    }
}
