using System.Globalization;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DotNetWatchDog;

internal sealed class IncidentReporter(
    AlertCenterApiClient api,
    Settings settings,
    StateStore store,
    ClickHouseLogReader chReader,
    ILogger<IncidentReporter> log)
{
    internal async Task<List<IncidentDetails>> GetActiveIncidentsAsync(CancellationToken ct)
    {
        var active = new List<IncidentDetails>();
        var sourcesToQuery = new HashSet<string>(StringComparer.Ordinal) { settings.Source };
        if (settings.Source != "clickhouse-app-log-error-monitor")
        {
            sourcesToQuery.Add("clickhouse-app-log-error-monitor");
        }

        foreach (var sourceVal in sourcesToQuery)
        {
            for (var page = 1; ; page++)
            {
                var result = await api.QueryIncidentsAsync(new QueryIncidentsRequest
                {
                    ActiveOnly = true,
                    MetadataKey = "source",
                    MetadataValue = sourceVal,
                    Page = page,
                    PageSize = 100
                }).WaitAsync(ct);

                foreach (var item in result.Items)
                {
                    if (active.All(x => x.IncidentId != item.IncidentId))
                    {
                        active.Add(item);
                    }
                }

                if (active.Count >= result.TotalCount || result.Items.Count == 0)
                {
                    break;
                }
            }
        }

        return active;
    }

    private bool Matches(IncidentDetails incident, string table)
    {
        var node = incident.Metadata.Value<string>("node");
        var db = incident.Metadata.Value<string>("database");
        var tbl = incident.Metadata.Value<string>("table");
        return string.Equals(node, settings.NodeName, StringComparison.Ordinal) &&
               string.Equals(db, settings.Database, StringComparison.Ordinal) &&
               string.Equals(tbl, table, StringComparison.Ordinal);
    }

    private static DateTimeOffset? GetOriginalWindowEnd(IncidentDetails incident)
    {
        var context = incident.Context;
        if (context != null)
        {
            var endStr = context.Value<string>("WindowEndUtc");
            if (!string.IsNullOrEmpty(endStr) && DateTimeOffset.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return end.ToUniversalTime();
            }
        }

        if (incident.OccurredAtUtc != default)
        {
            return incident.OccurredAtUtc.ToUniversalTime();
        }

        return null;
    }

    internal async Task ProcessTableAsync(
        string table,
        ErrorCounts? counts,
        IReadOnlyList<IncidentDetails> activeIncidents,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var matches = activeIncidents.Where(x => Matches(x, table)).ToList();
        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"Duplicate active incident identity for table '{table}'");
        }

        IncidentDetails? current = null;
        var pendingRaw = store.Pending(table);
        if (!string.IsNullOrEmpty(pendingRaw))
        {
            var pending = JsonConvert.DeserializeObject<CreateIncidentRequest>(pendingRaw)
                ?? throw new InvalidOperationException($"Corrupted pending payload for table '{table}'");

            if (matches.Count > 0 && matches[0].ReportId != pending.ReportId)
            {
                throw new InvalidOperationException($"Pending report conflicts with active incident for table '{table}'");
            }

            var createResp = await api.CreateIncidentAsync(pending).WaitAsync(ct);
            var verified = (await api.GetIncidentAsync(createResp.IncidentId).WaitAsync(ct)).Incident;
            if (verified.IncidentId != createResp.IncidentId || verified.ReportId != pending.ReportId)
            {
                throw new InvalidOperationException("Receipt identity mismatch for pending incident create");
            }

            store.Delete(table);
            current = verified;
        }
        else if (matches.Count > 0)
        {
            current = (await api.GetIncidentAsync(matches[0].IncidentId).WaitAsync(ct)).Incident;
        }

        if (counts == null)
        {
            return;
        }

        if (current is { Status: "resolved" or "mitigated" })
        {
            current = null;
        }

        var wantedSeverity = SeverityHelper.GetSeverity(counts.Total);
        var shouldSample = wantedSeverity != null && (current == null || SeverityHelper.IsHigherSeverity(wantedSeverity, current.Level));
        IReadOnlyList<LogSample>? samples = null;

        if (shouldSample && counts.Total > 0)
        {
            try
            {
                samples = await chReader.FetchSamplesAsync(table, nowUtc.AddMinutes(-15), nowUtc, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to fetch log samples for table '{Table}'; proceeding with error count evidence only", table);
            }
        }

        var triggerEnd = counts.Total == 0 && current != null ? GetOriginalWindowEnd(current) : null;
        var (context, diagnosticText) = DiagnosticsBuilder.Build(
            settings.NodeName,
            settings.Database,
            table,
            counts,
            nowUtc,
            settings.PublicUrl,
            samples,
            triggerEnd);

        if (current == null && wantedSeverity != null)
        {
            var queryInfo = InvestigationQueries.BuildQueries(settings.Database, table, nowUtc);
            var investigateUrl = InvestigationQueries.BuildInvestigateUrl(settings.PublicUrl, queryInfo.CountSql);

            var title = $"[{settings.NodeName}] {table}: {counts.Total} errors in 15 minutes";
            if (title.Length > 200) title = title[..200];

            var createRequest = new CreateIncidentRequest
            {
                ReportId = Guid.NewGuid(),
                Title = title,
                Content = diagnosticText,
                Level = wantedSeverity,
                Context = context,
                OccurredAtUtc = nowUtc.UtcDateTime,
                InvestigateUrl = investigateUrl,
                Metadata = new JObject
                {
                    ["source"] = settings.Source,
                    ["node"] = settings.NodeName,
                    ["database"] = settings.Database,
                    ["table"] = table
                }
            };

            store.Save(table, JsonConvert.SerializeObject(createRequest));
            var resp = await api.CreateIncidentAsync(createRequest).WaitAsync(ct);
            var verified = (await api.GetIncidentAsync(resp.IncidentId).WaitAsync(ct)).Incident;
            if (verified.IncidentId != resp.IncidentId || verified.ReportId != createRequest.ReportId)
            {
                throw new InvalidOperationException("Receipt identity mismatch after incident creation");
            }

            store.Delete(table);
            current = verified;
            log.LogInformation("Created new {Level} incident {IncidentId} for table {Table} ({Total} errors)", wantedSeverity, resp.IncidentId, table, counts.Total);
        }

        if (current == null)
        {
            return;
        }

        var targetIncidentId = current.IncidentId;
        var targetReportId = current.ReportId;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (current.IncidentId != targetIncidentId || current.ReportId != targetReportId)
            {
                throw new InvalidOperationException($"Incident identity changed unexpectedly during update for table '{table}'");
            }

            if (current.Status is "resolved" or "mitigated")
            {
                return;
            }

            string? action = null;
            if (counts.Total == 0)
            {
                action = "mitigate";
            }
            else if (wantedSeverity != null && SeverityHelper.IsHigherSeverity(wantedSeverity, current.Level))
            {
                action = "severity";
            }

            if (action == null)
            {
                return;
            }

            try
            {
                if (action == "mitigate")
                {
                    await api.MitigateIncidentAsync(current.IncidentId, new MitigateIncidentRequest
                    {
                        Version = current.Version,
                        Reason = diagnosticText
                    }).WaitAsync(ct);
                    log.LogInformation("Mitigated incident {IncidentId} for table {Table} after 15-minute error count dropped to zero", current.IncidentId, table);
                    return;
                }

                if (action == "severity")
                {
                    await api.ChangeIncidentSeverityAsync(current.IncidentId, new ChangeIncidentSeverityRequest
                    {
                        Version = current.Version,
                        Level = wantedSeverity!,
                        Reason = diagnosticText
                    }).WaitAsync(ct);
                    log.LogInformation("Upgraded severity of incident {IncidentId} for table {Table} to {Level}", current.IncidentId, table, wantedSeverity);
                    return;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Incident update attempt {Attempt}/3 failed for table {Table}", attempt, table);
                if (attempt == 3) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                current = (await api.GetIncidentAsync(targetIncidentId).WaitAsync(ct)).Incident;
            }
        }
    }
}
