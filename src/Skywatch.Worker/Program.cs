using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

/// <summary>Run modes selected by the first argument (AC-F1.1–F1.3). <c>rescan</c> arrives in P6b.</summary>
internal enum RunMode
{
    Run,
    Login,
    ListChats,
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

        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var bridge = new WTelegramLogBridge(
            host.Services.GetRequiredService<ILoggerFactory>(),
            [configuration["Telegram:ApiHash"], configuration["Telegram:PhoneNumber"], configuration["Telegram:SessionKey"]]);
        bridge.Install();

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
            return mode == RunMode.Login
                ? await host.Services.GetRequiredService<LoginCommand>().RunAsync(bridge, ct)
                : await host.Services.GetRequiredService<ListChatsCommand>().RunAsync(ct);
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
        var builder = Host.CreateApplicationBuilder(args);
        AddUserSecrets(builder);
        configure?.Invoke(builder);

        var mode = ParseRunMode(args) ?? RunMode.Run;
        builder.Services.AddOptions<TelegramOptions>().BindConfiguration(TelegramOptions.SectionName).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<TelegramOptions>>(new TelegramOptionsValidator(mode));
        var monitoring = builder.Services.AddOptions<MonitoringOptions>().BindConfiguration(MonitoringOptions.SectionName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        if (mode == RunMode.Run)
        {
            monitoring.ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<MonitoringOptions>, MonitoringOptionsValidator>();
            builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
            builder.Services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName).ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<NotificationOptions>>(new NotificationOptionsValidator());
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
            if (builder.Configuration.GetValue<bool>($"{NotificationOptions.SectionName}:{nameof(NotificationOptions.Enabled)}"))
            {
                builder.Services.AddHostedService<NotificationDispatchService>();
                builder.Services.AddHostedService<StaleSummaryService>();
                builder.Services.AddSingleton<LabelService>();
                builder.Services.AddHostedService<BotCallbackListener>();
            }

            builder.Services.AddHostedService<RetentionService>();
        }

        builder.Services.AddSingleton<LoginCommand>();
        builder.Services.AddSingleton<ListChatsCommand>();

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
        _ => null,
    };

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

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unknown run mode '{Mode}'. Use `login`, `list-chats` or no argument.")]
    private static partial void LogUnknownRunMode(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Configuration is invalid: {Failures}")]
    private static partial void LogInvalidConfiguration(ILogger logger, IEnumerable<string> failures);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Mode} failed: {Error}")]
    private static partial void LogCommandFailed(ILogger logger, RunMode mode, string error);
}
