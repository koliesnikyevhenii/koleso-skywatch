using Skywatch.Core.Time;

namespace Skywatch.Core.Tests.Time;

public class ClockSkewEstimatorTests
{
    private static readonly DateTime _published = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    // AC-F3.8: minimum of ReceivedAt − PublishedAt, excluding differences over 120 s (catch-up);
    // suspect if the minimum exceeds 10 s or any value is below −2 s; no data → no verdict.
    [Theory]
    [InlineData(new[] { 1.5, 3.0, 8.0 }, ClockSkewStatus.Normal, 1.5)]
    [InlineData(new[] { 12.0, 15.0, 30.0 }, ClockSkewStatus.Suspect, 12.0)]
    [InlineData(new[] { -3.0, 1.0, 2.0 }, ClockSkewStatus.Suspect, -3.0)]
    [InlineData(new[] { 300.0, 1.0, 2.0 }, ClockSkewStatus.Normal, 1.0)]
    [InlineData(new[] { 400.0, 120.0, 11.0 }, ClockSkewStatus.Suspect, 11.0)]
    [InlineData(new[] { 10.0, 20.0 }, ClockSkewStatus.Normal, 10.0)]
    [InlineData(new[] { -2.0, 5.0 }, ClockSkewStatus.Normal, -2.0)]
    [InlineData(new[] { 121.0, 500.0 }, ClockSkewStatus.NoData, null)]
    [InlineData(new double[0], ClockSkewStatus.NoData, null)]
    public void AC_F3_8_clock_skew_is_estimated_from_minimum_live_latency_excluding_catch_up(
        double[] latencySeconds, ClockSkewStatus expectedStatus, double? expectedMinimumSeconds)
    {
        var samples = latencySeconds.Select(s => (_published, _published.AddSeconds(s)));

        var result = ClockSkewEstimator.Evaluate(samples);

        result.Status.ShouldBe(expectedStatus);
        (result.MinimumLatency?.TotalSeconds).ShouldBe(expectedMinimumSeconds);
    }
}
