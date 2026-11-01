namespace Skywatch.Worker.Persistence.Entities;

/// <summary>Spec §7 Messages: metadata for every received message; text only per the storage rule (F6.1, F12).</summary>
internal sealed class Message
{
    public long Id { get; set; }

    public int SourceId { get; set; }

    public Source? Source { get; set; }

    public int TelegramMessageId { get; set; }

    public string? Text { get; set; }

    public DateTime PublishedAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime? EditedAtUtc { get; set; }

    public string ContentHash { get; set; } = "";

    public long OriginChatId { get; set; }

    public int OriginMessageId { get; set; }

    public bool IsMatched { get; set; }

    /// <summary>Comma-separated pattern names.</summary>
    public string? MatchedPatterns { get; set; }

    public bool IsDuplicate { get; set; }

    /// <summary>Set to null by the database when the original is deleted by retention; <see cref="IsDuplicate"/> stays true.</summary>
    public long? DuplicateOfMessageId { get; set; }

    public Message? DuplicateOf { get; set; }

    public DuplicateReason? DuplicateReason { get; set; }

    /// <summary>The Telegram id of the message this one replies to in the same chat (F13).</summary>
    public int? ReplyToTelegramMessageId { get; set; }

    /// <summary>AC-F13.1: the original message this follow-up continues; set to null when retention deletes it.</summary>
    public long? FollowUpOfMessageId { get; set; }

    public Message? FollowUpOf { get; set; }
}

internal enum DuplicateReason
{
    Forward,
    SameText,
}
