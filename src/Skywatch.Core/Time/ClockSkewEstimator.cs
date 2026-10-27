namespace Skywatch.Core.Time;

public enum ClockSkewStatus
{
    NoData,
    Normal,
    Suspect,
}

public sealed record ClockSkewResult(ClockSkewStatus Status, TimeSpan? MinimumLatency);

/// <summary>Passive clock-skew estimate from live-message latency (AC-F3.8).</summary>
public static class ClockSkewEstimator
{
    private static readonly TimeSpan _catchUpThreshold = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _maxMinimumLatency = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _minLatency = TimeSpan.FromSeconds(-2);

    /// <summary>
    /// Minimum of <c>received − published</c>, ignoring catch-up (over 120 s). Suspect when the minimum exceeds 10 s
    /// or any value is below −2 s — the latter is the same as the minimum being below −2 s.
    /// </summary>
    public static ClockSkewResult Evaluate(IEnumerable<(DateTime PublishedUtc, DateTime ReceivedUtc)> samples)
    {
        TimeSpan? minimum = null;
        foreach (var (publishedUtc, receivedUtc) in samples)
        {
            var latency = receivedUtc - publishedUtc;
            if (latency <= _catchUpThreshold && (minimum is null || latency < minimum))
            {
                minimum = latency;
            }
        }

        if (minimum is not { } min)
        {
            return new ClockSkewResult(ClockSkewStatus.NoData, null);
        }

        var suspect = min > _maxMinimumLatency || min < _minLatency;
        return new ClockSkewResult(suspect ? ClockSkewStatus.Suspect : ClockSkewStatus.Normal, min);
    }
}
