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

    // AC-F1.7: a second process on the same session (the lock is held) logs a Critical naming the lock file and
    // exits non-zero before anything connects to Telegram (R22).
    [Theory]
    [InlineData("")]
    [InlineData("list-chats")]
    [InlineData("login")]
    public async Task AC_F1_7_second_process_on_the_same_session_exits_with_critical_without_connecting(string mode)
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var source = new FakeUpdateSource();
        using var held = SessionFiles.TryLock(session.Path);
        held.ShouldNotBeNull();

        var exitCode = await Program.RunAsync(mode.Length == 0 ? [] : [mode], TestHost.Configure(settings, logs, source), TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);
        source.ResolveCalls.ShouldBe(0);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("Another Skywatch process") && e.Message.Contains(SessionFiles.LockPath(session.Path)));
    }

    // AC-F1.8: a rejection at start leaves the marker; the next start (the container restart) fails with a Critical
    // telling to run `login` and never connects (R23).
    [Fact]
    public async Task AC_F1_8_rejected_session_is_marked_and_the_next_start_does_not_connect()
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var rejecting = new FakeUpdateSource { ResolveFailure = new TelegramSessionRejectedException("AUTH_KEY_UNREGISTERED") };
        (await Program.RunAsync([], TestHost.Configure(settings, new CapturingLoggerProvider(), rejecting), TestContext.Current.CancellationToken)).ShouldNotBe(0);
        SessionFiles.IsMarkedRejected(session.Path).ShouldBeTrue();

        var logs = new CapturingLoggerProvider();
        var restarted = new FakeUpdateSource();
        var exitCode = await Program.RunAsync([], TestHost.Configure(settings, logs, restarted), TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);
        restarted.ResolveCalls.ShouldBe(0);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("rejected") && e.Message.Contains("login"));
    }

    // AC-F1.8: a rejection while reconnecting stops the running service with a Critical and leaves the marker.
    [Fact]
    public async Task AC_F1_8_rejection_while_reconnecting_stops_the_service_and_marks_the_session()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var source = new FakeUpdateSource { Sources = [new ResolvedSource(-1000000000042, "Test source", SourceType.Channel, null, false, Resolved: true)] };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var run = Program.RunAsync([], TestHost.Configure(settings, logs, source), timeout.Token);
        source.Emit(new SessionRejected("SESSION_REVOKED", DateTime.UtcNow));
        var exitCode = await run;

        timeout.IsCancellationRequested.ShouldBeFalse("the service stops by itself");
        exitCode.ShouldNotBe(0);
        SessionFiles.IsMarkedRejected(session.Path).ShouldBeTrue();
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains("SESSION_REVOKED") && e.Message.Contains("login"));
    }

    // AC-F1.5: a listener start error is logged through the secret filter.
    [Fact]
    public async Task AC_F1_5_listener_start_error_is_logged_without_secret_values()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source"));
        var source = new FakeUpdateSource { ResolveFailure = new InvalidOperationException($"failed for {TestHost.FakePhone} with {TestHost.FakeApiHash}") };

        (await Program.RunAsync([], TestHost.Configure(settings, logs, source), TestContext.Current.CancellationToken)).ShouldNotBe(0);

        var critical = logs.Entries.Where(e => e.Level == LogLevel.Critical).ShouldHaveSingleItem();
        critical.Message.ShouldContain("failed for");
        critical.Message.ShouldNotContain(TestHost.FakePhone.TrimStart('+'));
        critical.Message.ShouldNotContain(TestHost.FakeApiHash);
    }
}
