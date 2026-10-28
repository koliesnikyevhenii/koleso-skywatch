using Skywatch.Core.Time;

namespace Skywatch.Core.Tests.Time;

public class StalenessPolicyTests
{
    private static readonly DateTime _published = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    // AC-F7.1 with the AC-F7.2 default of 120 s: older than the maximum age is stale, exactly the maximum is not.
    [Theory]
    [InlineData(0, false)]
    [InlineData(120, false)]
    [InlineData(121, true)]
    [InlineData(3600, true)]
    public void AC_F7_1_message_older_than_max_age_is_stale(int ageSeconds, bool stale) =>
        new StalenessPolicy(TimeSpan.FromSeconds(120)).IsStale(_published, _published.AddSeconds(ageSeconds)).ShouldBe(stale);
}
