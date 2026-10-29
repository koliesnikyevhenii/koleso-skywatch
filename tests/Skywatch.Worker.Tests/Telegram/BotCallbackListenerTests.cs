using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;

namespace Skywatch.Worker.Tests.Telegram;

// Spec AC-F10.6 and plan R13: callbacks arrive by long polling (callback_query only) in a hosted service, with the
// same token the sender uses. FakeTelegramBotClient, LabelService on SQLite in-memory, fake clock. Fake ids and a
// fake token only.
public sealed class BotCallbackListenerTests : IDisposable
{
    private const long _targetChatId = -1000000000777;
    private const long _labeler = 1001;
    private const string _fakeToken = "123456:fake-bot-token-do-not-log";

    private readonly TimerCountingTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new(concurrent: true);
    private readonly FakeTelegramBotClient _bot = new();
    private readonly FakeNotificationSender _sender = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly BotCallbackListener _listener;
    private readonly long _notificationId;

    public BotCallbackListenerTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = true, BotToken = _fakeToken, TargetChatId = _targetChatId, LabelerUserIds = [_labeler] });
        var labels = new LabelService(_db, _sender, options, _time, _loggerFactory.CreateLogger<LabelService>());
        _listener = new BotCallbackListener(_bot, labels, options, _time, _loggerFactory.CreateLogger<BotCallbackListener>());

        using var db = _db.CreateDbContext();
        var notification = new Notification
        {
            Message = new Skywatch.Worker.Persistence.Entities.Message
            {
                Source = new Source { TelegramChatId = -1000000000042, Name = "Test source A", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch },
                TelegramMessageId = 1,
                PublishedAtUtc = DateTime.UnixEpoch,
                ReceivedAtUtc = DateTime.UnixEpoch,
                ContentHash = "hash-1",
                OriginChatId = -1000000000042,
                OriginMessageId = 1,
                IsMatched = true,
            },
            Kind = NotificationKind.New,
            Status = NotificationStatus.Sent,
            BotMessageId = 555,
            CreatedAtUtc = DateTime.UnixEpoch,
        };
        db.Notifications.Add(notification);
        db.SaveChanges();
        _notificationId = notification.Id;
    }

    public void Dispose()
    {
        _listener.Dispose();
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Update Press(int updateId, string data) => new()
    {
        Id = updateId,
        CallbackQuery = new CallbackQuery
        {
            Id = $"cb-{updateId}",
            Data = data,
            From = new User { Id = _labeler, FirstName = "Labeler" },
            Message = new Message { Id = 555, Chat = new Chat { Id = _targetChatId, Type = ChatType.Supergroup } },
        },
    };

    private async Task<NotificationLabel?> LabelAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.Where(n => n.Id == _notificationId).Select(n => n.Label).SingleAsync(Ct);
    }

    private Task PollsAsync(int count) => Eventually.ShouldHappen(() => _bot.Polls.Count >= count, $"{count} polls");

    // AC-F10.6: getUpdates long-polls for callback_query only; a press is labeled and answered; every next poll
    // confirms the updates before it; other updates are skipped.
    [Fact]
    public async Task AC_F10_6_callback_presses_arrive_by_long_polling_and_are_labeled()
    {
        await _listener.StartAsync(Ct);
        await PollsAsync(1);

        _bot.EmitUpdates(new Update { Id = 7, Message = new Message { Id = 1, Chat = new Chat { Id = _targetChatId } } }, Press(8, $"lbl:{_notificationId}:u"));
        await PollsAsync(2);
        (await LabelAsync()).ShouldBe(NotificationLabel.Useful);
        _sender.Answers.ShouldHaveSingleItem().ShouldBe(("cb-8", LabelService.SavedAnswer));

        _bot.EmitUpdates(Press(9, $"lbl:{_notificationId}:f"));
        await PollsAsync(3);
        (await LabelAsync()).ShouldBe(NotificationLabel.False);

        var polls = _bot.Polls.ToArray();
        polls.ShouldAllBe(p => p.Timeout == BotCallbackListener.PollTimeoutSeconds);
        polls.ShouldAllBe(p => p.AllowedUpdates!.SequenceEqual(new[] { UpdateType.CallbackQuery }));
        polls.Select(p => p.Offset).ShouldBe([null, 9, 10]);
        await _listener.StopAsync(Ct);
    }

    // AC-F10.5 through the listener: a malformed press is answered and polling goes on.
    [Fact]
    public async Task AC_F10_5_malformed_press_is_answered_and_polling_continues()
    {
        await _listener.StartAsync(Ct);
        _bot.EmitUpdates(Press(1, "lbl:oops"));
        await PollsAsync(2);

        _sender.Answers.ShouldHaveSingleItem().Text.ShouldBe(LabelService.MalformedAnswer);
        (await LabelAsync()).ShouldBeNull();
        await _listener.StopAsync(Ct);
    }

    // Plan R13: a failing getUpdates (a webhook or a second poller with the token → 409) is logged without the token
    // and retried after 5 s, then 10 s; a successful poll resets the backoff.
    [Fact]
    public async Task AC_F10_6_failed_poll_is_retried_with_backoff_and_the_token_is_redacted()
    {
        _bot.FailPoll(new RequestException($"Exception during making request to https://api.telegram.org/bot{_fakeToken}/getUpdates"));
        _bot.FailPoll(new ApiRequestException("Conflict: terminated by other getUpdates request", 409));
        await _listener.StartAsync(Ct);

        await PollsAsync(1);
        await _time.TimersCreatedAtLeast(1);
        _time.Advance(BotCallbackListener.FirstBackoff - TimeSpan.FromTicks(1));
        _bot.Polls.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromTicks(1));
        await PollsAsync(2);

        await _time.TimersCreatedAtLeast(2);
        _time.Advance(BotCallbackListener.FirstBackoff * 2 - TimeSpan.FromTicks(1));
        _bot.Polls.Count.ShouldBe(2);
        _time.Advance(TimeSpan.FromTicks(1));
        await PollsAsync(3);

        var warnings = _logs.Entries.Where(e => e.Category.EndsWith(nameof(BotCallbackListener), StringComparison.Ordinal)).ToList();
        warnings.Count.ShouldBe(2);
        warnings.ShouldAllBe(e => e.Level == LogLevel.Warning);
        warnings.ShouldAllBe(e => !e.Message.Contains(_fakeToken));
        warnings[1].Message.ShouldContain("Conflict");
        await _listener.StopAsync(Ct);
    }

    // AC-F10.6: the run-mode host with notifications enabled polls for callbacks; with notifications disabled it
    // does not (no bot is used at all).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AC_F10_6_host_polls_for_callbacks_only_when_notifications_are_enabled(bool enabled)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source A"));
        settings["Notifications:Enabled"] = enabled ? "true" : "false";
        settings["Notifications:BotToken"] = _fakeToken;
        settings["Notifications:TargetChatId"] = "-1000000000777";
        var bot = new FakeTelegramBotClient();
        using var host = TestHost.Build([], settings, bot: bot);

        host.Services.GetServices<IHostedService>().OfType<BotCallbackListener>().Count().ShouldBe(enabled ? 1 : 0);
        if (enabled)
        {
            await host.StartAsync(Ct);
            await Eventually.ShouldHappen(() => !bot.Polls.IsEmpty, "callback poll");
            await host.StopAsync(Ct);
        }
    }
}
