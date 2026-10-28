using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Skywatch.Core.Deduplication;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;
using Skywatch.Worker.Tests.Fakes;

namespace Skywatch.Worker.Tests.Hosting;

// The run-mode host with FakeUpdateSource and FakeTimeProvider. Fake ids only.
public sealed class ProcessingServiceTests : IDisposable
{
    private const long _sourceA = -1000000000042;
    private const long _sourceB = -1000000000043;
    private const string _sourceAName = "Test source A";
    private const string _sourceBName = "Test source B";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly TestHost.TempFile _session = TestHost.ExistingSessionFile();

    public void Dispose() => _session.Dispose();

    private static ResolvedSource Resolved(long chatId, string name, bool noForwards = false) =>
        new(chatId, name, SourceType.Channel, null, noForwards, Resolved: true);

    private IncomingMessage Message(int id, string text, TimeSpan latency, long chatId = _sourceA)
    {
        var received = _time.GetUtcNow().UtcDateTime;
        return new IncomingMessage(chatId, id, text, received - latency, received, new OriginKey(chatId, id), IsEdit: false, EditedAtUtc: null);
    }

    private async Task<IHost> StartAsync(FakeUpdateSource source, Dictionary<string, string?>? extraSettings = null)
    {
        var settings = TestHost.RunModeSettings(_session.Path, (_sourceA, _sourceAName), (_sourceB, _sourceBName));
        foreach (var (key, value) in extraSettings ?? [])
        {
            settings[key] = value;
        }

        var host = TestHost.Build([], settings, _logs, source, _time);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private Task MessageLogged(int messageId) =>
        Eventually.ShouldHappen(() => _logs.Lines.Any(l => l.Contains(_sourceAName) && l.Contains($"message {messageId}")), $"message {messageId} logged");

    // AC-F2.3: an unresolvable enabled source → Warning naming the configured Name and ChatId; skipped, startup continues.
    [Fact]
    public async Task AC_F2_3_unresolved_source_logs_warning_with_name_and_chat_id_and_startup_continues()
    {
        var source = new FakeUpdateSource
        {
            Sources = [Resolved(_sourceA, _sourceAName), new ResolvedSource(_sourceB, _sourceBName, SourceType.Channel, null, false, Resolved: false)],
        };
        using var host = await StartAsync(source);

        source.Emit(Message(1, "Текст", TimeSpan.FromSeconds(1)));
        await MessageLogged(1);

        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains(_sourceBName) && e.Message.Contains(_sourceB.ToString(CultureInfo.InvariantCulture)));
        _logs.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning && e.Message.Contains(_sourceAName));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // AC-F2.4: at startup each source with noforwards = true is logged at Information.
    [Fact]
    public async Task AC_F2_4_noforwards_source_is_logged_at_information()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName, noForwards: true), Resolved(_sourceB, _sourceBName)] };
        using var host = await StartAsync(source);

        await Eventually.ShouldHappen(
            () => _logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains(_sourceAName) && e.Message.Contains("noforwards")),
            "noforwards source logged");
        _logs.Entries.ShouldNotContain(e => e.Message.Contains(_sourceBName) && e.Message.Contains("noforwards"));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Plan P1b temporary body: source name, message id, text length (not text) and ReceivedAt − PublishedAt latency.
    [Fact]
    public async Task P1b_incoming_message_is_logged_with_source_name_id_text_length_and_latency_but_not_text()
    {
        const string text = "Приватний текст повідомлення";
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        source.Emit(Message(5, text, TimeSpan.FromSeconds(3)));
        await MessageLogged(5);

        var line = _logs.Lines.Single(l => l.Contains("message 5"));
        line.ShouldContain(_sourceAName);
        line.ShouldContain($"{text.Length} chars");
        line.ShouldContain("latency 3");
        _logs.Lines.ShouldNotContain(l => l.Contains(text));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // AC-F3.7 (log part): disconnects and reconnects are logged.
    [Fact]
    public async Task AC_F3_7_disconnect_and_reconnect_are_logged()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        source.Emit(new ConnectionStateChanged(ConnectionState.Disconnected, _time.GetUtcNow().UtcDateTime));
        source.Emit(new ConnectionStateChanged(ConnectionState.Reconnected, _time.GetUtcNow().UtcDateTime));

        await Eventually.ShouldHappen(
            () => _logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
                && _logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("reconnected", StringComparison.OrdinalIgnoreCase)),
            "disconnect warning and reconnect information");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Plan P1b: after each event, if UpdateStateSaveIntervalSeconds (default 60) elapsed, TrySaveStateAsync.
    [Fact]
    public async Task P1b_update_state_is_saved_after_an_event_once_the_save_interval_elapsed()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        // Advance the clock only once the previous event, including its save check, is fully handled.
        source.Emit(Message(1, "Перше", TimeSpan.FromSeconds(1)));
        await Eventually.ShouldHappen(() => source.Handled == 1, "message 1 handled");
        _time.Advance(TimeSpan.FromSeconds(59));
        source.Emit(Message(2, "Друге", TimeSpan.FromSeconds(1)));
        await Eventually.ShouldHappen(() => source.Handled == 2, "message 2 handled");
        source.SaveCalls.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(1));
        source.Emit(Message(3, "Третє", TimeSpan.FromSeconds(1)));
        await MessageLogged(3);
        await Eventually.ShouldHappen(() => !source.SaveCalls.IsEmpty, "state saved after the interval");
        source.SaveCalls.ShouldHaveSingleItem().ShouldBe(3);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Plan P1b shutdown: stop reading, drain the channel through processing, then TrySaveStateAsync.
    [Fact]
    public async Task P1b_on_shutdown_queued_events_are_processed_then_state_is_saved()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)], HoldUntilStopped = true };
        using var host = await StartAsync(source);
        for (var i = 1; i <= 5; i++)
        {
            source.Emit(Message(i, $"Повідомлення {i}", TimeSpan.FromSeconds(1)));
        }

        await host.StopAsync(TestContext.Current.CancellationToken);

        for (var i = 1; i <= 5; i++)
        {
            _logs.Lines.ShouldContain(l => l.Contains($"message {i}"));
        }

        source.SaveCalls.ShouldNotBeEmpty();
        source.SaveCalls.Last().ShouldBe(5);
    }

    // AC-F3.8: hourly, Warning if the minimum live latency of the last hour exceeds 10 s.
    [Fact]
    public async Task AC_F3_8_hourly_clock_skew_check_warns_when_minimum_latency_exceeds_10_seconds()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        source.Emit(Message(1, "Пізнє", TimeSpan.FromSeconds(15)));
        await MessageLogged(1);
        _time.Advance(TimeSpan.FromHours(1));

        await Eventually.ShouldHappen(
            () => _logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("clock", StringComparison.OrdinalIgnoreCase)),
            "clock skew warning");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // AC-F3.8: catch-up messages (> 120 s) are excluded and only the last hour counts; no data → Debug, skip.
    [Fact]
    public async Task AC_F3_8_hourly_clock_skew_check_without_live_data_logs_debug_and_skips()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source, new() { ["Logging:LogLevel:Default"] = "Debug" });

        source.Emit(Message(1, "Наздогнане", TimeSpan.FromSeconds(300)));
        await MessageLogged(1);
        _time.Advance(TimeSpan.FromHours(1));

        await Eventually.ShouldHappen(
            () => _logs.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("clock", StringComparison.OrdinalIgnoreCase)),
            "clock skew debug without data");
        _logs.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning && e.Message.Contains("clock", StringComparison.OrdinalIgnoreCase));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AC_F3_8_clock_skew_check_ignores_messages_older_than_the_last_hour()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source, new() { ["Logging:LogLevel:Default"] = "Debug" });

        source.Emit(Message(1, "Пізнє", TimeSpan.FromSeconds(15)));
        await MessageLogged(1);
        _time.Advance(TimeSpan.FromHours(1));
        await Eventually.ShouldHappen(() => ClockEntries(LogLevel.Warning) == 1, "first hour warns");

        _time.Advance(TimeSpan.FromHours(1));

        await Eventually.ShouldHappen(() => ClockEntries(LogLevel.Debug) == 1, "second hour has no data");
        ClockEntries(LogLevel.Warning).ShouldBe(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Plan P2: ProcessingService logs matches with source, pattern names and latency; still no text.
    [Fact]
    public async Task P2_matching_message_is_logged_with_source_pattern_names_and_latency_but_not_text()
    {
        const string text = "Перевірка: тест зв'язку";
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        source.Emit(Message(7, text, TimeSpan.FromSeconds(2)));

        await Eventually.ShouldHappen(() => MatchLines(7).Any(), "match logged");
        var line = MatchLines(7).Single();
        line.ShouldContain(_sourceAName);
        line.ShouldContain(TestHost.ProbePattern);
        line.ShouldContain("latency 2");
        _logs.Lines.ShouldNotContain(l => l.Contains(text));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task P2_message_without_a_mention_logs_no_match()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source);

        source.Emit(Message(8, "Без згадок", TimeSpan.FromSeconds(1)));
        source.Emit(Message(9, "Ще один тест", TimeSpan.FromSeconds(1)));

        await Eventually.ShouldHappen(() => MatchLines(9).Any(), "the later matching message is logged");
        MatchLines(8).ShouldBeEmpty();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // AC-F5.10: a regex timeout is logged at Warning with the pattern name; the other patterns still run.
    [Fact]
    public async Task AC_F5_10_regex_timeout_is_logged_as_warning_with_pattern_name_and_processing_continues()
    {
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartAsync(source, new()
        {
            ["Monitoring:Patterns:0:Name"] = "Slow",
            ["Monitoring:Patterns:0:Regex"] = @"^(\w+\s?)*$",
            ["Monitoring:Patterns:1:Name"] = TestHost.ProbePattern,
            ["Monitoring:Patterns:1:Regex"] = @"\bтест\b",
        });

        source.Emit(Message(10, "тест " + new string('а', 5000) + "!", TimeSpan.FromSeconds(1)));

        await Eventually.ShouldHappen(() => MatchLines(10).Any(), "the message is still matched by the other pattern");
        _logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Slow") && e.Message.Contains("timed out"));
        MatchLines(10).Single().ShouldNotContain("Slow");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // AC-F2.2: resolved sources are upserted into Sources with type, username and the noforwards flag.
    [Fact]
    public async Task AC_F2_2_resolved_sources_are_upserted_with_type_username_and_noforwards()
    {
        var settings = Settings();
        var source = new FakeUpdateSource
        {
            Sources =
            [
                new ResolvedSource(_sourceA, _sourceAName, SourceType.Channel, "test_channel_a", NoForwards: true, Resolved: true),
                new ResolvedSource(_sourceB, _sourceBName, SourceType.Supergroup, null, NoForwards: false, Resolved: true),
            ],
        };
        using var host = await StartWithAsync(source, settings);
        await StartedRecorded(settings);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var rows = Query(settings, db => db.Sources.OrderBy(s => s.TelegramChatId).ToList());
        rows.Count.ShouldBe(2);
        var a = rows.Single(s => s.TelegramChatId == _sourceA);
        (a.Name, a.Type, a.Username, a.NoForwards, a.IsEnabled).ShouldBe((_sourceAName, SourceType.Channel, "test_channel_a", true, true));
        a.CreatedAtUtc.ShouldBe(_time.GetUtcNow().UtcDateTime);
        var b = rows.Single(s => s.TelegramChatId == _sourceB);
        (b.Name, b.Type, b.Username, b.NoForwards, b.IsEnabled).ShouldBe((_sourceBName, SourceType.Supergroup, (string?)null, false, true));
    }

    // AC-F2.2: on the next start an existing source is updated in place (same row, original CreatedAtUtc).
    [Fact]
    public async Task AC_F2_2_existing_source_is_updated_in_place_on_the_next_start()
    {
        var settings = Settings();
        await RunOnceAsync(settings, new ResolvedSource(_sourceA, "Old name", SourceType.Channel, null, NoForwards: false, Resolved: true));
        var created = Query(settings, db => db.Sources.Single());
        _time.Advance(TimeSpan.FromDays(1));

        await RunOnceAsync(settings, new ResolvedSource(_sourceA, _sourceAName, SourceType.Supergroup, "renamed_a", NoForwards: true, Resolved: true));

        var updated = Query(settings, db => db.Sources.Single());
        updated.Id.ShouldBe(created.Id);
        updated.CreatedAtUtc.ShouldBe(created.CreatedAtUtc);
        (updated.Name, updated.Type, updated.Username, updated.NoForwards, updated.IsEnabled).ShouldBe((_sourceAName, SourceType.Supergroup, "renamed_a", true, true));
    }

    // AC-F2.2: configuration is the source of truth for Enabled. A source no longer enabled in config is marked
    // disabled, and enabled again once it is back.
    [Fact]
    public async Task AC_F2_2_configuration_is_the_source_of_truth_for_enabled()
    {
        var settings = Settings();
        await RunOnceAsync(settings, Resolved(_sourceA, _sourceAName), Resolved(_sourceB, _sourceBName));

        await RunOnceAsync(settings, Resolved(_sourceA, _sourceAName));
        Query(settings, db => db.Sources.Single(s => s.TelegramChatId == _sourceB).IsEnabled).ShouldBeFalse();
        Query(settings, db => db.Sources.Single(s => s.TelegramChatId == _sourceA).IsEnabled).ShouldBeTrue();

        await RunOnceAsync(settings, Resolved(_sourceA, _sourceAName), Resolved(_sourceB, _sourceBName));
        Query(settings, db => db.Sources.Count(s => s.IsEnabled)).ShouldBe(2);
    }

    // AC-F2.2 / F2.3: only resolved sources are upserted; an unresolved one is skipped.
    [Fact]
    public async Task AC_F2_2_unresolved_source_is_not_inserted()
    {
        var settings = Settings();

        await RunOnceAsync(settings, Resolved(_sourceA, _sourceAName), new ResolvedSource(_sourceB, _sourceBName, SourceType.Channel, null, false, Resolved: false));

        Query(settings, db => db.Sources.Select(s => s.TelegramChatId).ToList()).ShouldBe([_sourceA]);
    }

    // AC-F3.7: disconnects and reconnects are recorded as ServiceEvents at the time they happened.
    [Fact]
    public async Task AC_F3_7_disconnect_and_reconnect_are_recorded_as_service_events()
    {
        var settings = Settings();
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartWithAsync(source, settings);
        var disconnectedAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-30);
        var reconnectedAt = _time.GetUtcNow().UtcDateTime;

        source.Emit(new ConnectionStateChanged(ConnectionState.Disconnected, disconnectedAt));
        source.Emit(new ConnectionStateChanged(ConnectionState.Reconnected, reconnectedAt));

        await Eventually.ShouldHappen(() => Events(settings).Any(e => e.Kind == ServiceEventKind.Reconnected), "reconnect recorded");
        var events = Events(settings).Where(e => e.Kind is ServiceEventKind.Disconnected or ServiceEventKind.Reconnected).ToList();
        events.Select(e => (e.Kind, e.OccurredAtUtc)).ShouldBe([(ServiceEventKind.Disconnected, disconnectedAt), (ServiceEventKind.Reconnected, reconnectedAt)]);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Plan P3a: ServiceEvents for Started and Stopped; Stopped after the channel is drained on shutdown.
    [Fact]
    public async Task P3a_started_and_stopped_are_recorded_as_service_events()
    {
        var settings = Settings();
        var source = new FakeUpdateSource { Sources = [Resolved(_sourceA, _sourceAName)] };
        using var host = await StartWithAsync(source, settings);
        await StartedRecorded(settings);
        Events(settings).ShouldNotContain(e => e.Kind == ServiceEventKind.Stopped);

        _time.Advance(TimeSpan.FromMinutes(5));
        await host.StopAsync(TestContext.Current.CancellationToken);

        var events = Events(settings);
        events.Select(e => e.Kind).ShouldBe([ServiceEventKind.Started, ServiceEventKind.Stopped]);
        events[1].OccurredAtUtc.ShouldBe(_time.GetUtcNow().UtcDateTime);
    }

    private Dictionary<string, string?> Settings() =>
        TestHost.RunModeSettings(_session.Path, (_sourceA, _sourceAName), (_sourceB, _sourceBName));

    private async Task<IHost> StartWithAsync(FakeUpdateSource source, Dictionary<string, string?> settings)
    {
        var host = TestHost.Build([], settings, _logs, source, _time);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>One full start → Started recorded → stop cycle against the same database.</summary>
    private async Task RunOnceAsync(Dictionary<string, string?> settings, params ResolvedSource[] sources)
    {
        // Counted before the start: ExecuteAsync runs in the background and may record Started before StartAsync returns.
        var before = File.Exists(settings["Storage:DatabasePath"]) ? Events(settings).Count(e => e.Kind == ServiceEventKind.Started) : 0;
        using var host = await StartWithAsync(new FakeUpdateSource { Sources = sources }, settings);
        await Eventually.ShouldHappen(() => Events(settings).Count(e => e.Kind == ServiceEventKind.Started) > before, "Started recorded");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Task StartedRecorded(Dictionary<string, string?> settings) =>
        Eventually.ShouldHappen(() => Events(settings).Any(e => e.Kind == ServiceEventKind.Started), "Started recorded");

    private static T Query<T>(Dictionary<string, string?> settings, Func<SkywatchDbContext, T> query)
    {
        using var db = SqliteTestDb.OpenFile(settings["Storage:DatabasePath"]!);
        return query(db);
    }

    private static List<ServiceEvent> Events(Dictionary<string, string?> settings) =>
        Query(settings, db => db.ServiceEvents.OrderBy(e => e.Id).ToList());

    private IEnumerable<string> MatchLines(int messageId) =>
        _logs.Lines.Where(l => l.Contains($"message {messageId} matched"));

    private int ClockEntries(LogLevel level) =>
        _logs.Entries.Count(e => e.Level == level && e.Message.Contains("clock", StringComparison.OrdinalIgnoreCase));
}
