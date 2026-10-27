using Microsoft.Extensions.Configuration;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests;

public class CommittedConfigTests
{
    // Spec C4: the committed appsettings.json carries no identifying values.
    [Fact]
    public void P0_committed_appsettings_has_no_identifying_values()
    {
        var config = LoadCommitted();

        config.GetSection("Monitoring:Sources").GetChildren().ShouldBeEmpty();
        config.GetSection("Notifications:LabelerUserIds").GetChildren().ShouldBeEmpty();
        config.GetValue<long>("Notifications:TargetChatId").ShouldBe(0);
    }

    // Spec C4 / AC-F1.4: Telegram secrets and the session key are never committed.
    [Fact]
    public void AC_F1_4_committed_appsettings_has_empty_telegram_secrets()
    {
        var config = LoadCommitted();

        config.GetValue<int>("Telegram:ApiId").ShouldBe(0);
        config["Telegram:ApiHash"].ShouldBeNullOrEmpty();
        config["Telegram:PhoneNumber"].ShouldBeNullOrEmpty();
        config["Telegram:SessionKey"].ShouldBeNullOrEmpty();
        config["Telegram:SessionPath"].ShouldBe("data/session.dat");
    }

    private static IConfigurationRoot LoadCommitted() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(RepoRoot.Find(), "src", "Skywatch.Worker", "appsettings.json"), optional: false)
        .Build();
}
