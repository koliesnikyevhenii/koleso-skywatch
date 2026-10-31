using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Telegram;

// AC-F1.3: what the run mode answers when WTelegramClient asks for configuration while resuming a session.
public class WTelegramRunModeConfigTests
{
    private static readonly TelegramOptions _telegram = new() { ApiId = 1, ApiHash = TestHost.FakeApiHash, PhoneNumber = TestHost.FakePhone, SessionKey = TestHost.FakeSessionKey };

    // AC-F1.3: a valid session is resumed: the library checks it against the configured number, so that is no prompt.
    [Fact]
    public void AC_F1_3_phone_number_comes_from_configuration_on_resume() =>
        WTelegramUpdateSource.RunModeConfig(_telegram, "phone_number").ShouldBe(TestHost.FakePhone);

    // AC-F1.3: a request for a code or the 2FA password means a new login is needed: the session counts as rejected.
    [Theory]
    [InlineData("verification_code")]
    [InlineData("password")]
    public void AC_F1_3_login_prompts_mean_the_session_is_rejected(string what) =>
        Should.Throw<TelegramSessionRejectedException>(() => WTelegramUpdateSource.RunModeConfig(_telegram, what));

    // AC-F1.3: without a configured number there is nothing to answer with; the run mode still never prompts.
    [Fact]
    public void AC_F1_3_missing_phone_number_is_a_rejected_session_not_a_prompt() =>
        Should.Throw<TelegramSessionRejectedException>(() => WTelegramUpdateSource.RunModeConfig(new TelegramOptions(), "phone_number"));

    // Other keys pass through to the shared configuration.
    [Fact]
    public void Other_keys_come_from_configuration() =>
        WTelegramUpdateSource.RunModeConfig(_telegram, "session_key").ShouldBe(TestHost.FakeSessionKey);
}
