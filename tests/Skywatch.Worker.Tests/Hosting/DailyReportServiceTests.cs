using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// Spec AC-F11.1, F11.3, F11.4 with the default report time 09:00 Kyiv (07:00 UTC in winter). SQLite in-memory,
// FakeNotificationSender, fake clock. Fake ids only.
public sealed class DailyReportServiceTests : IDisposable
{
    private static readonly TimeZoneInfo _kyiv = TimeZoneResolver.ResolveOrThrow(TimeZoneResolver.Kyiv);
    private static readonly int[] _retryWaitSeconds = [30, 60, 120, 240, 480, 900, 900];

    private readonly SqliteTestDb _db = new(concurrent: true);
    private readonly FakeNotificationSender _sender = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private TimerCountingTimeProvider _time = At(8, 0);

    /// <summary>The service has been running since October: every day is reportable.</summary>
    public DailyReportServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        using var db = _db.CreateDbContext();
        db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.Started, OccurredAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _db.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>2026-10-27 at the given Kyiv winter time (UTC+2).</summary>
    private static TimerCountingTimeProvider At(int kyivHour, int minute) =>
        new(new DateTimeOffset(2026, 10, 27, kyivHour, minute, 0, TimeSpan.FromHours(2)));

    private DailyReportService Service(bool notificationsEnabled = true) =>
        new(
            _db,
            new DailyReportBuilder(_db, Microsoft.Extensions.Options.Options.Create(new MonitoringOptions())),
            notificationsEnabled ? _sender : null,
            Microsoft.Extensions.Options.Options.Create(new ReportingOptions()),
            _kyiv,
            _time,
            _loggerFactory.CreateLogger<DailyReportService>());

    private void ReportedAlready(string day)
    {
        using var db = _db.CreateDbContext();
        db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.DailyReportSent, OccurredAtUtc = DateTime.UnixEpoch, Detail = day });
        db.SaveChanges();
    }

    private async Task<List<string?>> ReportedDaysAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.ServiceEvents.Where(e => e.Kind == ServiceEventKind.DailyReportSent).OrderBy(e => e.Id).Select(e => e.Detail).ToListAsync(Ct);
    }

    // AC-F11.1: at 09:00 local the report for the previous local day is sent, at low priority, without buttons.
    [Fact]
    public async Task AC_F11_1_report_for_the_previous_day_is_sent_at_09_00_local()
    {
        ReportedAlready("2026-10-25");
        using var service = Service();
        await service.StartAsync(Ct);
        await _time.TimersCreatedAtLeast(1);

        _time.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromTicks(1));
        _sender.Sends.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromTicks(1));
        await Eventually.ShouldHappenAsync(async () => (await ReportedDaysAsync()).Count == 2, "report recorded");

        var sent = _sender.Sends.ShouldHaveSingleItem();
        sent.Notification.Text.ShouldStartWith("📊 Звіт за 26.10.2026 (24 год)");
        sent.Notification.Priority.ShouldBe(SendPriority.Background);
        sent.Notification.LabelNotificationId.ShouldBeNull();
        (await ReportedDaysAsync()).ShouldBe(["2026-10-25", "2026-10-26"]);

        // The next one is due a day later.
        await _time.TimersCreatedAtLeast(2);
        _time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        _sender.Sends.Count.ShouldBe(1);
        await service.StopAsync(Ct);
    }

    // AC-F11.4: down at 09:00 and started at 11:00 → the missed report is sent once; a restart does not resend it.
    [Fact]
    public async Task AC_F11_4_missed_report_is_sent_once_on_start_and_not_again_after_a_restart()
    {
        _time = At(11, 0);
        using (var service = Service())
        {
            await service.StartAsync(Ct);
            await Eventually.ShouldHappen(() => _sender.Sends.Count == 1, "missed report sent");
            await _time.TimersCreatedAtLeast(1);
            _time.Advance(TimeSpan.FromHours(1));
            await service.StopAsync(Ct);
        }

        using (var restarted = Service())
        {
            await restarted.StartAsync(Ct);
            await _time.TimersCreatedAtLeast(2);
            await restarted.StopAsync(Ct);
        }

        _sender.Sends.ShouldHaveSingleItem().Notification.Text.ShouldStartWith("📊 Звіт за 26.10.2026");
        (await ReportedDaysAsync()).ShouldBe(["2026-10-26"]);
    }

    // AC-F11.4: started at 08:00 with the 25th never reported → that day is caught up (the 26th is due at 09:00).
    [Fact]
    public async Task AC_F11_4_start_before_the_report_time_catches_up_the_day_before_yesterday()
    {
        using var service = Service();

        (await service.RunOnceAsync(Ct)).ShouldBeTrue();

        _sender.Sends.ShouldHaveSingleItem().Notification.Text.ShouldStartWith("📊 Звіт за 25.10.2026 (25 год)");
        (await ReportedDaysAsync()).ShouldBe(["2026-10-25"]);
    }

    // Plan P6b: a failed send writes no DailyReportSent and is retried with backoff (30 s doubling, at most 15 min)
    // until it succeeds.
    [Fact]
    public async Task AC_F11_4_failed_send_is_not_recorded_and_is_retried_with_backoff()
    {
        _time = At(9, 0);
        TimeSpan[] waits = [.. _retryWaitSeconds.Select(s => TimeSpan.FromSeconds(s))];
        _sender.Script([.. waits.Select((_, i) => i % 2 == 0 ? (SendResult)new SendResult.Transient("timeout", DeliveryUnknown: true) : new SendResult.Fatal("Bad Request"))]);
        using var service = Service();
        await service.StartAsync(Ct);

        for (var attempt = 1; attempt <= waits.Length; attempt++)
        {
            await Eventually.ShouldHappen(() => _sender.Sends.Count == attempt, $"attempt {attempt}");
            await _time.TimersCreatedAtLeast(attempt);
            (await ReportedDaysAsync()).ShouldBeEmpty();
            _time.Advance(waits[attempt - 1] - TimeSpan.FromTicks(1));
            _sender.Sends.Count.ShouldBe(attempt, $"retry {attempt} waits {waits[attempt - 1]}");
            _time.Advance(TimeSpan.FromTicks(1));
        }

        await Eventually.ShouldHappenAsync(async () => (await ReportedDaysAsync()).Count == 1, "last attempt recorded");
        _sender.Sends.Count.ShouldBe(waits.Length + 1);
        (await ReportedDaysAsync()).ShouldBe(["2026-10-26"]);
        _logs.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(waits.Length);
        await service.StopAsync(Ct);
    }

    // AC-F11.4 "missed": on the very first start there is no missed report; the first one covers the first day run.
    [Fact]
    public async Task AC_F11_4_first_start_ever_sends_no_report_for_days_before_it()
    {
        _time = At(11, 0); // the 26th is due, and ended 11 hours before the first start
        await using (var db = _db.CreateDbContext())
        {
            await db.ServiceEvents.ExecuteDeleteAsync(Ct);
            db.ServiceEvents.Add(new ServiceEvent { Kind = ServiceEventKind.Started, OccurredAtUtc = _time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(Ct);
        }

        using var service = Service();
        (await service.RunOnceAsync(Ct)).ShouldBeTrue();
        _sender.Sends.ShouldBeEmpty();
        (await ReportedDaysAsync()).ShouldBeEmpty();

        _time.Advance(TimeSpan.FromHours(22)); // 09:00 on the 28th: the 27th, the first day run, is due
        (await service.RunOnceAsync(Ct)).ShouldBeTrue();
        _sender.Sends.ShouldHaveSingleItem().Notification.Text.ShouldStartWith("📊 Звіт за 27.10.2026");
    }

    // AC-F11.3: reporting enabled, notifications disabled → the report goes to the log at Information, once.
    [Fact]
    public async Task AC_F11_3_with_notifications_disabled_the_report_is_logged()
    {
        _time = At(9, 0);
        using var service = Service(notificationsEnabled: false);

        (await service.RunOnceAsync(Ct)).ShouldBeTrue();
        (await service.RunOnceAsync(Ct)).ShouldBeTrue();

        _sender.Sends.ShouldBeEmpty();
        _logs.Entries.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Level.ShouldBe(LogLevel.Information),
            e => e.Message.ShouldContain("📊 Звіт за 26.10.2026"));
        (await ReportedDaysAsync()).ShouldBe(["2026-10-26"]);
    }

    // AC-F11.1/F11.3 wiring: the run-mode host runs the report service unless Reporting:DailyReportEnabled is false;
    // with notifications disabled it needs no bot token.
    [Theory]
    [InlineData(null, 1)]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    public void AC_F11_1_host_runs_the_report_service_unless_disabled(string? enabled, int expected)
    {
        using var session = TestHost.ExistingSessionFile();
        var settings = TestHost.RunModeSettings(session.Path, (-1000000000042, "Test source A"));
        if (enabled is not null)
        {
            settings["Reporting:DailyReportEnabled"] = enabled;
        }

        using var host = TestHost.Build([], settings);

        host.Services.GetServices<IHostedService>().OfType<DailyReportService>().Count().ShouldBe(expected);
    }
}
