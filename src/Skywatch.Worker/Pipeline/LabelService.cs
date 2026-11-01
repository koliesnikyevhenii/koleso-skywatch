using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Pipeline;

internal enum LabelOutcome
{
    /// <summary>The label is stored; a later press overwrites it (AC-F10.3).</summary>
    Labeled,

    /// <summary>AC-F10.4: the presser is not a labeler, or the press came from another chat.</summary>
    Unauthorized,

    /// <summary>AC-F10.5: the callback data is not <c>lbl:{id}:{u|f|d}</c>.</summary>
    Malformed,

    /// <summary>
    /// AC-F10.5/F10.7: no labelable notification has this id (e.g. deleted by retention), or it is not the sent
    /// message that was pressed.
    /// </summary>
    UnknownNotification,

    /// <summary>AC-F10.8: storing the press failed; it is still answered.</summary>
    Failed,

    /// <summary>AC-F10.11: labeling is turned off; a press on an older message changes nothing.</summary>
    Disabled,
}

/// <summary>One button press, mapped from a Telegram callback query by <see cref="BotCallbackListener"/>.</summary>
internal sealed record LabelPress(string CallbackQueryId, string? Data, long UserId, long? ChatId, int? BotMessageId);

/// <summary>AC-F10.1/F10.5: the label buttons and their callback data <c>lbl:{notificationId}:{u|f|d}</c>.</summary>
internal static class LabelButtons
{
    private const string _prefix = "lbl";
    private const string _chosenMark = "✔ ";

    public static IReadOnlyList<NotificationLabel> All { get; } = [NotificationLabel.Useful, NotificationLabel.False, NotificationLabel.Duplicate];

    /// <summary>AC-F10.2: the chosen label is marked with <c>✔</c>.</summary>
    public static string Text(NotificationLabel label, NotificationLabel? chosen) =>
        (label == chosen ? _chosenMark : "") + label switch
        {
            NotificationLabel.Useful => "✅ Корисно",
            NotificationLabel.False => "❌ Хибне",
            NotificationLabel.Duplicate => "🔁 Дубль",
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
        };

    public static string Data(long notificationId, NotificationLabel label) =>
        string.Create(CultureInfo.InvariantCulture, $"{_prefix}:{notificationId}:{Code(label)}");

    public static bool TryParse(string? data, out long notificationId, out NotificationLabel label)
    {
        notificationId = 0;
        label = default;
        var parts = data?.Split(':');
        if (parts is not [_prefix, var id, [var code]]
            || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out notificationId)
            || notificationId <= 0)
        {
            return false;
        }

        NotificationLabel? parsed = code switch
        {
            'u' => NotificationLabel.Useful,
            'f' => NotificationLabel.False,
            'd' => NotificationLabel.Duplicate,
            _ => null,
        };
        label = parsed.GetValueOrDefault();
        return parsed is not null;
    }

    private static char Code(NotificationLabel label) => label switch
    {
        NotificationLabel.Useful => 'u',
        NotificationLabel.False => 'f',
        NotificationLabel.Duplicate => 'd',
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
    };
}

/// <summary>
/// Spec F10: stores a labeler's verdict on a sent match notification, one new context per press. A press counts
/// only from <c>Notifications:TargetChatId</c> by a user in <c>Notifications:LabelerUserIds</c>, or by anyone there
/// with <c>Notifications:LabelingOpenToAll</c> (AC-F10.10).
/// </summary>
internal sealed partial class LabelService(
    IDbContextFactory<SkywatchDbContext> dbFactory,
    INotificationSender sender,
    IOptions<NotificationOptions> options,
    TimeProvider time,
    ILogger<LabelService> logger)
{
    public const string SavedAnswer = "Збережено";
    public const string UnauthorizedAnswer = "Немає прав";
    public const string MalformedAnswer = "Невідома дія";
    public const string UnknownAnswer = "Сповіщення не знайдено";
    public const string FailedAnswer = "Помилка, спробуйте ще раз";
    public const string DisabledAnswer = "Позначки вимкнено";

    /// <summary>AC-F10.2–F10.5: never throws for bad input; every press is answered.</summary>
    public async Task<LabelOutcome> HandleAsync(LabelPress press, CancellationToken ct)
    {
        LabelOutcome outcome;
        try
        {
            outcome = await DecideAsync(press, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AC-F10.8: the labeler is told, instead of a button that keeps spinning.
            LogStoreFailed(logger, ex, press.UserId);
            outcome = LabelOutcome.Failed;
        }

        await AnswerAsync(press, outcome, ct);
        if (outcome == LabelOutcome.Labeled
            && press.BotMessageId is { } botMessageId
            && LabelButtons.TryParse(press.Data, out var notificationId, out var label))
        {
            var markup = await sender.SetLabelMarkupAsync(botMessageId, notificationId, label, ct);
            if (markup is not SendResult.Success)
            {
                LogMarkupFailed(logger, notificationId, botMessageId, markup);
            }
        }

        return outcome;
    }

    private async Task<LabelOutcome> DecideAsync(LabelPress press, CancellationToken ct)
    {
        if (!options.Value.LabelingEnabled)
        {
            return LabelOutcome.Disabled;
        }

        if (press.ChatId != options.Value.TargetChatId)
        {
            LogForeignChat(logger, press.UserId, press.ChatId);
            return LabelOutcome.Unauthorized;
        }

        if (!LabelButtons.TryParse(press.Data, out var notificationId, out var label))
        {
            LogMalformed(logger, press.UserId, press.ChatId, press.Data);
            return options.Value.CanLabel(press.UserId) ? LabelOutcome.Malformed : LabelOutcome.Unauthorized;
        }

        return await ApplyAsync(notificationId, label, press.UserId, press.BotMessageId, ct);
    }

    /// <summary>
    /// AC-F10.2–F10.4: the latest press by a labeler wins. AC-F10.7: only on the sent message that carries the
    /// buttons, so callback data copied from another message cannot label a notification that was never sent.
    /// </summary>
    public async Task<LabelOutcome> ApplyAsync(long notificationId, NotificationLabel label, long userId, int? botMessageId, CancellationToken ct)
    {
        if (!options.Value.CanLabel(userId))
        {
            LogUnauthorized(logger, userId, notificationId);
            return LabelOutcome.Unauthorized;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var notification = await db.Notifications.SingleOrDefaultAsync(
            n => n.Id == notificationId
                && n.Kind != NotificationKind.EditReply
                && n.Status == NotificationStatus.Sent
                && n.BotMessageId != null
                && n.BotMessageId == botMessageId,
            ct);
        if (notification is null)
        {
            LogUnknown(logger, userId, notificationId);
            return LabelOutcome.UnknownNotification;
        }

        notification.Label = label;
        notification.LabeledByUserId = userId;
        notification.LabeledAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        LogLabeled(logger, notificationId, label, userId);
        return LabelOutcome.Labeled;
    }

    private async Task AnswerAsync(LabelPress press, LabelOutcome outcome, CancellationToken ct)
    {
        var text = outcome switch
        {
            LabelOutcome.Labeled => SavedAnswer,
            LabelOutcome.Unauthorized => UnauthorizedAnswer,
            LabelOutcome.Malformed => MalformedAnswer,
            LabelOutcome.Failed => FailedAnswer,
            LabelOutcome.Disabled => DisabledAnswer,
            _ => UnknownAnswer,
        };
        var result = await sender.AnswerCallbackAsync(press.CallbackQueryId, text, ct);
        if (result is not SendResult.Success)
        {
            LogAnswerFailed(logger, press.UserId, result);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} labeled {Label} by user {UserId}.")]
    private static partial void LogLabeled(ILogger logger, long notificationId, NotificationLabel label, long userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label press by user {UserId} from chat {ChatId}, which is not the target chat; ignored.")]
    private static partial void LogForeignChat(ILogger logger, long userId, long? chatId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label press by user {UserId} in chat {ChatId} has malformed callback data '{Data}'; ignored.")]
    private static partial void LogMalformed(ILogger logger, long userId, long? chatId, string? data);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label press by user {UserId}, who is not a labeler, on notification {NotificationId}; ignored.")]
    private static partial void LogUnauthorized(ILogger logger, long userId, long notificationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Label press by user {UserId} on unknown notification {NotificationId}; ignored.")]
    private static partial void LogUnknown(ILogger logger, long userId, long notificationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storing the label press by user {UserId} failed; the press is answered with an error.")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, long userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Answering the label press by user {UserId} failed: {Result}")]
    private static partial void LogAnswerFailed(ILogger logger, long userId, SendResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marking the label on notification {NotificationId} (bot message {BotMessageId}) failed: {Result}")]
    private static partial void LogMarkupFailed(ILogger logger, long notificationId, int botMessageId, SendResult result);
}
