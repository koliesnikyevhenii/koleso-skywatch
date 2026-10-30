using Microsoft.Extensions.Logging;
using TL;

namespace Skywatch.Worker.Telegram;

/// <summary>How the reader client waits after a Telegram error (AC-F3.10, AC-F3.11, risk R23).</summary>
internal static partial class TelegramRetry
{
    public static readonly TimeSpan FirstReconnectDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FloodWaitMargin = TimeSpan.FromSeconds(5);

    /// <summary>The session is not usable any more: retrying only repeats a rejected login (AC-F1.8).</summary>
    public static bool IsSessionRejected(Exception ex) => ex is TelegramSessionRejectedException or RpcException { Code: 401 };

    /// <summary>
    /// AC-F3.10: X seconds plus a margin for a 420 <c>FLOOD_WAIT_X</c>. The client already retries waits under its
    /// own threshold by itself, so this sees only the long ones.
    /// </summary>
    public static TimeSpan? FloodWait(Exception ex) =>
        ex is RpcException { Code: 420, X: > 0 } flood ? TimeSpan.FromSeconds(flood.X) + FloodWaitMargin : null;

    public static TimeSpan NextBackoff(TimeSpan current) => TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaxReconnectDelay.Ticks));

    /// <summary>Runs <paramref name="call"/> again after every FLOOD_WAIT until it succeeds or fails otherwise.</summary>
    public static async Task<T> HonourFloodWaitAsync<T>(Func<Task<T>> call, TimeProvider time, ILogger logger, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (FloodWait(ex) is { } wait)
            {
                LogFloodWait(logger, wait.TotalSeconds);
                await Task.Delay(wait, time, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram asked to wait (FLOOD_WAIT); the next attempt is in {WaitSeconds} s.")]
    public static partial void LogFloodWait(ILogger logger, double waitSeconds);
}
