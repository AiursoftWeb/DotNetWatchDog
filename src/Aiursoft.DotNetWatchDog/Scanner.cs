using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aiursoft.DotNetWatchDog;

internal sealed class Scanner(
    IServiceScopeFactory scopes,
    StateStore store,
    Settings settings,
    ILogger<Scanner> log) : BackgroundService
{
    internal async Task<int> ScanOnceAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var scope = scopes.CreateAsyncScope();
        var chReader = scope.ServiceProvider.GetRequiredService<ClickHouseLogReader>();
        var reporter = scope.ServiceProvider.GetRequiredService<IncidentReporter>();

        var tables = await chReader.DiscoverTablesAsync(ct);
        store.Remember(tables);

        var active = await reporter.GetActiveIncidentsAsync(ct);
        var activeNames = active
            .Where(x => x.Metadata.Value<string>("node") == settings.NodeName &&
                        x.Metadata.Value<string>("database") == settings.Database)
            .Select(x => x.Metadata.Value<string>("table"))
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.Ordinal);

        store.Remember(activeNames.Concat(store.PendingNames()));

        var missing = store.KnownNames().Except(tables).OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var missingTable in missing)
        {
            log.LogError("Known table {Table} missing from discovery; monitoring coverage lost", missingTable);
        }

        var failed = 0;
        var pendingFailed = 0;

        foreach (var table in tables)
        {
            try
            {
                ErrorCounts? counts;
                try
                {
                    counts = await chReader.CountErrorsAsync(table, nowUtc.AddMinutes(-15), nowUtc, ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to query error counts for table {Table}", table);
                    if (!string.IsNullOrEmpty(store.Pending(table)))
                    {
                        await reporter.ProcessTableAsync(table, null, active, nowUtc, ct);
                    }
                    throw;
                }

                await reporter.ProcessTableAsync(table, counts, active, nowUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                log.LogError(ex, "Table {Table} failed ({ErrorType})", table, ex.GetType().Name);
            }
        }

        foreach (var missingTable in missing)
        {
            try
            {
                await reporter.ProcessTableAsync(missingTable, null, active, nowUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                pendingFailed++;
                log.LogError(ex, "Pending incident for missing table {Table} failed ({ErrorType})", missingTable, ex.GetType().Name);
            }
        }

        stopwatch.Stop();
        var elapsedSec = stopwatch.Elapsed.TotalSeconds;
        log.LogInformation(
            "Scan tables={Total} succeeded={Succeeded} failed_tables={Failed} missing_tables={Missing} pending_failed={PendingFailed} seconds={Seconds:F3}",
            tables.Count,
            tables.Count - failed,
            failed,
            missing.Count,
            pendingFailed,
            elapsedSec);

        if (elapsedSec >= 300)
        {
            log.LogWarning("Scan exceeded 300 seconds");
        }

        return failed + missing.Count + pendingFailed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                var totalErrors = await ScanOnceAsync(started, stoppingToken);
                if (totalErrors == 0)
                {
                    var healthPath = Path.Combine(settings.DataDirectory, "healthy");
                    await File.WriteAllTextAsync(healthPath, started.ToString("O"), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scan cycle failed ({ErrorType})", ex.GetType().Name);
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var delay = TimeSpan.FromSeconds(300) - elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
