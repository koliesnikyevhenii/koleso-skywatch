using Microsoft.Extensions.Logging;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using TL;

namespace Skywatch.Worker.Tests.Telegram;

// AC-F3.10 (FLOOD_WAIT), AC-F3.11 (backoff) and AC-F1.8 (what counts as a rejected session).
public sealed class TelegramRetryTests : IDisposable
{
    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public TelegramRetryTests() => _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _logs.Dispose();
    }

    private static RpcException Flood(int seconds) => new(420, $"FLOOD_WAIT_{seconds}", seconds);

    // AC-F3.10: X seconds plus 5 s; anything else is not a flood wait.
    [Fact]
    public void AC_F3_10_flood_wait_is_x_seconds_plus_margin()
    {
        TelegramRetry.FloodWait(Flood(120)).ShouldBe(TimeSpan.FromSeconds(125));
        TelegramRetry.FloodWait(new RpcException(420, "FLOOD_WAIT", -1)).ShouldBeNull();
        TelegramRetry.FloodWait(new RpcException(400, "PEER_ID_INVALID", -1)).ShouldBeNull();
        TelegramRetry.FloodWait(new IOException("network")).ShouldBeNull();
    }

    // AC-F1.8: 401 errors and a login prompt in the run mode mean the session is rejected; other errors do not.
    [Fact]
    public void AC_F1_8_unauthorized_and_login_prompt_count_as_rejected_session()
    {
        TelegramRetry.IsSessionRejected(new RpcException(401, "AUTH_KEY_UNREGISTERED", -1)).ShouldBeTrue();
        TelegramRetry.IsSessionRejected(new RpcException(401, "SESSION_REVOKED", -1)).ShouldBeTrue();
        TelegramRetry.IsSessionRejected(new TelegramSessionRejectedException("not authorized")).ShouldBeTrue();
        TelegramRetry.IsSessionRejected(Flood(10)).ShouldBeFalse();
        TelegramRetry.IsSessionRejected(new IOException("network")).ShouldBeFalse();
    }

    // AC-F3.11: 10 s doubling, capped at 10 min.
    [Fact]
    public void AC_F3_11_backoff_doubles_up_to_ten_minutes()
    {
        var delays = new List<TimeSpan>();
        var delay = TelegramRetry.FirstReconnectDelay;
        for (var i = 0; i < 10; i++)
        {
            delays.Add(delay);
            delay = TelegramRetry.NextBackoff(delay);
        }

        delays.Select(d => d.TotalSeconds).ShouldBe([10, 20, 40, 80, 160, 320, 600, 600, 600, 600]);
    }

    // AC-F3.10 at start: the call is repeated only after X + 5 s, with a Warning naming the wait; the result is returned.
    [Fact]
    public async Task AC_F3_10_flood_wait_at_start_is_waited_out_and_the_call_repeated()
    {
        var calls = 0;
        var call = TelegramRetry.HonourFloodWaitAsync(
            () => ++calls == 1 ? Task.FromException<string>(Flood(60)) : Task.FromResult("chats"),
            _time,
            _loggerFactory.CreateLogger("test"),
            TestContext.Current.CancellationToken);

        await _time.TimersCreatedAtLeast(1);
        _time.Advance(TimeSpan.FromSeconds(65) - TimeSpan.FromTicks(1));
        calls.ShouldBe(1);
        _time.Advance(TimeSpan.FromTicks(1));

        (await call).ShouldBe("chats");
        calls.ShouldBe(2);
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("FLOOD_WAIT") && e.Message.Contains("65"));
    }

    // AC-F3.10: other errors are not retried here.
    [Fact]
    public async Task AC_F3_10_other_errors_are_not_retried()
    {
        var calls = 0;

        await Should.ThrowAsync<RpcException>(() => TelegramRetry.HonourFloodWaitAsync<string>(
            () =>
            {
                calls++;
                throw new RpcException(401, "AUTH_KEY_UNREGISTERED", -1);
            },
            _time,
            _loggerFactory.CreateLogger("test"),
            TestContext.Current.CancellationToken));

        calls.ShouldBe(1);
    }
}
