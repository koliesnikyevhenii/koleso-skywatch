using Skywatch.Core.Time;

namespace Skywatch.Core.Tests.Time;

// Spec AC-F11.1 (previous local day, 23 or 25 hours on DST change days) and AC-F11.4 (latest due day). Kyiv 2026:
// summer time starts 29 Mar (03:00 EET → 04:00 EEST at 01:00 UTC) and ends 25 Oct (04:00 EEST → 03:00 EET at 01:00 UTC).
public class ReportPeriodTests
{
    private static readonly TimeZoneInfo _kyiv = FindKyiv();
    private static readonly TimeOnly _nine = new(9, 0);

    private static TimeZoneInfo FindKyiv()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kiev");
        }
    }

    private static DateTime Utc(int month, int day, int hour, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    [Theory]
    [InlineData(10, 25, 25, 10, 24, 21, 10, 25, 22)] // summer time ends
    [InlineData(3, 29, 23, 3, 28, 22, 3, 29, 21)] // summer time starts
    [InlineData(10, 27, 24, 10, 26, 22, 10, 27, 22)] // winter
    [InlineData(7, 1, 24, 6, 30, 21, 7, 1, 21)] // summer
    public void AC_F11_1_day_is_local_midnight_to_midnight_in_utc(
        int month, int day, int hours, int startMonth, int startDay, int startHour, int endMonth, int endDay, int endHour)
    {
        var period = ReportPeriod.ForDay(new DateOnly(2026, month, day), _kyiv);

        period.StartUtc.ShouldBe(Utc(startMonth, startDay, startHour));
        period.EndUtc.ShouldBe(Utc(endMonth, endDay, endHour));
        period.StartUtc.Kind.ShouldBe(DateTimeKind.Utc);
        period.EndUtc.Kind.ShouldBe(DateTimeKind.Utc);
        period.Length.ShouldBe(TimeSpan.FromHours(hours));
    }

    // AC-F11.1/F11.4: from 09:00 local the previous day is due; before 09:00 the day before it still is.
    [Theory]
    [InlineData(10, 27, 7, 0, 0, 26)] // 09:00 EET
    [InlineData(10, 27, 6, 59, 59, 25)] // 08:59:59 EET
    [InlineData(10, 27, 21, 59, 59, 26)] // 23:59:59 EET
    [InlineData(10, 27, 22, 0, 0, 26)] // 00:00 on the 28th: before 09:00, so the 26th is still the latest due day
    [InlineData(7, 2, 6, 0, 0, 1)] // 09:00 EEST
    [InlineData(7, 2, 5, 59, 0, 30)] // 08:59 EEST: 30 June
    public void AC_F11_4_latest_due_day_depends_on_the_local_report_time(int month, int day, int hour, int minute, int second, int expectedDay)
    {
        var due = ReportPeriod.LatestDue(Utc(month, day, hour, minute, second), _nine, _kyiv);

        due.Day.Day.ShouldBe(expectedDay);
    }

    // AC-F11.1: the next report time is 09:00 local, strictly after now, across the DST change.
    [Theory]
    [InlineData(10, 27, 6, 59, 10, 27, 7)]
    [InlineData(10, 27, 7, 0, 10, 28, 7)]
    [InlineData(7, 1, 5, 59, 7, 1, 6)]
    [InlineData(10, 24, 6, 0, 10, 25, 7)] // 09:00 EEST → next day 09:00 EET, 25 hours later
    [InlineData(3, 28, 7, 0, 3, 29, 6)] // 09:00 EET → next day 09:00 EEST, 23 hours later
    public void AC_F11_1_next_report_time_is_the_next_local_report_time(int month, int day, int hour, int minute, int nextMonth, int nextDay, int nextHour) =>
        ReportPeriod.NextReportTimeUtc(Utc(month, day, hour, minute), _nine, _kyiv).ShouldBe(Utc(nextMonth, nextDay, nextHour));

    // A report time inside the spring gap moves past it; one in the repeated autumn hour takes its first occurrence.
    [Fact]
    public void AC_F11_1_report_time_in_a_dst_gap_or_overlap_is_well_defined()
    {
        var halfPastThree = new TimeOnly(3, 30);

        ReportPeriod.NextReportTimeUtc(Utc(3, 28, 12), halfPastThree, _kyiv).ShouldBe(Utc(3, 29, 1)); // 04:00 EEST
        ReportPeriod.NextReportTimeUtc(Utc(10, 24, 12), halfPastThree, _kyiv).ShouldBe(Utc(10, 25, 0, 30)); // 03:30 EEST
    }
}
