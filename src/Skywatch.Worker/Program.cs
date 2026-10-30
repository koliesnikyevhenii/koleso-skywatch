using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Core.Filtering;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Persistence;
using Skywatch.Worker.Pipeline;
using Skywatch.Worker.Telegram;
using Telegram.Bot;

return await Program.RunAsync(args, configure: null, CancellationToken.None);

/// <summary>Run modes selected by the first argument (AC-F1.1–F1.3, AC-F1.6).</summary>
internal enum RunMode
{
    Run,
    Login,
    ListChats,
    Rescan,
}

public partial class Program
{
    internal static async Task<int> RunAsync(string[] args, Action<HostApplicationBuilder>? configure, CancellationToken ct)
    {
        using var host = BuildHost(args, configure);
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        if (ParseRunMode(args) is not { } mode)
        {
            LogUnknownRunMode(logger, args[0]);
            return 2;
        }

        string? rescanPattern = null;
        if (mode == RunMode.Rescan && !TryParseRescanPattern(args, out rescanPattern))
        {
            LogInvalidRescanArguments(logger);
            return 2;
        }

        var bridge = host.Services.GetRequiredService<WTelegramLogBridge>();
        bridge.Install();

        // AC-F1.7: one process per session, checked before anything can connect (R22). Rescan never connects.
        var sessionPath = host.Services.GetRequiredService<IConfiguration>()[$"{TelegramOptions.SectionName}:{nameof(TelegramOptions.SessionPath)}"]
            ?? new TelegramOptions().SessionPath;
        using var sessionLock = mode == RunMode.Rescan || string.IsNullOrWhiteSpace(sessionPath) ? null : SessionFiles.TryLock(sessionPath);
        if (mode != RunMode.Rescan && !string.IsNullOrWhiteSpace(sessionPath) && sessionLock is null)
        {
            var lockPath = SessionFiles.LockPath(sessionPath);
            LogSessionInUse(logger, lockPath);
            return 1;
        }

        try
        {
            await host.StartAsync(ct);
        }
        catch (OptionsValidationException ex)
        {
            LogInvalidConfiguration(logger, ex.Failures);
            return 1;
        }

        if (mode == RunMode.Run)
        {
            await host.WaitForShutdownAsync(ct);
            return host.Services.GetRequiredService<ProcessingService>().Failed ? 1 : 0;
        }

        try
        {
            return mode switch
            {
                RunMode.Login => await host.Services.GetRequiredService<LoginCommand>().RunAsync(bridge, ct),
                RunMode.Rescan => await host.Services.GetRequiredService<RescanCommand>().RunAsync(rescanPattern, Console.Out, ct),
                _ => await host.Services.GetRequiredService<ListChatsCommand>().RunAsync(ct),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = bridge.Redact(ex.Message);
            LogCommandFailed(logger, mode, error);
            return 1;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    internal static IHost BuildHost(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        // A Windows Service starts in System32: appsettings.json and the relative data/ paths live next to the exe.
        if (WindowsServiceHelpers.IsWindowsService())
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }

        var builder = Host.CreateApplicationBuilder(args);
        AddUserSecrets(builder);
        configure?.Invoke(builder);

        var mode = ParseRunMode(args) ?? RunMode.Run;
        var telegram = builder.Services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName);
        var monitoring = builder.Services.AddOptions<MonitoringOptions>().BindConfiguration(MonitoringOptions.SectionName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        if (mode == RunMode.Rescan)
        {
            // AC-F1.6: database and patterns only (no Telegram settings, no session); read-only, no migrations.
            monitoring.ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<MonitoringOptions>, MonitoringOptionsValidator>();
            builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
            builder.Services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName);
            builder.Services.AddSingleton(sp => TimeZoneResolver.ResolveOrThrow(sp.GetRequiredService<IOptions<NotificationOptions>>().Value.DisplayTimeZone));
            builder.Services.AddDbContextFactory<SkywatchDbContext>(
                (sp, db) => db.UseSqlite(new SqliteConnectionStringBuilder(sp.GetRequiredService<IOptions<StorageOptions>>().Value.ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString()));
            builder.Services.AddSingleton<RescanCommand>();
        }
        else
        {
            telegram.ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<TelegramOptions>>(new TelegramOptionsValidator(mode));
        }

        if (mode == RunMode.Run)
        {
            // N4: the stop waits for the channel drain and the Stopped event; Docker's stop_grace_period is longer.
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);

            // N7: a Windows Service when started by the service control manager; a no-op otherwise.
            builder.Services.AddWindowsService(o => o.ServiceName = "Skywatch");

            monitoring.ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<MonitoringOptions>, MonitoringOptionsValidator>();
            builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
            builder.Services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<NotificationOptions>>(new NotificationOptionsValidator());
            builder.Services.AddOptions<ReportingOptions>().BindConfiguration(ReportingOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<ReportingOptions>, ReportingOptionsValidator>();
            builder.Services.AddSingleton(sp => TimeZoneResolver.ResolveOrThrow(sp.GetRequiredService<IOptions<NotificationOptions>>().Value.DisplayTimeZone));

            // Resolved only when something sends, which requires Notifications:Enabled (and so a BotToken).
            builder.Services.TryAddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(sp.GetRequiredService<IOptions<NotificationOptions>>().Value.BotToken));
            builder.Services.TryAddSingleton<INotificationSender, BotNotificationSender>();
            builder.Services.AddDbContextFactory<SkywatchDbContext>(
                (sp, db) => db.UseSqlite(sp.GetRequiredService<IOptions<StorageOptions>>().Value.ConnectionString));

            // Hosted services start in registration order: the database is migrated before anything uses it.
            builder.Services.AddHostedService<DatabaseInitializer>();

            // TryAdd: tests register FakeUpdateSource first.
            builder.Services.TryAddSingleton<ITelegramUpdateSource, WTelegramUpdateSource>();
            builder.Services.AddSingleton(CreateKeywordFilter);
            builder.Services.AddSingleton<DuplicateDetector>();
            builder.Services.AddSingleton<MessageProcessor>();
            builder.Services.AddSingleton<EditHandler>();
            builder.Services.AddSingleton<EditReplyService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<EditReplyService>());
            builder.Services.AddSingleton<ProcessingService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingService>());
            builder.Services.AddSingleton<DispatchSignal>();

            // With notifications disabled nothing is ever Pending, and the bot client (which needs a token) is never built.
            var notificationsEnabled = builder.Configuration.GetValue<bool>($"{NotificationOptions.SectionName}:{nameof(NotificationOptions.Enabled)}");
            if (notificationsEnabled)
            {
                builder.Services.AddHostedService<NotificationDispatchService>();
                builder.Services.AddHostedService<StaleSummaryService>();
                builder.Services.AddSingleton<LabelService>();
                builder.Services.AddHostedService<BotCallbackListener>();
            }

            builder.Services.AddHostedService<RetentionService>();

            // AC-F11.3: with notifications disabled the report goes to the log.
            if (builder.Configuration.GetValue($"{ReportingOptions.SectionName}:{nameof(ReportingOptions.DailyReportEnabled)}", true))
            {
                builder.Services.AddSingleton<DailyReportBuilder>();
                builder.Services.AddHostedService(sp => new DailyReportService(
                    sp.GetRequiredService<IDbContextFactory<SkywatchDbContext>>(),
                    sp.GetRequiredService<DailyReportBuilder>(),
                    notificationsEnabled ? sp.GetRequiredService<INotificationSender>() : null,
                    sp.GetRequiredService<IOptions<ReportingOptions>>(),
                    sp.GetRequiredService<TimeZoneInfo>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<DailyReportService>>()));
            }
        }

        builder.Services.AddSingleton<LoginCommand>();
        builder.Services.AddSingleton<ListChatsCommand>();
        builder.Services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new WTelegramLogBridge(
                sp.GetRequiredService<ILoggerFactory>(),
                [configuration["Telegram:ApiHash"], configuration["Telegram:PhoneNumber"], configuration["Telegram:SessionKey"]]);
        });

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() =>
            {
                LogStarted(logger);
                if (mode == RunMode.Run)
                {
                    var zone = host.Services.GetRequiredService<TimeZoneInfo>();
                    var now = TimeZoneInfo.ConvertTime(host.Services.GetRequiredService<TimeProvider>().GetUtcNow(), zone);
                    LogDisplayTimeZone(logger, zone.Id, now);
                }
            });
        return host;
    }

    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Patterns are compiled once (AC-F5.9); a match timeout is logged with the pattern name (AC-F5.10).</summary>
    private static KeywordFilter CreateKeywordFilter(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<KeywordFilter>>();
        var patterns = services.GetRequiredService<IOptions<MonitoringOptions>>().Value.Patterns.Select(p => p.ToDefinition());
        return new KeywordFilter(patterns, name => LogPatternTimedOut(logger, name));
    }

    internal static RunMode? ParseRunMode(string[] args) => args switch
    {
        [] => RunMode.Run,
        [var first, ..] when first.StartsWith('-') => RunMode.Run, // host switches such as --environment
        ["login", ..] => RunMode.Login,
        ["list-chats", ..] => RunMode.ListChats,
        ["rescan", ..] => RunMode.Rescan,
        _ => null,
    };

    /// <summary>AC-F1.6: <c>rescan</c> alone, or <c>rescan --pattern &lt;regex&gt;</c>.</summary>
    internal static bool TryParseRescanPattern(string[] args, out string? pattern)
    {
        pattern = null;
        switch (args)
        {
            case ["rescan"]:
                return true;
            case ["rescan", RescanCommand.OneOffPatternName, var regex] when !string.IsNullOrWhiteSpace(regex):
                pattern = regex;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// CreateApplicationBuilder loads User Secrets only in Development, but `dotnet run` defaults to Production.
    /// They go right after the appsettings files, so environment variables and the command line still win.
    /// </summary>
    private static void AddUserSecrets(HostApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        var sources = builder.Configuration.Sources;
        var lastJson = sources.Select((source, index) => (source, index)).LastOrDefault(x => x.source is JsonConfigurationSource).index;
        var userSecrets = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).Sources[0];
        sources.Insert(lastJson + 1, userSecrets);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skywatch started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Display time zone {Zone}; local time now {Now:yyyy-MM-dd HH:mm:ss zzz}.")]
    private static partial void LogDisplayTimeZone(ILogger logger, string zone, DateTimeOffset now);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pattern '{Pattern}' timed out and is treated as no match for this message.")]
    private static partial void LogPatternTimedOut(ILogger logger, string pattern);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unknown run mode '{Mode}'. Use `login`, `list-chats`, `rescan` or no argument.")]
    private static partial void LogUnknownRunMode(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Invalid arguments. Use `rescan` or `rescan --pattern \"<regex>\"`.")]
    private static partial void LogInvalidRescanArguments(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Another Skywatch process is using this session (lock '{LockPath}'). Stop it first (`docker compose stop`), then retry.")]
    private static partial void LogSessionInUse(ILogger logger, string lockPath);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Configuration is invalid: {Failures}")]
    private static partial void LogInvalidConfiguration(ILogger logger, IEnumerable<string> failures);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Mode} failed: {Error}")]
    private static partial void LogCommandFailed(ILogger logger, RunMode mode, string error);
}
