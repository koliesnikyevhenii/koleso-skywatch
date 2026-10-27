using Microsoft.Extensions.Logging;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Telegram;

[Collection(WTelegramStaticLog.Name)]
public sealed class WTelegramLogBridgeTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _factory;
    private readonly Action<int, string> _originalLog = WTelegram.Helpers.Log;

    public WTelegramLogBridgeTests() => _factory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

    public void Dispose()
    {
        WTelegram.Helpers.Log = _originalLog;
        _factory.Dispose();
    }

    // AC-F1.5: API hash, phone number and session key never reach the log, even when WTelegramClient prints them.
    [Theory]
    [InlineData(TestHost.FakeApiHash)]
    [InlineData(TestHost.FakeSessionKey)]
    [InlineData(TestHost.FakePhone)]
    [InlineData("380001112233")] // the same phone number without the leading +
    public void AC_F1_5_log_bridge_masks_known_secret_values(string leaked)
    {
        new WTelegramLogBridge(_factory, [TestHost.FakeApiHash, TestHost.FakeSessionKey, TestHost.FakePhone]).Install();

        WTelegram.Helpers.Log(4, $"Something went wrong with {leaked} here");

        var line = _logs.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Error);
        line.Message.ShouldNotContain(leaked);
        line.Message.ShouldContain("***");
    }

    // AC-F1.5: the verification code and 2FA password typed during `login` are masked from then on.
    [Fact]
    public void AC_F1_5_log_bridge_masks_secrets_added_after_install()
    {
        var bridge = new WTelegramLogBridge(_factory, []);
        bridge.Install();

        bridge.AddSecret("54321");
        bridge.AddSecret("my 2fa password");
        WTelegram.Helpers.Log(3, "code 54321 and password my 2fa password");

        _logs.Lines.ShouldHaveSingleItem().ShouldBe("code *** and password ***");
    }

    // Plan P1a: WTelegramClient's own logging goes to ILogger at Warning and above only.
    [Fact]
    public void AC_F1_5_log_bridge_drops_wtelegram_lines_below_warning()
    {
        new WTelegramLogBridge(_factory, []).Install();

        WTelegram.Helpers.Log(1, "debug line");
        WTelegram.Helpers.Log(2, "info line");
        WTelegram.Helpers.Log(3, "warning line");
        WTelegram.Helpers.Log(5, "critical line");

        _logs.Entries.Select(e => (e.Level, e.Message)).ShouldBe(
        [
            (LogLevel.Warning, "warning line"),
            (LogLevel.Critical, "critical line"),
        ]);
    }

    [Fact]
    public void AC_F1_5_empty_secret_values_are_ignored()
    {
        new WTelegramLogBridge(_factory, ["", null]).Install();

        WTelegram.Helpers.Log(3, "plain line");

        _logs.Lines.ShouldHaveSingleItem().ShouldBe("plain line");
    }
}
