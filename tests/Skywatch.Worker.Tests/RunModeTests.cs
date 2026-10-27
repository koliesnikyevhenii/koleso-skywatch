using Microsoft.Extensions.Logging;
using Skywatch.Core.Deduplication;
using Skywatch.Worker.Telegram;
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

    // AC-F1.5 (run-mode start test): run mode with known fake secrets in config processes events without logging any of them.
    [Fact]
    public async Task AC_F1_5_run_mode_start_log_contains_no_secret_values()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var source = new FakeUpdateSource { Sources = [new ResolvedSource(-1000000000042, "Test source", SourceType.Channel, null, false, Resolved: true)] };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = Program.RunAsync([], TestHost.Configure(settings, logs, source), stop.Token);
        source.Emit(new IncomingMessage(-1000000000042, 1, "Текст", DateTime.UtcNow, DateTime.UtcNow, new OriginKey(-1000000000042, 1), false, null));
        source.Emit(new ConnectionStateChanged(ConnectionState.Disconnected, DateTime.UtcNow));
        await Eventually.ShouldHappen(() => logs.Lines.Any(l => l.Contains("message 1")), "the message was processed");
        await stop.CancelAsync();
        await run;

        logs.Entries.ShouldAllBe(e =>
            !e.Message.Contains(TestHost.FakeApiHash)
            && !e.Message.Contains(TestHost.FakeSessionKey)
            && !e.Message.Contains(TestHost.FakePhone)
            && !e.Message.Contains(TestHost.FakePhone.TrimStart('+')));
    }

    // AC-F1.3 "rejected": the session is rejected when the listener connects → Critical telling to run `login`, non-zero exit.
    [Fact]
    public async Task AC_F1_3_run_with_rejected_session_logs_critical_with_login_and_exits_non_zero()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var source = new FakeUpdateSource { ResolveFailure = new TelegramSessionRejectedException("AUTH_KEY_UNREGISTERED") };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var exitCode = await Program.RunAsync([], TestHost.Configure(settings, logs, source), timeout.Token);

        timeout.IsCancellationRequested.ShouldBeFalse("the service stops by itself instead of waiting for shutdown");
        exitCode.ShouldNotBe(0);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("login"));
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
