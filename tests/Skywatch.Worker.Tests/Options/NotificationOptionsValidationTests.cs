using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// Spec §6: with Notifications:Enabled = true, startup fails without BotToken or TargetChatId. Fake values only.
public sealed class NotificationOptionsValidationTests
{
    private const string _fakeToken = "123456:fake-bot-token-do-not-log";

    private static async Task<OptionsValidationException> StartupFailureAsync(Dictionary<string, string?> overrides)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        using var host = TestHost.Build([], settings);
        return await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Notifications:BotToken", "Notifications:TargetChatId", "-1000000000777")]
    [InlineData("Notifications:TargetChatId", "Notifications:BotToken", _fakeToken)]
    public async Task Spec6_enabled_notifications_require_bot_token_and_target_chat_id(string missingKey, string presentKey, string presentValue)
    {
        var ex = await StartupFailureAsync(new() { ["Notifications:Enabled"] = "true", [presentKey] = presentValue });

        ex.Message.ShouldContain(missingKey);
        ex.Message.ShouldNotContain(_fakeToken); // C5: messages name keys, never values
    }

    // C2: the target is the private test group, so a positive (user) chat id is rejected.
    [Fact]
    public async Task C2_target_chat_id_must_be_a_group() =>
        (await StartupFailureAsync(new() { ["Notifications:TargetChatId"] = "12345" })).Message.ShouldContain("Notifications:TargetChatId");

    [Theory]
    [InlineData("Notifications:MaxSendAttempts", "0")]
    [InlineData("Notifications:MaxMessageAgeSeconds", "0")]
    [InlineData("Notifications:DisplayTimeZone", "Mars/Olympus_Mons")]
    public async Task Spec6_invalid_notification_option_fails_run_mode_startup(string key, string value) =>
        (await StartupFailureAsync(new() { [key] = value })).Message.ShouldContain(key);

    // Disabled notifications need neither a token nor a target.
    [Fact]
    public void Spec6_disabled_notifications_need_no_bot_token() =>
        new NotificationOptionsValidator().Validate(null, new NotificationOptions()).Succeeded.ShouldBeTrue();
}
