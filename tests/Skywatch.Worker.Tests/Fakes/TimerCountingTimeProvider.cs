using Microsoft.Extensions.Time.Testing;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that counts created timers, so a test advances time only after the code under
/// test has started waiting (<c>Task.Delay</c> and <c>PeriodicTimer</c> each create one).
/// </summary>
internal sealed class TimerCountingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private int _timers;

    public int TimersCreated => Volatile.Read(ref _timers);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timers);
        return timer;
    }

    public Task TimersCreatedAtLeast(int count) => Eventually.ShouldHappen(() => TimersCreated >= count, $"{count} timers created");
}
