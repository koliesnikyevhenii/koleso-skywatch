using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using TL;

namespace Skywatch.Worker.Tests.Telegram;

// AC-F3.11 reconnect loop after a ReactorError, AC-F3.10 FLOOD_WAIT on reconnect, AC-F1.8 rejection on reconnect.
// The reconnect step is scripted, so no Telegram connection is made; the fake clock drives every wait.
public sealed class WTelegramReconnectTests : IDisposable
{
    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentQueue<Exception?> _script = new();
    private readonly ConcurrentQueue<DateTimeOffset> _attempts = new();
    private readonly WTelegramUpdateSource _source;

    public WTelegramReconnectTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        _source = new WTelegramUpdateSource(
            new TelegramOptions(),
            new MonitoringOptions(),
            _time,
            _loggerFactory.CreateLogger<WTelegramUpdateSource>(),
            saveState: _ => { },
            reconnect: _ =>
            {
                _attempts.Enqueue(_time.GetUtcNow());
                return _script.TryDequeue(out var failure) && failure is not null ? Task.FromException(failure) : Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        _source.Dispose();
        _loggerFactory.Dispose();
        _logs.Dispose();
    }

    private static readonly DateTimeOffset _start = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    private async Task<List<SourceEvent>> DrainAsync()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        List<SourceEvent> events = [];
        await foreach (var e in _source.ReadAllAsync(stop.Token))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>Waits until the loop sleeps for the <paramref name="timer"/>-th time, then lets that wait pass.</summary>
    private async Task PassWaitAsync(int timer, TimeSpan wait)
    {
        await _time.TimersCreatedAtLeast(timer);
        _time.Advance(wait - TimeSpan.FromTicks(1));
        _attempts.Count.ShouldBe(timer - 1, "no attempt before the wait is over");
        _time.Advance(TimeSpan.FromTicks(1));
        await Eventually.ShouldHappen(() => _attempts.Count == timer, $"attempt {timer}");
    }

    // AC-F3.11: failed attempts wait 10 s, then 20 s, then 40 s; success records Disconnected then Reconnected.
    [Fact]
    public async Task AC_F3_11_failed_reconnects_back_off_and_success_reports_reconnected()
    {
        _script.Enqueue(new IOException("network down"));
        _script.Enqueue(new IOException("network down"));
        _script.Enqueue(null);

        var loop = _source.OnReactorErrorAsync();
        await PassWaitAsync(1, TimeSpan.FromSeconds(10));
        await PassWaitAsync(2, TimeSpan.FromSeconds(20));
        await PassWaitAsync(3, TimeSpan.FromSeconds(40));
        await loop;

        _attempts.Select(a => (a - _start).TotalSeconds).ShouldBe([10, 30, 70]);
        var events = await DrainAsync();
        events.Select(e => ((ConnectionStateChanged)e).State).ShouldBe([ConnectionState.Disconnected, ConnectionState.Reconnected]);
        _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Reconnect to Telegram failed")).ShouldBe(2);
    }

    // AC-F3.10: a FLOOD_WAIT on reconnect makes the next attempt wait X + 5 s, logged at Warning.
    [Fact]
    public async Task AC_F3_10_flood_wait_on_reconnect_delays_the_next_attempt()
    {
        _script.Enqueue(new RpcException(420, "FLOOD_WAIT_300", 300));
        _script.Enqueue(null);

        var loop = _source.OnReactorErrorAsync();
        await PassWaitAsync(1, TimeSpan.FromSeconds(10));
        await PassWaitAsync(2, TimeSpan.FromSeconds(305));
        await loop;

        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("FLOOD_WAIT") && e.Message.Contains("305"));
        (await DrainAsync()).Last().ShouldBeOfType<ConnectionStateChanged>().State.ShouldBe(ConnectionState.Reconnected);
    }

    // AC-F1.8: a rejected session ends the attempts and is reported as SessionRejected (no Reconnected).
    [Fact]
    public async Task AC_F1_8_rejected_session_on_reconnect_stops_retrying()
    {
        _script.Enqueue(new RpcException(401, "SESSION_REVOKED", -1));

        var loop = _source.OnReactorErrorAsync();
        await PassWaitAsync(1, TimeSpan.FromSeconds(10));
        await loop;
        _time.Advance(TimeSpan.FromHours(1));

        _attempts.Count.ShouldBe(1);
        var events = await DrainAsync();
        events[0].ShouldBeOfType<ConnectionStateChanged>().State.ShouldBe(ConnectionState.Disconnected);
        events[1].ShouldBeOfType<SessionRejected>().Reason.ShouldContain("SESSION_REVOKED");
        events.Count.ShouldBe(2);
    }

    // AC-F3.11: a second reactor error while the loop runs does not start a second loop.
    [Fact]
    public async Task AC_F3_11_one_reconnect_loop_at_a_time()
    {
        _script.Enqueue(null);

        var loop = _source.OnReactorErrorAsync();
        await _time.TimersCreatedAtLeast(1);
        await _source.OnReactorErrorAsync();
        _time.Advance(TimeSpan.FromSeconds(10));
        await loop;

        _attempts.Count.ShouldBe(1);
        (await DrainAsync()).Count.ShouldBe(2);
    }
}
