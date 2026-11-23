using Aiursoft.AlertCenter.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aiursoft.DotNetWatchDog;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var dataDir = Environment.GetEnvironmentVariable("STATE_DIR") ??
                      Environment.GetEnvironmentVariable("DATA_DIRECTORY") ??
                      "/data";
        var healthFile = Path.Combine(dataDir, "healthy");

        if (args is ["--healthcheck"])
        {
            return IsHealthy(healthFile) ? 0 : 1;
        }

        var settings = Settings.Load();
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddScoped<ClickHouseLogReader>();
        builder.Services.AddScoped<IncidentReporter>();
        builder.Services.AddAlertCenterSdk(settings.AlertCenterUrl, settings.AlertCenterKey);

        if (args is ["--once"])
        {
            builder.Services.AddScoped<Scanner>();
            using var onceHost = builder.Build();
            using var store = onceHost.Services.GetRequiredService<StateStore>();
            using var scope = onceHost.Services.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<Scanner>();
            var failureCount = await scanner.ScanOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            if (failureCount == 0)
            {
                await File.WriteAllTextAsync(healthFile, DateTimeOffset.UtcNow.ToString("O"));
            }
            return failureCount == 0 ? 0 : 1;
        }

        builder.Services.AddHostedService<Scanner>();
        using var host = builder.Build();
        // StateStore acquires monitor.lock file lease upon instantiation
        _ = host.Services.GetRequiredService<StateStore>();
        await host.RunAsync();
        return 0;
    }

    private static bool IsHealthy(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var now = DateTime.UtcNow;
            return now >= lastWrite && now - lastWrite < TimeSpan.FromMinutes(15);
        }
        catch
        {
            return false;
        }
    }
}
