namespace Skywatch.Core.Deduplication;

/// <summary>
/// <c>(chatId, messageId)</c> of the original post: the forward source if the message is a forward, else the message itself (spec §3).
/// Chat ids are Bot-API-style marked ids.
/// </summary>
public readonly record struct OriginKey(long ChatId, int MessageId);
