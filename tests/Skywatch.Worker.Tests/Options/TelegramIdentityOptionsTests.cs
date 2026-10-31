using Skywatch.Worker.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// AC-F1.9: fixed device identity for the reader client and a session key WTelegramClient can use.
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

    // AC-F1.9: the session key is the hex AES key of the session file (16, 24 or 32 bytes); anything else fails
    // startup with a message naming the key, not the value.
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00", false)]
    [InlineData("q2Zx+/abcdefghijklmnopqrstuvwxyz0123456789ABCD=", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    public void AC_F1_9_session_key_must_be_hex_of_an_aes_key_length(string key, bool valid)
    {
        using var session = TestHost.ExistingSessionFile();

        var result = new TelegramOptionsValidator(RunMode.Run).Validate(null, new TelegramOptions { ApiId = 1, ApiHash = TestHost.FakeApiHash, PhoneNumber = TestHost.FakePhone, SessionKey = key, SessionPath = session.Path });

        result.Succeeded.ShouldBe(valid);
        if (!valid)
        {
            result.FailureMessage.ShouldNotBeNull().ShouldContain("Telegram:SessionKey");
            result.FailureMessage.ShouldContain("hex");
            result.FailureMessage.ShouldNotContain(key);
        }
    }

    // AC-F1.9 also for login: a key WTelegramClient cannot use must fail before the code is asked for.
    [Fact]
    public void AC_F1_9_invalid_session_key_fails_login_too()
    {
        var result = new TelegramOptionsValidator(RunMode.Login).Validate(null, new TelegramOptions { ApiId = 1, ApiHash = TestHost.FakeApiHash, PhoneNumber = TestHost.FakePhone, SessionKey = "not-a-hex-session-key", SessionPath = "data/session.dat" });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Telegram:SessionKey");
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
            .Validate(null, new TelegramOptions { ApiId = 1, ApiHash = TestHost.FakeApiHash, PhoneNumber = TestHost.FakePhone, SessionKey = TestHost.FakeSessionKey, SessionPath = session.Path });

        result.Succeeded.ShouldBe(valid);
        if (!valid)
        {
            result.FailureMessage.ShouldNotBeNull().ShouldContain("login");
        }
    }
}
