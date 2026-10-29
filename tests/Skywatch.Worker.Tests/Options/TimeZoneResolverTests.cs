using Skywatch.Worker.Options;

namespace Skywatch.Worker.Tests.Options;

// Plan P4a / AC-F8.12: Europe/Kyiv, falling back to the former name Europe/Kiev; neither → startup error.
public sealed class TimeZoneResolverTests
{
    private static readonly TimeZoneInfo _zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(2), "Test", "Test");

    private static Func<string, TimeZoneInfo?> Known(params string[] ids) => id => ids.Contains(id) ? _zone : null;

    [Fact]
    public void AC_F8_12_kyiv_resolves_when_known() =>
        TimeZoneResolver.Resolve(TimeZoneResolver.Kyiv, Known(TimeZoneResolver.Kyiv)).ShouldBeSameAs(_zone);

    [Fact]
    public void AC_F8_12_kyiv_missing_falls_back_to_kiev()
    {
        List<string> asked = [];

        var zone = TimeZoneResolver.Resolve(TimeZoneResolver.Kyiv, id => { asked.Add(id); return Known(TimeZoneResolver.Kiev)(id); });

        zone.ShouldBeSameAs(_zone);
        asked.ShouldBe([TimeZoneResolver.Kyiv, TimeZoneResolver.Kiev]);
    }

    [Fact]
    public void AC_F8_12_neither_kyiv_nor_kiev_is_an_error()
    {
        TimeZoneResolver.Resolve(TimeZoneResolver.Kyiv, Known()).ShouldBeNull();
        new NotificationOptionsValidator(Known()).Validate(null, new NotificationOptions()).FailureMessage.ShouldNotBeNull().ShouldContain("Notifications:DisplayTimeZone");
    }

    // Another configured zone has no fallback.
    [Fact]
    public void AC_F8_12_other_zone_has_no_fallback() =>
        TimeZoneResolver.Resolve("Europe/Warsaw", Known(TimeZoneResolver.Kiev)).ShouldBeNull();

    // On this machine the default resolves (either name), so the run-mode default configuration starts.
    [Fact]
    public void AC_F8_12_default_display_time_zone_resolves_on_this_system() =>
        TimeZoneResolver.ResolveOrThrow(new NotificationOptions().DisplayTimeZone).BaseUtcOffset.ShouldBe(TimeSpan.FromHours(2));
}
