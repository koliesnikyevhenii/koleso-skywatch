using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Pipeline;

// Spec F10.2–F10.5 and the plan P6a press rules: a press counts only from TargetChatId by a user in
// LabelerUserIds. SQLite in-memory, FakeNotificationSender, fake clock. Fake ids only.
public sealed class LabelServiceTests : IDisposable
{
    private const long _targetChatId = -1000000000777;
    private const long _foreignChatId = -1000000000778;
    private const long _labeler = 1001;
    private const long _otherLabeler = 1002;
    private const long _stranger = 2001;
    private const int _botMessageId = 555;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDb _db = new();
    private readonly FakeNotificationSender _sender = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly LabelService _labels;
    private readonly long _notificationId;
    private int _nextCallbackId;

    public LabelServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        _labels = new LabelService(
            _db,
            _sender,
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions { Enabled = true, TargetChatId = _targetChatId, LabelerUserIds = [_labeler, _otherLabeler] }),
            _time,
            _loggerFactory.CreateLogger<LabelService>());
        _notificationId = AddNotification(NotificationKind.New);
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long AddNotification(NotificationKind kind)
    {
        using var db = _db.CreateDbContext();
        var source = db.Sources.FirstOrDefault() ?? new Source { TelegramChatId = -1000000000042, Name = "Test source A", Type = SourceType.Channel, IsEnabled = true, CreatedAtUtc = DateTime.UnixEpoch };
        var id = db.Messages.Count() + 1;
        var notification = new Notification
        {
            Message = new Message
            {
                Source = source,
                TelegramMessageId = id,
                PublishedAtUtc = DateTime.UnixEpoch,
                ReceivedAtUtc = DateTime.UnixEpoch,
                ContentHash = $"hash-{id}",
                OriginChatId = source.TelegramChatId,
                OriginMessageId = id,
                IsMatched = true,
            },
            Kind = kind,
            Status = NotificationStatus.Sent,
            BotMessageId = _botMessageId,
            CreatedAtUtc = DateTime.UnixEpoch,
        };
        db.Notifications.Add(notification);
        db.SaveChanges();
        return notification.Id;
    }

    private LabelPress Press(string? data, long userId = _labeler, long? chatId = _targetChatId) =>
        new($"cb-{++_nextCallbackId}", data, userId, chatId, _botMessageId);

    private string Data(NotificationLabel label, long? notificationId = null) => LabelButtons.Data(notificationId ?? _notificationId, label);

    private async Task<Notification> RowAsync(long? id = null)
    {
        await using var db = _db.CreateDbContext();
        return await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == (id ?? _notificationId), Ct);
    }

    private async Task NothingStoredAsync()
    {
        var row = await RowAsync();
        row.Label.ShouldBeNull();
        row.LabeledByUserId.ShouldBeNull();
        row.LabeledAtUtc.ShouldBeNull();
        _sender.Markups.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("lbl:42:u", 42, nameof(NotificationLabel.Useful))]
    [InlineData("lbl:42:f", 42, nameof(NotificationLabel.False))]
    [InlineData("lbl:42:d", 42, nameof(NotificationLabel.Duplicate))]
    [InlineData("lbl:9007199254740993:u", 9007199254740993, nameof(NotificationLabel.Useful))]
    public void AC_F10_5_valid_callback_data_is_parsed(string data, long expectedId, string expectedLabel)
    {
        LabelButtons.TryParse(data, out var id, out var label).ShouldBeTrue();
        id.ShouldBe(expectedId);
        label.ShouldBe(Enum.Parse<NotificationLabel>(expectedLabel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lbl")]
    [InlineData("lbl:42")]
    [InlineData("lbl:42:")]
    [InlineData("lbl:42:x")]
    [InlineData("lbl:42:U")]
    [InlineData("lbl:42:uu")]
    [InlineData("lbl:42:u:extra")]
    [InlineData("lbl::u")]
    [InlineData("lbl:abc:u")]
    [InlineData("lbl:0:u")]
    [InlineData("lbl:-1:u")]
    [InlineData("lbl:+42:u")]
    [InlineData("lbl: 42:u")]
    [InlineData("lbl:99999999999999999999:u")]
    [InlineData("LBL:42:u")]
    [InlineData("xyz:42:u")]
    public void AC_F10_5_malformed_callback_data_is_rejected(string? data) =>
        LabelButtons.TryParse(data, out _, out _).ShouldBeFalse();

    [Theory]
    [InlineData(nameof(NotificationLabel.Useful))]
    [InlineData(nameof(NotificationLabel.False))]
    [InlineData(nameof(NotificationLabel.Duplicate))]
    public void AC_F10_5_button_data_is_lbl_id_code_and_parses_back(string label)
    {
        var expected = Enum.Parse<NotificationLabel>(label);
        var data = LabelButtons.Data(42, expected);

        data.ShouldBe($"lbl:42:{char.ToLowerInvariant(label[0])}");
        LabelButtons.TryParse(data, out var id, out var parsed).ShouldBeTrue();
        (id, parsed).ShouldBe((42L, expected));
    }

    // AC-F10.1 texts; AC-F10.2: only the chosen button is marked with ✔.
    [Fact]
    public void AC_F10_2_button_texts_mark_only_the_chosen_label()
    {
        LabelButtons.All.Select(l => LabelButtons.Text(l, chosen: null)).ShouldBe(["✅ Корисно", "❌ Хибне", "🔁 Дубль"]);
        LabelButtons.All.Select(l => LabelButtons.Text(l, NotificationLabel.False)).ShouldBe(["✅ Корисно", "✔ ❌ Хибне", "🔁 Дубль"]);
    }

    // AC-F10.2: label, user and time are stored, the press is answered and the buttons re-rendered with the choice.
    [Fact]
    public async Task AC_F10_2_labeler_press_stores_label_answers_and_marks_the_button()
    {
        var outcome = await _labels.HandleAsync(Press(Data(NotificationLabel.Useful)), Ct);

        outcome.ShouldBe(LabelOutcome.Labeled);
        var row = await RowAsync();
        row.Label.ShouldBe(NotificationLabel.Useful);
        row.LabeledByUserId.ShouldBe(_labeler);
        row.LabeledAtUtc.ShouldBe(_time.GetUtcNow().UtcDateTime);
        _sender.Answers.ShouldHaveSingleItem().ShouldBe(("cb-1", LabelService.SavedAnswer));
        _sender.Markups.ShouldHaveSingleItem().ShouldBe((_botMessageId, _notificationId, NotificationLabel.Useful));
    }

    // AC-F10.3: a later press with another label, by the same or another labeler, overwrites; the latest wins.
    [Fact]
    public async Task AC_F10_3_a_different_press_overwrites_the_label()
    {
        await _labels.HandleAsync(Press(Data(NotificationLabel.Useful)), Ct);
        _time.Advance(TimeSpan.FromMinutes(3));

        (await _labels.HandleAsync(Press(Data(NotificationLabel.Duplicate), _otherLabeler), Ct)).ShouldBe(LabelOutcome.Labeled);

        var row = await RowAsync();
        row.Label.ShouldBe(NotificationLabel.Duplicate);
        row.LabeledByUserId.ShouldBe(_otherLabeler);
        row.LabeledAtUtc.ShouldBe(_time.GetUtcNow().UtcDateTime);
        _sender.Markups.Last().Label.ShouldBe(NotificationLabel.Duplicate);
    }

    // AC-F10.4: a user outside LabelerUserIds changes nothing and gets "Немає прав".
    [Fact]
    public async Task AC_F10_4_press_by_a_non_labeler_changes_nothing_and_answers_no_rights()
    {
        (await _labels.HandleAsync(Press(Data(NotificationLabel.False), _stranger), Ct)).ShouldBe(LabelOutcome.Unauthorized);

        await NothingStoredAsync();
        _sender.Answers.ShouldHaveSingleItem().Text.ShouldBe("Немає прав");
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains(_stranger.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    // AC-F10.4: a non-labeler with malformed data still gets "Немає прав", not a hint about the data format.
    [Fact]
    public async Task AC_F10_4_non_labeler_with_malformed_data_answers_no_rights()
    {
        (await _labels.HandleAsync(Press("garbage", _stranger), Ct)).ShouldBe(LabelOutcome.Unauthorized);

        await NothingStoredAsync();
        _sender.Answers.ShouldHaveSingleItem().Text.ShouldBe(LabelService.UnauthorizedAnswer);
    }

    // Plan P6a: a labeler pressing in another chat (the message was forwarded there) → nothing stored, Warning with the chat id.
    [Theory]
    [InlineData(_foreignChatId)]
    [InlineData(null)]
    public async Task AC_F10_4_labeler_press_from_a_foreign_chat_changes_nothing(long? chatId)
    {
        (await _labels.HandleAsync(Press(Data(NotificationLabel.Useful), chatId: chatId), Ct)).ShouldBe(LabelOutcome.Unauthorized);

        await NothingStoredAsync();
        _sender.Answers.ShouldHaveSingleItem().Text.ShouldBe(LabelService.UnauthorizedAnswer);
        var warning = _logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        if (chatId is { } id)
        {
            warning.Message.ShouldContain(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // AC-F10.5: malformed data is answered and logged at Warning, without throwing.
    [Theory]
    [InlineData(null)]
    [InlineData("lbl:42")]
    [InlineData("lbl:abc:u")]
    [InlineData("other:1:u")]
    public async Task AC_F10_5_malformed_data_is_answered_and_logged_without_throwing(string? data)
    {
        (await _labels.HandleAsync(Press(data), Ct)).ShouldBe(LabelOutcome.Malformed);

        await NothingStoredAsync();
        _sender.Answers.ShouldHaveSingleItem().Text.ShouldBe(LabelService.MalformedAnswer);
        _logs.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    // AC-F10.5: an unknown notification id (or an edit reply, which carries no buttons) is answered and logged at Warning.
    [Fact]
    public async Task AC_F10_5_unknown_notification_id_is_answered_and_logged()
    {
        var reply = AddNotification(NotificationKind.EditReply);

        (await _labels.HandleAsync(Press(Data(NotificationLabel.Useful, 9999)), Ct)).ShouldBe(LabelOutcome.UnknownNotification);
        (await _labels.HandleAsync(Press(Data(NotificationLabel.Useful, reply)), Ct)).ShouldBe(LabelOutcome.UnknownNotification);

        (await RowAsync(reply)).Label.ShouldBeNull();
        await NothingStoredAsync();
        _sender.Answers.Select(a => a.Text).ShouldBe([LabelService.UnknownAnswer, LabelService.UnknownAnswer]);
        _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("unknown notification")).ShouldBe(2);
    }

    // AC-F10.1 for EditNewMatch: its buttons label like those of a New notification.
    [Fact]
    public async Task AC_F10_2_edit_new_match_notification_can_be_labeled()
    {
        var edit = AddNotification(NotificationKind.EditNewMatch);

        (await _labels.HandleAsync(Press(Data(NotificationLabel.False, edit)), Ct)).ShouldBe(LabelOutcome.Labeled);

        (await RowAsync(edit)).Label.ShouldBe(NotificationLabel.False);
    }

    // AC-F10.2: the label is stored even when re-rendering the buttons fails; the failure is a Warning.
    [Fact]
    public async Task AC_F10_2_failed_markup_edit_keeps_the_label_and_logs_a_warning()
    {
        _sender.MarkupResult = new SendResult.Fatal("Bad Request: message to edit not found");

        (await _labels.HandleAsync(Press(Data(NotificationLabel.Useful)), Ct)).ShouldBe(LabelOutcome.Labeled);

        (await RowAsync()).Label.ShouldBe(NotificationLabel.Useful);
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("message to edit not found"));
    }
}
