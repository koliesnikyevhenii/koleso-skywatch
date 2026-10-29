using System.Collections.Concurrent;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ITelegramBotClient"/> for <c>BotNotificationSender</c> and <c>BotCallbackListener</c>:
/// records every <see cref="SendMessageRequest"/> (and the other requests in <see cref="OtherRequests"/>) and answers
/// with the next queued response, or a message with the next id. <c>getUpdates</c> returns the next batch from
/// <see cref="EmitUpdates"/>, or waits like a long poll.
/// </summary>
internal sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private readonly ConcurrentQueue<Func<Task>> _responses = new();
    private int _nextMessageId = 100;

    private readonly Channel<Func<Update[]>> _updates = Channel.CreateUnbounded<Func<Update[]>>();

    public ConcurrentQueue<SendMessageRequest> Requests { get; } = new();

    public ConcurrentQueue<object> OtherRequests { get; } = new();

    public ConcurrentQueue<GetUpdatesRequest> Polls { get; } = new();

    public void EmitUpdates(params Update[] updates) => _updates.Writer.TryWrite(() => updates);

    /// <summary>The next <c>getUpdates</c> throws <paramref name="exception"/>.</summary>
    public void FailPoll(Exception exception) => _updates.Writer.TryWrite(() => throw exception);

    public bool LocalBotServer => false;

    public long BotId => 1;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }

    /// <summary>The next send throws <paramref name="exception"/>.</summary>
    public void Throw(Exception exception) => _responses.Enqueue(() => Task.FromException(exception));

    /// <summary>The next send stays in flight until the returned source completes.</summary>
    public TaskCompletionSource Hold()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses.Enqueue(() => hold.Task);
        return hold;
    }

    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetUpdatesRequest poll)
        {
            Polls.Enqueue(poll);
            var next = await _updates.Reader.ReadAsync(cancellationToken);
            return (TResponse)(object)next();
        }

        if (request is not SendMessageRequest send)
        {
            OtherRequests.Enqueue(request);
            if (_responses.TryDequeue(out var queued))
            {
                await queued();
            }

            return request switch
            {
                AnswerCallbackQueryRequest => (TResponse)(object)true,
                EditMessageReplyMarkupRequest edit => (TResponse)(object)new Message { Id = edit.MessageId },
                _ => throw new NotSupportedException(request.GetType().Name),
            };
        }

        Requests.Enqueue(send);
        if (_responses.TryDequeue(out var response))
        {
            await response();
        }

        object message = new Message { Id = Interlocked.Increment(ref _nextMessageId), Text = send.Text };
        return (TResponse)message;
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
