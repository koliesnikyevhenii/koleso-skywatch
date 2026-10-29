using Skywatch.Worker.Persistence.Entities;

namespace Skywatch.Worker.Telegram;

/// <summary>The delivery seam over the bot (D13). Sends only to <c>Notifications:TargetChatId</c> (C2).</summary>
internal interface INotificationSender
{
    Task<SendResult> SendAsync(OutgoingNotification notification, CancellationToken ct);

    /// <summary>Sends <paramref name="notification"/> as a reply to an earlier bot message in the target chat.</summary>
    Task<SendResult> ReplyAsync(long replyToBotMessageId, OutgoingNotification notification, CancellationToken ct);

    /// <summary>AC-F10.2/F10.4/F10.5: answers a button press; <paramref name="text"/> is shown to the presser.</summary>
    Task<SendResult> AnswerCallbackAsync(string callbackQueryId, string text, CancellationToken ct);

    /// <summary>AC-F10.2: re-renders the label buttons of a bot message in the target chat with <paramref name="label"/> marked.</summary>
    Task<SendResult> SetLabelMarkupAsync(int botMessageId, long notificationId, NotificationLabel label, CancellationToken ct);
}

/// <summary>
/// Alerts and edit replies go before summaries and reports: a <see cref="Background"/> send waits while an
/// <see cref="Alert"/> send is waiting.
/// </summary>
internal enum SendPriority
{
    Alert,
    Background,
}

/// <summary>
/// Plain text, sent without a parse mode. With <see cref="LabelNotificationId"/> the message carries the label
/// buttons for that notification (AC-F10.1).
/// </summary>
internal sealed record OutgoingNotification(string Text, SendPriority Priority, long? LabelNotificationId = null);
