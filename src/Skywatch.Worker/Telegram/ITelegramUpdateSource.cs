using Skywatch.Core.Deduplication;

namespace Skywatch.Worker.Telegram;

/// <summary>The listener seam over the Telegram user client (D13). No TL types cross it.</summary>
internal interface ITelegramUpdateSource
{
    /// <summary>Connects and resolves every enabled configured source against the reader account's chats (AC-F2.2–F2.4).</summary>
    Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct);

    /// <summary>
    /// Events in arrival order. Cancelling <paramref name="ct"/> stops intake; events already queued are still
    /// yielded and then the sequence ends, so the consumer can drain before it saves the update state.
    /// </summary>
    IAsyncEnumerable<SourceEvent> ReadAllAsync(CancellationToken ct);

    /// <summary>Saves the update state only when no event is queued; returns <see langword="false"/> otherwise.</summary>
    Task<bool> TrySaveStateAsync(CancellationToken ct);
}

internal abstract record SourceEvent;

/// <summary>A new or edited message from an enabled source (spec F3). <see cref="Text"/> is the text or the media caption.</summary>
internal sealed record IncomingMessage(
    long ChatId,
    int MessageId,
    string Text,
    DateTime PublishedAtUtc,
    DateTime ReceivedAtUtc,
    OriginKey Origin,
    bool IsEdit,
    DateTime? EditedAtUtc) : SourceEvent;

internal enum ConnectionState
{
    Disconnected,
    Reconnected,
}

internal sealed record ConnectionStateChanged(ConnectionState State, DateTime AtUtc) : SourceEvent;

internal enum SourceType
{
    Channel,
    Supergroup,
    Group,
}

/// <summary>Telegram rejected the stored session (AC-F1.3); the operator has to run <c>login</c> again.</summary>
internal sealed class TelegramSessionRejectedException(string reason, Exception? inner = null)
    : Exception($"Telegram rejected the session ({reason}).", inner);

/// <summary>A configured source after resolution. <see cref="Resolved"/> is false when the reader account has no such chat.</summary>
internal sealed record ResolvedSource(long ChatId, string Name, SourceType Type, string? Username, bool NoForwards, bool Resolved);
