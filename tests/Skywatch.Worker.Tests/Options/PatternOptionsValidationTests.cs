using Microsoft.Extensions.Options;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Options;

// AC-F5.9: an invalid regex, empty Name, duplicate Name or zero enabled patterns fails startup via options validation.
public sealed class PatternOptionsValidationTests : IDisposable
{
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();

    public void Dispose() => _session.Dispose();

    private async Task<OptionsValidationException> RunModeStartFails(params (string Name, string Regex, bool Enabled)[] patterns)
    {
        var settings = TestHost.TelegramSettings(_session.Path);
        TestHost.SetPatterns(settings, patterns);
        using var host = TestHost.Build([], settings);

        return await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AC_F5_9_invalid_regex_fails_startup()
    {
        var ex = await RunModeStartFails(("Good", @"\bтест\b", true), ("Broken", @"(незакрита", true));

        ex.Message.ShouldContain("Monitoring:Patterns:1:Regex");
        ex.Message.ShouldContain("Broken");
    }

    [Fact]
    public async Task AC_F5_9_empty_pattern_name_fails_startup()
    {
        var ex = await RunModeStartFails(("Good", @"\bтест\b", true), (" ", @"\bінше\b", true));

        ex.Message.ShouldContain("Monitoring:Patterns:1:Name");
    }

    [Fact]
    public async Task AC_F5_9_duplicate_pattern_name_fails_startup()
    {
        var ex = await RunModeStartFails(("Same", @"\bтест\b", true), ("Same", @"\bінше\b", true));

        ex.Message.ShouldContain("Same");
        ex.Message.ShouldContain("more than once");
    }

    [Fact]
    public async Task AC_F5_9_no_enabled_pattern_fails_startup()
    {
        var ex = await RunModeStartFails(("Off", @"\bтест\b", false));

        ex.Message.ShouldContain("Monitoring:Patterns");
        ex.Message.ShouldContain("enabled");
    }

    [Fact]
    public async Task AC_F5_9_no_patterns_at_all_fails_startup()
    {
        var ex = await RunModeStartFails();

        ex.Message.ShouldContain("Monitoring:Patterns");
    }
}
