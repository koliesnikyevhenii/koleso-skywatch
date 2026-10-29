using Skywatch.Worker.Options;

namespace Skywatch.Worker.Tests.Options;

// Spec §6 Reporting:DailyReportLocalTime (default 09:00, AC-F11.1): HH:mm, validated at start in run mode.
public class ReportingOptionsValidationTests
{
    [Theory]
    [InlineData("09:00", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("9:00", false)]
    [InlineData("24:00", false)]
    [InlineData("09:00:00", false)]
    [InlineData("", false)]
    [InlineData("ранок", false)]
    public void AC_F11_1_report_time_must_be_HH_mm(string value, bool valid)
    {
        var result = new ReportingOptionsValidator().Validate(null, new ReportingOptions { DailyReportLocalTime = value });

        result.Succeeded.ShouldBe(valid);
        if (!valid)
        {
            result.FailureMessage.ShouldNotBeNull().ShouldContain("Reporting:DailyReportLocalTime");
        }
    }

    [Fact]
    public void AC_F11_1_defaults_are_enabled_at_09_00() =>
        new ReportingOptions().ShouldSatisfyAllConditions(
            o => o.DailyReportEnabled.ShouldBeTrue(),
            o => o.ReportTime.ShouldBe(new TimeOnly(9, 0)));
}
