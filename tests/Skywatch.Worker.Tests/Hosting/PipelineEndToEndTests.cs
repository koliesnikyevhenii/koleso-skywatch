using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Core.Deduplication;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Plan P4b end-to-end: the run-mode host with notifications enabled, FakeUpdateSource → MessageProcessor →
// outbox → NotificationDispatchService → FakeNotificationSender. Fake ids and a fake token only.
public sealed class PipelineEndToEndTests : IDisposable
{
    private const long _sourceA = -1000000000042;
    private const long _sourceB = -1000000000043;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeNotificationSender _sender = new();
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();

    public void Dispose() => _session.Dispose();

    private IncomingMessage Message(long chatId, int id, string text, double ageSeconds = 2, OriginKey? origin = null)
    {
        var received = _time.GetUtcNow().UtcDateTime;
        return new IncomingMessage(chatId, id, text, received.AddSeconds(-ageSeconds), received, origin ?? new OriginKey(chatId, id), IsEdit: false, EditedAtUtc: null);
    }

    // AC-F6.5, AC-F7.1, AC-F8.1, AC-F8.10: a match is sent; its forward to another source, a stale match and a
    // non-match are not. The last match acts as a barrier: it is sent only after everything before it was handled.
    [Fact]
    public async Task P4b_match_is_sent_and_duplicate_stale_and_unmatched_messages_are_not()
    {
        var settings = TestHost.RunModeSettings(_session.Path, (_sourceA, "Test source A"), (_sourceB, "Test source B"));
        settings["Notifications:Enabled"] = "true";
        settings["Notifications:BotToken"] = "123456:fake-bot-token-do-not-log";
        settings["Notifications:TargetChatId"] = "-1000000000777";
        var source = new FakeUpdateSource
        {
            Sources =
            [
                new ResolvedSource(_sourceA, "Test source A", SourceType.Channel, "test_a", NoForwards: false, Resolved: true),
                new ResolvedSource(_sourceB, "Test source B", SourceType.Channel, null, NoForwards: false, Resolved: true),
            ],
        };
        using var host = TestHost.Build([], settings, source: source, time: _time, sender: _sender);
        await host.StartAsync(TestContext.Current.CancellationToken);

        source.Emit(Message(_sourceA, 1, "тест перший"));
        source.Emit(Message(_sourceB, 2, "тест перший", origin: new OriginKey(_sourceA, 1)));
        source.Emit(Message(_sourceA, 3, "тест застарілий", ageSeconds: 300));
        source.Emit(Message(_sourceA, 4, "тиша"));
        source.Emit(Message(_sourceB, 5, "тест другий"));

        await Eventually.ShouldHappen(() => _sender.Sends.Count == 2, "two notifications sent");

        _sender.Texts[0].ShouldContain("тест перший");
        _sender.Texts[0].ShouldContain("https://t.me/test_a/1");
        _sender.Texts[1].ShouldContain("тест другий");
        await Eventually.ShouldHappenAsync(
            async () =>
            {
                await using var db = SqliteTestDb.OpenFile(settings["Storage:DatabasePath"]!);
                return !await db.Notifications.AnyAsync(n => n.Status == NotificationStatus.Pending, TestContext.Current.CancellationToken);
            },
            "no notification pending");
        await using (var db = SqliteTestDb.OpenFile(settings["Storage:DatabasePath"]!))
        {
            var statuses = await db.Notifications.OrderBy(n => n.Id).Select(n => n.Status).ToListAsync(TestContext.Current.CancellationToken);
            statuses.ShouldBe([NotificationStatus.Sent, NotificationStatus.SkippedDuplicate, NotificationStatus.SkippedStale, NotificationStatus.Sent]);
            (await db.Messages.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(5);
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
        _sender.Sends.Count.ShouldBe(2);
    }
}
