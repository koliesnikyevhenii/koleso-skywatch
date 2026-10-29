using System.Globalization;
using Microsoft.Extensions.Options;

namespace Skywatch.Worker.Options;

internal sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    /// <summary>AC-F11.1: sent by the bot only when <c>Notifications:Enabled</c> is true too; logged otherwise (AC-F11.3).</summary>
    public bool DailyReportEnabled { get; set; } = true;

    /// <summary>AC-F11.1: <c>HH:mm</c> in <c>Notifications:DisplayTimeZone</c>.</summary>
    public string DailyReportLocalTime { get; set; } = "09:00";

    public TimeOnly ReportTime => TimeOnly.ParseExact(DailyReportLocalTime, "HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Run mode only (spec §6). Failure messages name keys.</summary>
internal sealed class ReportingOptionsValidator : IValidateOptions<ReportingOptions>
{
    public ValidateOptionsResult Validate(string? name, ReportingOptions options) =>
        TimeOnly.TryParseExact(options.DailyReportLocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Reporting:DailyReportLocalTime '{options.DailyReportLocalTime}' must be a local time as HH:mm, e.g. 09:00.");
}
