using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using var host = Program.BuildHost(args);
await host.RunAsync();

public partial class Program
{
    internal static IHost BuildHost(string[] args, Action<ILoggingBuilder>? configureLogging = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configureLogging?.Invoke(builder.Logging);

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() => LogStarted(logger));
        return host;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skywatch started")]
    private static partial void LogStarted(ILogger logger);
}
