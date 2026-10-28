namespace Skywatch.Worker.Telegram;

/// <summary>The delivery seam over the bot (D13). Sends only to <c>Notifications:TargetChatId</c> (C2).</summary>
internal interface INotificationSender
{
    Task<SendResult> SendAsync(OutgoingNotification notification, CancellationToken ct);

    /// <summary>Sends <paramref name="notification"/> as a reply to an earlier bot message in the target chat.</summary>
    Task<SendResult> ReplyAsync(long replyToBotMessageId, OutgoingNotification notification, CancellationToken ct);
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

/// <summary>Plain text, sent without a parse mode.</summary>
internal sealed record OutgoingNotification(string Text, SendPriority Priority);
