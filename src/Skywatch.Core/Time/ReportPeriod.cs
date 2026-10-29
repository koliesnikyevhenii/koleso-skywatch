namespace Skywatch.Core.Time;

/// <summary>
/// AC-F11.1: one local calendar day, 00:00–24:00 in the display time zone, as UTC bounds
/// (<see cref="StartUtc"/> inclusive, <see cref="EndUtc"/> exclusive). 23 or 25 hours long on DST change days.
/// </summary>
public sealed record ReportPeriod(DateOnly Day, DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Length => EndUtc - StartUtc;

    public static ReportPeriod ForDay(DateOnly day, TimeZoneInfo zone) =>
        new(day, LocalToUtc(day, TimeOnly.MinValue, zone), LocalToUtc(day.AddDays(1), TimeOnly.MinValue, zone));

    /// <summary>
    /// AC-F11.4: the most recent day whose report time has passed at <paramref name="nowUtc"/>: yesterday from the
    /// report time on, the day before yesterday until then.
    /// </summary>
    public static ReportPeriod LatestDue(DateTime nowUtc, TimeOnly reportTime, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var reportDay = nowUtc >= LocalToUtc(today, reportTime, zone) ? today.AddDays(-1) : today.AddDays(-2);
        return ForDay(reportDay, zone);
    }

    /// <summary>AC-F11.1: the first report time strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextReportTimeUtc(DateTime nowUtc, TimeOnly reportTime, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var next = LocalToUtc(today, reportTime, zone);
        return next > nowUtc ? next : LocalToUtc(today.AddDays(1), reportTime, zone);
    }

    /// <summary>
    /// A local time skipped by a DST jump moves forward past the gap; a repeated one takes its first occurrence.
    /// </summary>
    private static DateTime LocalToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
