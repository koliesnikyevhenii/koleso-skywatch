using System.Net;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Skywatch.Worker.Tests.Telegram;

// Plan P4a send gate and the SendResult mapping behind AC-F8.6–F8.10. Fake ids and a fake token only.
public sealed class BotNotificationSenderTests : IDisposable
{
    private const long _targetChatId = -1000000000777;
    private const string _fakeToken = "123456:fake-bot-token-do-not-log";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTelegramBotClient _bot = new();
    private readonly BotNotificationSender _sender;

    public BotNotificationSenderTests() =>
        _sender = new BotNotificationSender(
            _bot,
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = true, BotToken = _fakeToken, TargetChatId = _targetChatId }),
            _time);

    public void Dispose() => _sender.Dispose();

    private static OutgoingNotification Alert(string text = "alert") => new(text, SendPriority.Alert);

    private static OutgoingNotification Summary(string text = "summary") => new(text, SendPriority.Background);

    private Task<SendResult> SendAsync(OutgoingNotification notification) => _sender.SendAsync(notification, TestContext.Current.CancellationToken);

    private static ApiRequestException TooManyRequests(int retryAfterSeconds) =>
        new($"Too Many Requests: retry after {retryAfterSeconds}", 429, new ResponseParameters { RetryAfter = retryAfterSeconds });

    // C2 + AC-F8.2: every send goes to TargetChatId as plain text without a parse mode; success carries the bot
    // message id (AC-F8.10).
    [Fact]
    public async Task AC_F8_2_send_goes_to_the_target_chat_as_plain_text()
    {
        var result = await SendAsync(Alert("⚠️ text with *stars* and <tags>"));

        var request = _bot.Requests.ShouldHaveSingleItem();
        request.ChatId.Identifier.ShouldBe(_targetChatId);
        request.Text.ShouldBe("⚠️ text with *stars* and <tags>");
        request.ParseMode.ShouldBe(ParseMode.None);
        request.ReplyParameters.ShouldBeNull();
        result.ShouldBeOfType<SendResult.Success>().BotMessageId.ShouldBe(101);
    }

    [Fact]
    public async Task P4a_reply_is_sent_as_a_reply_to_the_given_bot_message_in_the_target_chat()
    {
        await _sender.ReplyAsync(555, Alert(), TestContext.Current.CancellationToken);

        var request = _bot.Requests.ShouldHaveSingleItem();
        request.ChatId.Identifier.ShouldBe(_targetChatId);
        request.ReplyParameters.ShouldNotBeNull().MessageId.ShouldBe(555);
    }

    // Plan P4a send gate + AC-F8.6: after a 429 with retry_after, a later summary send waits until then.
    [Fact]
    public async Task AC_F8_6_after_a_429_every_send_waits_until_retry_after()
    {
        _bot.Throw(TooManyRequests(30));

        (await SendAsync(Alert())).ShouldBe(new SendResult.RateLimited(TimeSpan.FromSeconds(30)));

        var summary = SendAsync(Summary());
        _time.Advance(TimeSpan.FromSeconds(29));
        summary.IsCompleted.ShouldBeFalse();
        _bot.Requests.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        (await summary).ShouldBeOfType<SendResult.Success>();
        _bot.Requests.Last().Text.ShouldBe("summary");
    }

    // Plan P4a send gate: an alert and a report ready at the same time → the alert is sent first, even when the
    // report started waiting earlier.
    [Fact]
    public async Task P4a_alert_waiting_together_with_a_report_is_sent_first()
    {
        var hold = _bot.Hold();
        var inFlight = SendAsync(Alert("first"));
        await Eventually.ShouldHappen(() => _bot.Requests.Count == 1, "first send in flight");

        var report = SendAsync(Summary("report"));
        var alert = SendAsync(Alert("alert"));
        hold.SetResult();
        await Task.WhenAll(inFlight, report, alert);

        _bot.Requests.Select(r => r.Text).ShouldBe(["first", "alert", "report"]);
    }

    // Plan P4a send gate: sends are serialized; a second alert waits for the one in flight.
    [Fact]
    public async Task P4a_sends_are_serialized()
    {
        var hold = _bot.Hold();
        var first = SendAsync(Alert("first"));
        await Eventually.ShouldHappen(() => _bot.Requests.Count == 1, "first send in flight");

        var second = SendAsync(Alert("second"));
        _bot.Requests.Count.ShouldBe(1);

        hold.SetResult();
        await Task.WhenAll(first, second);
        _bot.Requests.Count.ShouldBe(2);
    }

    // AC-F8.7: 5xx is transient; AC-F8.8: other 4xx are fatal. Stored errors never contain the token (C5).
    [Theory]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(400, true)]
    [InlineData(403, true)]
    public async Task AC_F8_7_server_errors_are_transient_and_AC_F8_8_client_errors_are_fatal(int status, bool fatal)
    {
        _bot.Throw(new ApiRequestException($"Error from bot{_fakeToken}/sendMessage", status));

        var result = await SendAsync(Alert());

        string error;
        if (fatal)
        {
            error = result.ShouldBeOfType<SendResult.Fatal>().Error;
        }
        else
        {
            var transient = result.ShouldBeOfType<SendResult.Transient>();
            transient.DeliveryUnknown.ShouldBeFalse();
            error = transient.Error;
        }

        error.ShouldNotContain(_fakeToken);
    }

    // AC-F8.7 network error (not delivered) vs AC-F8.9 timeout (delivery unknown).
    [Fact]
    public async Task AC_F8_9_timeout_is_transient_with_delivery_unknown()
    {
        _bot.Throw(new RequestException("Request timed out", new TaskCanceledException()));

        (await SendAsync(Alert())).ShouldBeOfType<SendResult.Transient>().DeliveryUnknown.ShouldBeTrue();
    }

    [Fact]
    public async Task AC_F8_7_network_error_is_transient_and_the_token_is_redacted()
    {
        _bot.Throw(new RequestException("Exception during making request", new HttpRequestException($"No connection to https://api.telegram.org/bot{_fakeToken}/sendMessage", null, HttpStatusCode.BadGateway)));

        var transient = (await SendAsync(Alert())).ShouldBeOfType<SendResult.Transient>();

        transient.DeliveryUnknown.ShouldBeFalse();
        transient.Error.ShouldContain("No connection");
        transient.Error.ShouldNotContain(_fakeToken);
    }
}
