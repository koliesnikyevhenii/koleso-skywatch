using Microsoft.Extensions.Logging;

namespace Skywatch.Worker.Telegram;

/// <summary>
/// Routes WTelegramClient's own logging into <see cref="ILogger"/> at Warning and above,
/// with known secret values replaced by <c>***</c> (AC-F1.5).
/// </summary>
internal sealed partial class WTelegramLogBridge
{
    private const string _mask = "***";

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private List<string> _secrets = [];

    public WTelegramLogBridge(ILoggerFactory loggerFactory, IEnumerable<string?> secrets)
    {
        _logger = loggerFactory.CreateLogger("WTelegram");
        foreach (var secret in secrets)
        {
            AddSecret(secret);
        }
    }

    public void Install() => WTelegram.Helpers.Log = Write;

    /// <summary>Adds a value to mask from now on, e.g. the verification code typed during <c>login</c>.</summary>
    public void AddSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_lock)
        {
            // Phone numbers also appear without the leading '+'. Longest first, so no partial match is left behind.
            _secrets = [.. _secrets.Append(value).Append(value.TrimStart('+')).Distinct().OrderByDescending(s => s.Length)];
        }
    }

    public string Redact(string text)
    {
        List<string> secrets;
        lock (_lock)
        {
            secrets = _secrets;
        }

        foreach (var secret in secrets)
        {
            text = text.Replace(secret, _mask, StringComparison.Ordinal);
        }

        return text;
    }

    private void Write(int level, string message)
    {
        var logLevel = (LogLevel)level;
        if (logLevel is < LogLevel.Warning or > LogLevel.Critical || !_logger.IsEnabled(logLevel))
        {
            return;
        }

        var line = Redact(message);
        LogLine(_logger, logLevel, line);
    }

    [LoggerMessage(Message = "{Line}")]
    private static partial void LogLine(ILogger logger, LogLevel level, string line);
}
