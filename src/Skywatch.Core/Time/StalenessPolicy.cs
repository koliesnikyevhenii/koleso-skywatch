namespace Skywatch.Core.Time;

/// <summary>Spec F7: a message older than <c>Notifications:MaxMessageAgeSeconds</c> is never sent.</summary>
public sealed class StalenessPolicy(TimeSpan maxAge)
{
    /// <summary>
    /// Stale when more than the maximum age has passed since <paramref name="eventTimeUtc"/> (publication, or the
    /// edit for edit-triggered notifications, AC-F7.3). Exactly the maximum age is not stale.
    /// </summary>
    public bool IsStale(DateTime eventTimeUtc, DateTime nowUtc) => nowUtc - eventTimeUtc > maxAge;
}
