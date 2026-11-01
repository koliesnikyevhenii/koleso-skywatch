using Microsoft.Extensions.Options;
using Skywatch.Core.Filtering;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Skywatch.Worker.Telegram;

/// <summary>
/// Telegram.Bot implementation of <see cref="INotificationSender"/>: plain text, no parse mode, always to
/// <c>Notifications:TargetChatId</c> (C2). All sends and button edits pass one <see cref="SendGate"/>; callback
/// answers do not, because Telegram expects them within seconds.
/// </summary>
internal sealed class BotNotificationSender(ITelegramBotClient bot, IOptions<NotificationOptions> options, TimeProvider time)
    : INotificationSender, IDisposable
{
    private const int _tooManyRequests = 429;
    private const int _firstServerError = 500;
    private const string _notModified = "message is not modified";

    private readonly SendGate _gate = new(time);

    public Task<SendResult> SendAsync(OutgoingNotification notification, CancellationToken ct) =>
        SendCoreAsync(notification, replyTo: null, ct);

    public Task<SendResult> ReplyAsync(long replyToBotMessageId, OutgoingNotification notification, CancellationToken ct) =>
        SendCoreAsync(notification, new ReplyParameters { MessageId = checked((int)replyToBotMessageId) }, ct);

    /// <summary>
    /// Succeeds with bot message id 0: an answer creates no message. AC-F10.9: answers bypass the gate, so a 429 on
    /// one does not block it either; match notifications are never held back by a button press.
    /// </summary>
    public Task<SendResult> AnswerCallbackAsync(string callbackQueryId, string text, CancellationToken ct) =>
        CallAsync(
            async () =>
            {
                await bot.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: ct);
                return new SendResult.Success(0);
            },
            ct,
            blockGateOnRateLimit: false);

    /// <summary>Pressing the already chosen label again leaves the markup unchanged, which is not a failure.</summary>
    public async Task<SendResult> SetLabelMarkupAsync(int botMessageId, long notificationId, NotificationLabel label, CancellationToken ct)
    {
        using var lease = await _gate.EnterAsync(SendPriority.Background, ct);
        var result = await CallAsync(
            async () =>
            {
                var message = await bot.EditMessageReplyMarkup(options.Value.TargetChatId, botMessageId, LabelKeyboard(notificationId, label), cancellationToken: ct);
                return new SendResult.Success(message.Id);
            },
            ct);
        return result is SendResult.Fatal fatal && fatal.Error.Contains(_notModified, StringComparison.OrdinalIgnoreCase)
            ? new SendResult.Success(botMessageId)
            : result;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>AC-F10.1: one row of label buttons; AC-F10.2: the chosen one is marked.</summary>
    internal static InlineKeyboardMarkup LabelKeyboard(long notificationId, NotificationLabel? chosen) =>
        new(LabelButtons.All.Select(l => InlineKeyboardButton.WithCallbackData(LabelButtons.Text(l, chosen), LabelButtons.Data(notificationId, l))).ToArray());

    /// <summary>AC-F8.14: Telegram entity offsets count UTF-16 code units, the same as .NET string indices.</summary>
    internal static MessageEntity[]? BoldEntities(IReadOnlyList<TextSpan>? bold) =>
        bold is null or { Count: 0 } ? null : [.. bold.Select(s => new MessageEntity { Type = MessageEntityType.Bold, Offset = s.Start, Length = s.Length })];

    private async Task<SendResult> SendCoreAsync(OutgoingNotification notification, ReplyParameters? replyTo, CancellationToken ct)
    {
        using var lease = await _gate.EnterAsync(notification.Priority, ct);
        var keyboard = notification.LabelNotificationId is { } id ? LabelKeyboard(id, chosen: null) : null;
        return await CallAsync(
            async () =>
            {
                var message = await bot.SendMessage(options.Value.TargetChatId, notification.Text, replyParameters: replyTo, replyMarkup: keyboard, entities: BoldEntities(notification.Bold), cancellationToken: ct);
                return new SendResult.Success(message.Id);
            },
            ct);
    }

    private async Task<SendResult> CallAsync(Func<Task<SendResult>> call, CancellationToken ct, bool blockGateOnRateLimit = true)
    {
        try
        {
            return await call();
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == _tooManyRequests)
        {
            var retryAfter = TimeSpan.FromSeconds(Math.Max(ex.Parameters?.RetryAfter ?? 1, 1));
            if (blockGateOnRateLimit)
            {
                _gate.BlockUntil(time.GetUtcNow() + retryAfter);
            }

            return new SendResult.RateLimited(retryAfter);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode >= _firstServerError)
        {
            return new SendResult.Transient(Redact(ex.Message), DeliveryUnknown: false);
        }
        catch (ApiRequestException ex)
        {
            return new SendResult.Fatal(Redact(ex.Message));
        }
        catch (RequestException ex) when (!ct.IsCancellationRequested)
        {
            // Network failure or client timeout. After a timeout the request may still have been delivered (AC-F8.9).
            var timedOut = ex.InnerException is TaskCanceledException or TimeoutException;
            return new SendResult.Transient(Redact(Describe(ex)), DeliveryUnknown: timedOut);
        }
    }

    private static string Describe(Exception ex) => ex.InnerException is { } inner ? $"{ex.Message}: {inner.Message}" : ex.Message;

    private string Redact(string error) => Redact(error, options.Value.BotToken);

    /// <summary>C5: the token is part of every request URL, so it must never reach a stored error or a log.</summary>
    internal static string Redact(string error, string token) =>
        string.IsNullOrEmpty(token) ? error : error.Replace(token, "***", StringComparison.Ordinal);
}

/// <summary>
/// Serializes every bot send. After a 429 nobody is admitted until the block ends. When the gate frees up,
/// waiting <see cref="SendPriority.Alert"/> sends are admitted before <see cref="SendPriority.Background"/> ones.
/// </summary>
internal sealed class SendGate(TimeProvider time) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<TaskCompletionSource> _alerts = new();
    private readonly Queue<TaskCompletionSource> _background = new();
    private bool _busy;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private ITimer? _unblockTimer;

    public async Task<IDisposable> EnterAsync(SendPriority priority, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            (priority == SendPriority.Alert ? _alerts : _background).Enqueue(waiter);
            AdmitNext();
        }

        // A cancelled waiter stays queued and is skipped when its turn comes (TrySetResult fails).
        using (ct.Register(() => waiter.TrySetCanceled(ct)))
        {
            await waiter.Task;
        }

        return new Lease(this);
    }

    /// <summary>Called by the gate holder on a 429; the block starts when the holder leaves.</summary>
    public void BlockUntil(DateTimeOffset until)
    {
        lock (_lock)
        {
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _unblockTimer?.Dispose();
        }
    }

    private void Leave()
    {
        lock (_lock)
        {
            _busy = false;
            AdmitNext();
        }
    }

    /// <summary>Under the lock.</summary>
    private void AdmitNext()
    {
        if (_busy || (_alerts.Count == 0 && _background.Count == 0))
        {
            return;
        }

        var now = time.GetUtcNow();
        if (now < _blockedUntil)
        {
            _unblockTimer?.Dispose();
            _unblockTimer = time.CreateTimer(_ => { lock (_lock) { AdmitNext(); } }, null, _blockedUntil - now, Timeout.InfiniteTimeSpan);
            return;
        }

        while (_alerts.TryDequeue(out var next) || _background.TryDequeue(out next))
        {
            if (next.TrySetResult())
            {
                _busy = true;
                return;
            }
        }
    }

    private sealed class Lease(SendGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Leave();
            }
        }
    }
}
