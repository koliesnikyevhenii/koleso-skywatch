using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Core.Deduplication;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Spec N4 and plan P7 (graceful shutdown, `docker compose restart`): the run-mode host with notifications enabled,
// FakeUpdateSource holding queued messages until the stop, FakeNotificationSender and a fake clock. Fake ids only.
public sealed class ShutdownTests : IDisposable
{
    private const long _sourceA = -1000000000042;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();
    private readonly Dictionary<string, string?> _settings;

    public ShutdownTests()
    {
        _settings = TestHost.RunModeSettings(_session.Path, (_sourceA, "Test source A"));
        _settings["Notifications:Enabled"] = "true";
        _settings["Notifications:BotToken"] = "123456:fake-bot-token-do-not-log";
        _settings["Notifications:TargetChatId"] = "-1000000000777";
        _settings["Reporting:DailyReportEnabled"] = "false";
    }

    public void Dispose() => _session.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeUpdateSource Source(bool holdUntilStopped = false) => new()
    {
        Sources = [new ResolvedSource(_sourceA, "Test source A", SourceType.Channel, null, NoForwards: false, Resolved: true)],
        HoldUntilStopped = holdUntilStopped,
    };

    private IncomingMessage Message(int id)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new IncomingMessage(_sourceA, id, $"тест {id}", now.AddSeconds(-1), now, new OriginKey(_sourceA, id), IsEdit: false, EditedAtUtc: null);
    }

    private async Task<List<NotificationStatus>> StatusesAsync()
    {
        await using var db = SqliteTestDb.OpenFile(_settings["Storage:DatabasePath"]!);
        return await db.Notifications.OrderBy(n => n.Id).Select(n => n.Status).ToListAsync(Ct);
    }

    // N4: the stop waits up to 30 s (Docker's stop_grace_period is longer).
    [Fact]
    public void N4_shutdown_timeout_is_30_seconds()
    {
        using var host = TestHost.Build([], _settings, source: Source(), time: _time, sender: new FakeNotificationSender());

        host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    // N4: messages still queued when the stop is requested are processed into the outbox before the Stopped event;
    // each of their notifications is either sent or still Pending.
    [Fact]
    public async Task N4_queued_messages_are_processed_on_stop_and_stopped_is_recorded_last()
    {
        var source = Source(holdUntilStopped: true);
        using var host = TestHost.Build([], _settings, source: source, time: _time, sender: new FakeNotificationSender());
        await host.StartAsync(Ct);

        // .NET 10 runs ExecuteAsync in the background: a stop before it begins would skip the service entirely,
        // which is not the scenario here (messages queued while the service runs).
        await Eventually.ShouldHappen(() => source.ResolveCalls == 1, "the listener connected");
        for (var i = 1; i <= 5; i++)
        {
            source.Emit(Message(i));
        }

        await host.StopAsync(Ct);

        await using var db = SqliteTestDb.OpenFile(_settings["Storage:DatabasePath"]!);
        (await db.Messages.CountAsync(Ct)).ShouldBe(5);
        (await StatusesAsync()).ShouldAllBe(s => s == NotificationStatus.Sent || s == NotificationStatus.Pending);
        (await StatusesAsync()).Count.ShouldBe(5);
        var events = await db.ServiceEvents.OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync(Ct);
        events.ShouldBe([ServiceEventKind.Started, ServiceEventKind.Stopped]);
    }

    // Plan P7 criterion 2: a notification still Pending at the stop (here: a send that keeps failing transiently)
    // survives the restart and is sent by the next process.
    [Fact]
    public async Task P7_pending_notification_survives_a_restart_and_is_sent_afterwards()
    {
        var failing = new FakeNotificationSender();
        failing.Script(new SendResult.Transient("timeout", DeliveryUnknown: false));
        var source = Source();
        using (var host = TestHost.Build([], _settings, source: source, time: _time, sender: failing))
        {
            await host.StartAsync(Ct);
            source.Emit(Message(1));
            await Eventually.ShouldHappen(() => failing.Sends.Count == 1, "first attempt");
            await host.StopAsync(Ct);
        }

        (await StatusesAsync()).ShouldBe([NotificationStatus.Pending]);

        var sender = new FakeNotificationSender();
        using (var restarted = TestHost.Build([], _settings, source: Source(), time: _time, sender: sender))
        {
            await restarted.StartAsync(Ct);
            await Eventually.ShouldHappenAsync(async () => (await StatusesAsync()).SequenceEqual([NotificationStatus.Sent]), "sent after the restart");
            await restarted.StopAsync(Ct);
        }

        sender.Sends.ShouldHaveSingleItem().Notification.Text.ShouldContain("тест 1");
    }
}
