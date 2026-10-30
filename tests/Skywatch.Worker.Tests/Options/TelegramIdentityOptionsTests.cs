using Skywatch.Worker.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// AC-F1.9: fixed device identity for the reader client and a minimum session key length.
public class TelegramIdentityOptionsTests
{
    // AC-F1.9: the configured device values reach WTelegramClient's config callback; the defaults are fixed values.
    [Fact]
    public void AC_F1_9_device_identity_comes_from_configuration()
    {
        var defaults = new TelegramOptions();
        (defaults.WTelegramConfig("device_model"), defaults.WTelegramConfig("system_version"), defaults.WTelegramConfig("app_version"))
            .ShouldBe(("Skywatch", "Server", "1.0"));
        defaults.WTelegramConfig("lang_code").ShouldBe("uk");
        defaults.WTelegramConfig("system_lang_code").ShouldBe("uk");

        var custom = new TelegramOptions { DeviceModel = "Reader", SystemVersion = "Debian 12", AppVersion = "2.0", LangCode = "ru" };
        (custom.WTelegramConfig("device_model"), custom.WTelegramConfig("system_version"), custom.WTelegramConfig("app_version"), custom.WTelegramConfig("lang_code"))
            .ShouldBe(("Reader", "Debian 12", "2.0", "ru"));
    }

    // AC-F1.9: an empty value falls back to the library default instead of sending an empty string.
    [Fact]
    public void AC_F1_9_empty_device_value_uses_the_library_default() =>
        new TelegramOptions { DeviceModel = " " }.WTelegramConfig("device_model").ShouldBeNull();

    // AC-F1.9: a session key shorter than 16 characters fails startup, naming the key but not the value.
    [Theory]
    [InlineData("short-key", false)]
    [InlineData("exactly-16-chars", true)]
    public void AC_F1_9_session_key_needs_at_least_16_characters(string key, bool valid)
    {
        using var session = TestHost.ExistingSessionFile();

        var result = new TelegramOptionsValidator(RunMode.Run).Validate(null, new TelegramOptions { ApiId = 1, ApiHash = TestHost.FakeApiHash, SessionKey = key, SessionPath = session.Path });

        result.Succeeded.ShouldBe(valid);
        if (!valid)
        {
            result.FailureMessage.ShouldNotBeNull().ShouldContain("Telegram:SessionKey");
            result.FailureMessage.ShouldNotContain(key);
        }
    }

    // AC-F1.8: the marker blocks the run mode and list-chats, not login (which clears it).
    [Theory]
    [InlineData(nameof(RunMode.Run), false)]
    [InlineData(nameof(RunMode.ListChats), false)]
    [InlineData(nameof(RunMode.Login), true)]
    public void AC_F1_8_rejection_marker_fails_validation_except_for_login(string mode, bool valid)
    {
        using var session = TestHost.ExistingSessionFile();
        Skywatch.Worker.Telegram.SessionFiles.MarkRejected(session.Path, DateTime.UtcNow);

        var result = new TelegramOptionsValidator(Enum.Parse<RunMode>(mode))
            .Validate(null, new TelegramOptions { ApiId = 1, ApiHash = TestHost.FakeApiHash, SessionKey = TestHost.FakeSessionKey, SessionPath = session.Path });

        result.Succeeded.ShouldBe(valid);
        if (!valid)
        {
            result.FailureMessage.ShouldNotBeNull().ShouldContain("login");
        }
    }
}
