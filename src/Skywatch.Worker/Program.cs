using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skywatch.Worker.Hosting;
using Skywatch.Worker.Options;
using Skywatch.Worker.Telegram;

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

            // TryAdd: tests register FakeUpdateSource first.
            builder.Services.TryAddSingleton<ITelegramUpdateSource, WTelegramUpdateSource>();
            builder.Services.AddSingleton<ProcessingService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingService>());
        }

        builder.Services.AddSingleton<LoginCommand>();
        builder.Services.AddSingleton<ListChatsCommand>();

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() => LogStarted(logger));
        return host;
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unknown run mode '{Mode}'. Use `login`, `list-chats` or no argument.")]
    private static partial void LogUnknownRunMode(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Configuration is invalid: {Failures}")]
    private static partial void LogInvalidConfiguration(ILogger logger, IEnumerable<string> failures);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Mode} failed: {Error}")]
    private static partial void LogCommandFailed(ILogger logger, RunMode mode, string error);
}
