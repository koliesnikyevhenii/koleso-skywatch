using Microsoft.Extensions.Configuration;

namespace Skywatch.Worker.Tests;

public class CommittedConfigTests
{
    // Spec C4: the committed appsettings.json carries no identifying values.
    [Fact]
    public void P0_committed_appsettings_has_no_identifying_values()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepoRoot(), "src", "Skywatch.Worker", "appsettings.json"), optional: false)
            .Build();

        config.GetSection("Monitoring:Sources").GetChildren().ShouldBeEmpty();
        config.GetSection("Notifications:LabelerUserIds").GetChildren().ShouldBeEmpty();
        config.GetValue<long>("Notifications:TargetChatId").ShouldBe(0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Skywatch.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Skywatch.slnx not found above the test output directory.");
    }
}
