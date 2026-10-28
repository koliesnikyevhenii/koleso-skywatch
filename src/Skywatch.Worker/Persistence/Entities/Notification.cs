namespace Skywatch.Worker.Persistence.Entities;

/// <summary>Spec §7 Notifications: the outbox row for one bot message about a <see cref="Message"/>.</summary>
internal sealed class Notification
{
    public long Id { get; set; }

    public long MessageId { get; set; }

    public Message? Message { get; set; }

    public NotificationKind Kind { get; set; }

    public NotificationStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    public int? BotMessageId { get; set; }

    public string? Error { get; set; }

    public bool PossibleDuplicate { get; set; }

    public DateTime? StaleReportedAtUtc { get; set; }

    public NotificationLabel? Label { get; set; }

    public long? LabeledByUserId { get; set; }

    public DateTime? LabeledAtUtc { get; set; }
}

internal enum NotificationKind
{
    New,
    EditNewMatch,
    EditReply,
}

internal enum NotificationStatus
{
    Pending,
    Sent,
    SkippedStale,
    SkippedDuplicate,
    Failed,
}

internal enum NotificationLabel
{
    Useful,
    False,
    Duplicate,
}
