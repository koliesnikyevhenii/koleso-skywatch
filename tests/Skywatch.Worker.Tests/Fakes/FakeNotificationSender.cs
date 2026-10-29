using System.Collections.Concurrent;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="INotificationSender"/>: records every send and returns the scripted results in order,
/// then <see cref="SendResult.Success"/> with increasing bot message ids.
/// </summary>
internal sealed class FakeNotificationSender : INotificationSender
{
    private readonly ConcurrentQueue<SendResult> _results = new();
    private int _nextBotMessageId = 100;

    public ConcurrentQueue<Sent> Sends { get; } = new();

    /// <summary>Runs on every send before its result is returned, e.g. to change the database meanwhile.</summary>
    public Action<Sent>? OnSend { get; set; }

    public IReadOnlyList<string> Texts => [.. Sends.Select(s => s.Notification.Text)];

    public void Script(params SendResult[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public Task<SendResult> SendAsync(OutgoingNotification notification, CancellationToken ct) => Record(new Sent(notification, ReplyTo: null));

    public Task<SendResult> ReplyAsync(long replyToBotMessageId, OutgoingNotification notification, CancellationToken ct) =>
        Record(new Sent(notification, replyToBotMessageId));

    private Task<SendResult> Record(Sent sent)
    {
        Sends.Enqueue(sent);
        OnSend?.Invoke(sent);
        return Task.FromResult(_results.TryDequeue(out var scripted) ? scripted : new SendResult.Success(Interlocked.Increment(ref _nextBotMessageId)));
    }

    internal sealed record Sent(OutgoingNotification Notification, long? ReplyTo);
}
