using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ITelegramUpdateSource"/>: emits messages and connection events, returns a configured
/// source list (or a failure) and records every <see cref="TrySaveStateAsync"/> call.
/// </summary>
internal sealed class FakeUpdateSource : ITelegramUpdateSource
{
    private readonly Channel<SourceEvent> _events = Channel.CreateUnbounded<SourceEvent>();
    private int _yielded;
    private int _handled;
    private int _resolveCalls;

    public IReadOnlyList<ResolvedSource> Sources { get; init; } = [];

    public Exception? ResolveFailure { get; init; }

    /// <summary>Yield nothing until the reader is cancelled, so every emitted event is still queued at shutdown.</summary>
    public bool HoldUntilStopped { get; init; }

    /// <summary>For each save call, how many events had been yielded at that moment.</summary>
    public ConcurrentQueue<int> SaveCalls { get; } = new();

    /// <summary>
    /// Events the consumer has finished with, including its save check: counted when it asks for the next event.
    /// </summary>
    public int Handled => Volatile.Read(ref _handled);

    /// <summary>How often the service tried to connect (AC-F1.7, AC-F1.8: never, when it must not).</summary>
    public int ResolveCalls => Volatile.Read(ref _resolveCalls);

    public void Emit(SourceEvent sourceEvent) => _events.Writer.TryWrite(sourceEvent);

    public Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _resolveCalls);
        return ResolveFailure is null ? Task.FromResult(Sources) : Task.FromException<IReadOnlyList<ResolvedSource>>(ResolveFailure);
    }

    public async IAsyncEnumerable<SourceEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            SourceEvent next;
            try
            {
                if (HoldUntilStopped)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                next = await _events.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Interlocked.Increment(ref _yielded);
            yield return next;
            Interlocked.Increment(ref _handled);
        }

        // Same contract as the real source: after cancellation, queued events are still delivered.
        while (_events.Reader.TryRead(out var queued))
        {
            Interlocked.Increment(ref _yielded);
            yield return queued;
        }
    }

    public Task<bool> TrySaveStateAsync(CancellationToken ct)
    {
        SaveCalls.Enqueue(Volatile.Read(ref _yielded));
        return Task.FromResult(true);
    }
}
