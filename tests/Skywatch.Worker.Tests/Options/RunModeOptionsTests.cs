using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// Plan P1a validation table and spec §6: options are validated per run mode at startup (ValidateOnStart).
public class RunModeOptionsTests
{
    // Spec §6: in normal run mode, startup fails if Telegram:ApiId, ApiHash or SessionKey is missing.
    [Theory]
    [InlineData("Telegram:ApiId", "0")]
    [InlineData("Telegram:ApiHash", "")]
    [InlineData("Telegram:SessionKey", "")]
    public async Task Spec_6_run_mode_fails_startup_when_required_telegram_option_is_missing(string key, string emptyValue)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings[key] = emptyValue;
        using var host = TestHost.Build([], settings);

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(key);
    }

    // AC-F1.1: `login` creates the session, so it must start without one and without the phone in config (it prompts).
    [Fact]
    public async Task AC_F1_1_login_mode_starts_without_an_existing_session_file()
    {
        var settings = TestHost.TelegramSettings(TestHost.MissingSessionPath());
        settings["Telegram:PhoneNumber"] = "";
        using var host = TestHost.Build(["login"], settings);
        var ct = TestContext.Current.CancellationToken;

        await Should.NotThrowAsync(() => host.StartAsync(ct));
        await host.StopAsync(ct);
    }

    [Theory]
    [InlineData("Telegram:ApiId", "0")]
    [InlineData("Telegram:ApiHash", "")]
    [InlineData("Telegram:SessionKey", "")]
    public async Task AC_F1_1_login_mode_fails_startup_when_required_telegram_option_is_missing(string key, string emptyValue)
    {
        var settings = TestHost.TelegramSettings(TestHost.MissingSessionPath());
        settings[key] = emptyValue;
        using var host = TestHost.Build(["login"], settings);

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(key);
    }

    // AC-F1.2: `list-chats` uses the existing session; without it startup fails and points the operator to `login`.
    [Fact]
    public async Task AC_F1_2_list_chats_mode_fails_startup_without_a_session_file()
    {
        using var host = TestHost.Build(["list-chats"], TestHost.TelegramSettings(TestHost.MissingSessionPath()));

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("login");
    }

    [Fact]
    public async Task AC_F1_2_list_chats_mode_starts_with_an_existing_session_file()
    {
        using var session = TestHost.ExistingSessionFile();
        using var host = TestHost.Build(["list-chats"], TestHost.TelegramSettings(session.Path));
        var ct = TestContext.Current.CancellationToken;

        await Should.NotThrowAsync(() => host.StartAsync(ct));
        await host.StopAsync(ct);
    }

    // AC-F1.4: session path and encryption key come from configuration and are what WTelegramClient is given.
    [Fact]
    public void AC_F1_4_session_path_and_key_come_from_configuration()
    {
        using var session = TestHost.ExistingSessionFile();
        using var host = TestHost.Build(["login"], TestHost.TelegramSettings(session.Path));
        var options = host.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;

        options.WTelegramConfig("session_pathname").ShouldBe(session.Path);
        options.WTelegramConfig("session_key").ShouldBe(TestHost.FakeSessionKey);
    }

    // AC-F1.1: `login` reads ApiId, ApiHash and PhoneNumber from configuration.
    [Fact]
    public void AC_F1_1_login_reads_api_id_api_hash_and_phone_from_configuration()
    {
        using var host = TestHost.Build(["login"], TestHost.TelegramSettings(TestHost.MissingSessionPath()));
        var options = host.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;

        options.WTelegramConfig("api_id").ShouldBe("12345");
        options.WTelegramConfig("api_hash").ShouldBe(TestHost.FakeApiHash);
        options.WTelegramConfig("phone_number").ShouldBe(TestHost.FakePhone);
        options.WTelegramConfig("server_address").ShouldBeNull(); // everything else: WTelegramClient defaults
    }

    // AC-F2.1: sources are { ChatId, Name, Enabled } with the marked ids `list-chats` prints.
    [Fact]
    public void AC_F2_1_sources_bind_from_monitoring_section_with_marked_ids()
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Monitoring:Sources:0:ChatId"] = "-1000000000042";
        settings["Monitoring:Sources:0:Name"] = "Test Channel";
        settings["Monitoring:Sources:0:Enabled"] = "true";
        settings["Monitoring:Sources:1:ChatId"] = "-4242";
        settings["Monitoring:Sources:1:Name"] = "Test Group";
        settings["Monitoring:Sources:1:Enabled"] = "false";
        using var host = TestHost.Build([], settings);

        var sources = host.Services.GetRequiredService<IOptions<MonitoringOptions>>().Value.Sources;

        sources.Select(s => (s.ChatId, s.Name, s.Enabled)).ShouldBe(
        [
            (-1000000000042L, "Test Channel", true),
            (-4242L, "Test Group", false),
        ]);
    }

    // AC-F2.1: the committed example shows the full Sources shape and binds without errors.
    [Fact]
    public void AC_F2_1_example_config_sources_bind_with_marked_ids()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot.Find(), "src", "Skywatch.Worker", "appsettings.Example.json"), optional: false)
            .Build();

        var sources = config.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>()!.Sources;

        sources.ShouldNotBeEmpty();
        sources.ShouldAllBe(s => s.ChatId < 0 && s.Name.Length > 0);
    }

    // AC-F2.1: a source row that cannot identify a chat fails run-mode startup.
    [Theory]
    [InlineData("0", "Test Channel")]           // no chat id
    [InlineData("12345", "Test Channel")]       // a user id, not a chat
    [InlineData("-1000000000042", "")]          // no name
    public async Task AC_F2_1_invalid_source_fails_run_mode_startup(string chatId, string name)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Monitoring:Sources:0:ChatId"] = chatId;
        settings["Monitoring:Sources:0:Name"] = name;
        using var host = TestHost.Build([], settings);

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("Monitoring:Sources");
    }

    [Fact]
    public async Task AC_F2_1_duplicate_source_chat_id_fails_run_mode_startup()
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Monitoring:Sources:0:ChatId"] = "-1000000000042";
        settings["Monitoring:Sources:0:Name"] = "A";
        settings["Monitoring:Sources:1:ChatId"] = "-1000000000042";
        settings["Monitoring:Sources:1:Name"] = "B";
        using var host = TestHost.Build([], settings);

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("-1000000000042");
    }

    // Plan P1a validation table: Sources are not validated in `login` / `list-chats`.
    [Fact]
    public async Task AC_F1_2_list_chats_mode_ignores_invalid_sources()
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.TelegramSettings(session.Path);
        settings["Monitoring:Sources:0:ChatId"] = "0";
        using var host = TestHost.Build(["list-chats"], settings);
        var ct = TestContext.Current.CancellationToken;

        await Should.NotThrowAsync(() => host.StartAsync(ct));
        await host.StopAsync(ct);
    }
}
