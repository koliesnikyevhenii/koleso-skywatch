namespace Skywatch.Worker.Tests.Telegram;

/// <summary>Tests that touch the static <c>WTelegram.Helpers.Log</c> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WTelegramStaticLog
{
    public const string Name = "WTelegram static log";
}
