using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Skywatch.Worker.Tests.Fakes;

/// <summary>
/// Hand-written <see cref="ITelegramBotClient"/> for <c>BotNotificationSender</c>: records every
/// <see cref="SendMessageRequest"/> and answers with the next queued response, or a message with the next id.
/// </summary>
internal sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private readonly ConcurrentQueue<Func<Task>> _responses = new();
    private int _nextMessageId = 100;

    public ConcurrentQueue<SendMessageRequest> Requests { get; } = new();

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
        var send = request as SendMessageRequest ?? throw new NotSupportedException(request.GetType().Name);
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
