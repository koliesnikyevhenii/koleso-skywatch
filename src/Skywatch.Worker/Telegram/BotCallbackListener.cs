using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Pipeline;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Skywatch.Worker.Telegram;

/// <summary>
/// AC-F10.6: long polling for <c>callback_query</c> updates only, in the Worker process, with the same token the
/// sender uses. Polling and sending do not conflict; only a webhook (none is configured) or a second poller with
/// this token makes <c>getUpdates</c> fail, which is logged and retried with backoff.
/// </summary>
internal sealed partial class BotCallbackListener(
    ITelegramBotClient bot,
    LabelService labels,
    IOptions<NotificationOptions> options,
    TimeProvider time,
    ILogger<BotCallbackListener> logger) : BackgroundService
{
    /// <summary>Seconds Telegram holds one <c>getUpdates</c> open; below the client's 100 s HTTP timeout.</summary>
    internal const int PollTimeoutSeconds = 50;

    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private static readonly UpdateType[] _allowedUpdates = [UpdateType.CallbackQuery];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? offset = null;
        var backoff = FirstBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await bot.GetUpdates(offset, timeout: PollTimeoutSeconds, allowedUpdates: _allowedUpdates, cancellationToken: stoppingToken);
                backoff = FirstBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPollFailed(logger, BotNotificationSender.Redact(ex.Message, options.Value.BotToken), backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                continue;
            }

            foreach (var update in updates)
            {
                // Confirmed on the next poll: a press that fails here is not delivered again.
                offset = update.Id + 1;
                if (update.CallbackQuery is not { } query)
                {
                    continue;
                }

                try
                {
                    await labels.HandleAsync(new LabelPress(query.Id, query.Data, query.From.Id, query.Message?.Chat.Id, query.Message?.MessageId), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogPressFailed(logger, ex, query.From.Id);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling bot callbacks failed: {Error}. Retrying in {BackoffSeconds} s.")]
    private static partial void LogPollFailed(ILogger logger, string error, double backoffSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling the label press by user {UserId} failed; continuing with the next update.")]
    private static partial void LogPressFailed(ILogger logger, Exception exception, long userId);
}
