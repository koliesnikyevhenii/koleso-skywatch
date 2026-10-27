using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests;

public class HostLifecycleTests
{
    [Fact]
    public async Task P0_AC4_worker_host_starts_logs_and_stops_cleanly()
    {
        var logs = new CapturingLoggerProvider();
        using var session = TestHost.ExistingSessionFile();
        using var host = TestHost.Build([], TestHost.TelegramSettings(session.Path), logs);
        var ct = TestContext.Current.CancellationToken;

        await host.StartAsync(ct);
        logs.Lines.ShouldContain("Skywatch started");

        await Should.NotThrowAsync(() => host.StopAsync(ct));
    }
}
