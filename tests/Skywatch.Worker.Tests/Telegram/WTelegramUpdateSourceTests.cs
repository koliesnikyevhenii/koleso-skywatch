using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using TL;

namespace Skywatch.Worker.Tests.Telegram;

// The update handler and the channel it owns, without a Telegram connection. Fake ids only.
public sealed class WTelegramUpdateSourceTests : IDisposable
{
    private const long _enabledChannel = 42;
    private const long _disabledChannel = 43;
    private const long _unknownChannel = 99;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ConcurrentQueue<string> _savedPaths = new();
    private readonly TelegramOptions _telegram = new() { UpdateStatePath = "data/test-updates.state" };

    public void Dispose() => _logs.Dispose();

    private WTelegramUpdateSource CreateSource() => new(
        _telegram,
        new MonitoringOptions
        {
            Sources =
            [
                new SourceOptions { ChatId = TlMapper.ChannelToMarkedId(_enabledChannel), Name = "Enabled source" },
                new SourceOptions { ChatId = TlMapper.ChannelToMarkedId(_disabledChannel), Name = "Disabled source", Enabled = false },
            ],
        },
        _time,
        LoggerFactory.Create(b => b.AddProvider(_logs)).CreateLogger<WTelegramUpdateSource>(),
        _savedPaths.Enqueue);

    private UpdateNewChannelMessage Post(long channelId, int id) => new()
    {
        message = new Message { id = id, peer_id = new PeerChannel { channel_id = channelId }, date = _time.GetUtcNow().UtcDateTime, message = $"Текст {id}" },
    };

    private static async Task<List<SourceEvent>> DrainAsync(WTelegramUpdateSource source)
    {
        // Cancelling the reader stops intake; queued events are still yielded (ITelegramUpdateSource contract).
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        List<SourceEvent> events = [];
        await foreach (var e in source.ReadAllAsync(stop.Token))
        {
            events.Add(e);
        }

        return events;
    }

    // AC-F2.5: updates from chats that are not enabled sources are discarded before normalization.
    [Fact]
    public async Task AC_F2_5_update_from_chat_that_is_not_an_enabled_source_is_discarded()
    {
        var source = CreateSource();

        await source.HandleUpdateAsync(Post(_enabledChannel, 1));
        await source.HandleUpdateAsync(Post(_disabledChannel, 2));
        await source.HandleUpdateAsync(Post(_unknownChannel, 3));

        var events = await DrainAsync(source);
        events.ShouldHaveSingleItem().ShouldBeOfType<IncomingMessage>().MessageId.ShouldBe(1);
    }

    // AC-F3.4: the handler writes into a bounded channel (capacity 1000, full mode = wait) and returns.
    [Fact]
    public async Task AC_F3_4_update_handler_enqueues_into_bounded_channel_of_1000_and_waits_when_full()
    {
        var source = CreateSource();
        for (var i = 1; i <= 1000; i++)
        {
            var write = source.HandleUpdateAsync(Post(_enabledChannel, i));
            write.IsCompleted.ShouldBeTrue($"write {i} should not wait below capacity");
        }

        var overflow = source.HandleUpdateAsync(Post(_enabledChannel, 1001));
        overflow.IsCompleted.ShouldBeFalse("the 1001st write waits for the consumer");

        await using var reader = source.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await reader.MoveNextAsync()).ShouldBeTrue();
        await overflow.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // Plan P1b back-pressure: Warning with the current size when the channel is > 80% full, at most once per minute.
    [Fact]
    public async Task P1b_back_pressure_warns_with_size_above_80_percent_at_most_once_per_minute()
    {
        var source = CreateSource();
        for (var i = 1; i <= 800; i++)
        {
            await source.HandleUpdateAsync(Post(_enabledChannel, i));
        }

        Warnings().ShouldBeEmpty();

        await source.HandleUpdateAsync(Post(_enabledChannel, 801));
        Warnings().ShouldHaveSingleItem().ShouldContain("801");

        for (var i = 802; i <= 850; i++)
        {
            await source.HandleUpdateAsync(Post(_enabledChannel, i));
        }

        Warnings().Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(61));
        await source.HandleUpdateAsync(Post(_enabledChannel, 851));
        Warnings().Count.ShouldBe(2);
        Warnings().Last().ShouldContain("851");
    }

    // Plan P1b save policy: TrySaveStateAsync saves only when the channel is empty, otherwise returns false.
    [Fact]
    public async Task P1b_update_state_is_saved_only_when_the_channel_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = CreateSource();
        await source.HandleUpdateAsync(Post(_enabledChannel, 1));

        (await source.TrySaveStateAsync(ct)).ShouldBeFalse();
        _savedPaths.ShouldBeEmpty();

        await using var reader = source.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        (await reader.MoveNextAsync()).ShouldBeTrue();

        (await source.TrySaveStateAsync(ct)).ShouldBeTrue();
        _savedPaths.ShouldHaveSingleItem().ShouldBe(_telegram.UpdateStatePath);
    }

    // Plan P1b shutdown: stop reading new updates, but queued events still reach the consumer.
    [Fact]
    public async Task P1b_stopping_the_reader_yields_queued_events_then_ends()
    {
        var source = CreateSource();
        await source.HandleUpdateAsync(Post(_enabledChannel, 1));
        await source.HandleUpdateAsync(Post(_enabledChannel, 2));

        var events = await DrainAsync(source);

        events.Cast<IncomingMessage>().Select(m => m.MessageId).ShouldBe([1, 2]);
    }

    // AC-F3.4 receive time comes from TimeProvider at enqueue, so latency is measurable.
    [Fact]
    public async Task AC_F3_4_enqueued_message_carries_receive_time_from_time_provider()
    {
        var source = CreateSource();
        var post = Post(_enabledChannel, 1);
        _time.Advance(TimeSpan.FromSeconds(4));

        await source.HandleUpdateAsync(post);

        var message = (await DrainAsync(source)).ShouldHaveSingleItem().ShouldBeOfType<IncomingMessage>();
        (message.ReceivedAtUtc - message.PublishedAtUtc).ShouldBe(TimeSpan.FromSeconds(4));
    }

    private List<string> Warnings() =>
        [.. _logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
}
