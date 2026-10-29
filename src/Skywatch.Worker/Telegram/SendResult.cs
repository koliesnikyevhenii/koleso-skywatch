namespace Skywatch.Worker.Telegram;

/// <summary>The outcome of one send attempt; the dispatcher (P4b) turns it into retries and a status (AC-F8.6–F8.10).</summary>
internal abstract record SendResult
{
    private SendResult()
    {
    }

    public sealed record Success(int BotMessageId) : SendResult;

    /// <summary>HTTP 429: every later send waits <see cref="RetryAfter"/>; not an attempt against the limit (AC-F8.6).</summary>
    public sealed record RateLimited(TimeSpan RetryAfter) : SendResult;

    /// <summary>
    /// Network error, timeout or 5xx: retry with backoff (AC-F8.7). <see cref="DeliveryUnknown"/> means the message
    /// may have arrived anyway (AC-F8.9).
    /// </summary>
    public sealed record Transient(string Error, bool DeliveryUnknown) : SendResult;

    /// <summary>4xx other than 429: do not retry (AC-F8.8).</summary>
    public sealed record Fatal(string Error) : SendResult;
}
