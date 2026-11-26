using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DotNetWatchDog;

internal sealed record ErrorCounts(int Error, int Critical, int Total);

internal sealed record LogSample(DateTimeOffset EventTime, int LogLevel, string? Category, string? Message, string? Exception);

internal static class DiagnosticsBuilder
{
    internal static (JObject Context, string DiagnosticText) Build(
        string node,
        string database,
        string table,
        ErrorCounts counts,
        DateTimeOffset nowUtc,
        string? publicUrl,
        IReadOnlyList<LogSample>? samples = null,
        DateTimeOffset? triggerEndUtc = null)
    {
        var end = nowUtc.ToUniversalTime();
        var start = end.AddMinutes(-15);
        var level = SeverityHelper.GetSeverity(counts.Total);
        var threshold = SeverityHelper.GetThreshold(level);

        var context = new JObject
        {
            ["WindowStartUtc"] = start.ToString("O", CultureInfo.InvariantCulture),
            ["WindowEndUtc"] = end.ToString("O", CultureInfo.InvariantCulture),
            ["WindowBounds"] = "[start, end)",
            ["ErrorCount"] = counts.Error,
            ["CriticalCount"] = counts.Critical,
            ["TotalCount"] = counts.Total,
            ["TriggerThreshold"] = threshold,
            ["ThresholdLevel"] = level,
            ["Thresholds"] = new JObject
            {
                ["sev4"] = 3,
                ["sev3"] = 5,
                ["sev2"] = 15
            }
        };

        var query = InvestigationQueries.BuildQueries(database, table, end);
        var recovering = counts.Total == 0;
        var windowLabel = recovering ? "Recovery verification window" : "Error detection window";

        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant($"""
            {node} / {database}.{table}

            {counts.Total} errors in the last 15 minutes (Error: {counts.Error}; Critical: {counts.Critical}). Warnings excluded.
            {windowLabel} (UTC, start inclusive / end exclusive): {start:yyyy-MM-dd HH:mm:ss} to {end:yyyy-MM-dd HH:mm:ss}.
            Checked every 5 minutes; automatically mitigates when the 15-minute count is zero (final resolution remains a human decision).


            """));

        if (!string.IsNullOrEmpty(publicUrl))
        {
            sb.Append(FormattableString.Invariant($"ClickHouse query page: {publicUrl.TrimEnd('/')}/play\n\n"));
        }

        if (recovering)
        {
            sb.Append("Recovery confirmed. Zero errors are expected in the recovery window; this is not the original failure window.\n\n");
            if (triggerEndUtc.HasValue)
            {
                var triggerEnd = triggerEndUtc.Value.ToUniversalTime();
                var originalQueries = InvestigationQueries.BuildQueries(database, table, triggerEnd);
                sb.Append(FormattableString.Invariant($"""
                    Original incident trigger window (creation snapshot, UTC): {triggerEnd.AddMinutes(-15):yyyy-MM-dd HH:mm:ss} to {triggerEnd:yyyy-MM-dd HH:mm:ss}.
                    Investigate the original failure:
                    {originalQueries.CountSql}

                    Inspect original messages and exception stacks (up to 20 rows):
                    {originalQueries.DetailsSql}


                    """));
            }
            else
            {
                sb.Append("Original trigger window unavailable. Open the incident creation details; do not use the recovery query to investigate the original failure.\n\n");
            }

            sb.Append(FormattableString.Invariant($"Verify recovery (expected total: 0; first_error and last_error: NULL):\n{query.CountSql}"));
        }
        else
        {
            sb.Append(FormattableString.Invariant($"""
                Investigate this error detection window:
                {query.CountSql}

                Inspect messages and exception stacks (up to 20 rows):
                {query.DetailsSql}
                """));
        }

        if (samples != null && samples.Count > 0 && counts.Total > 0)
        {
            var snippetBuilder = new StringBuilder();
            foreach (var sample in samples)
            {
                var parts = new List<string>
                {
                    RedactionHelper.Redact(sample.EventTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
                    RedactionHelper.Redact(sample.LogLevel.ToString(CultureInfo.InvariantCulture)),
                    RedactionHelper.Redact(sample.Category),
                    RedactionHelper.Redact(sample.Message),
                    RedactionHelper.Redact(sample.Exception)
                };
                var partText = string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
                if (!string.IsNullOrWhiteSpace(partText))
                {
                    if (snippetBuilder.Length > 0) snippetBuilder.Append("\n\n");
                    snippetBuilder.Append(partText);
                }
            }

            var snippet = snippetBuilder.ToString();
            if (string.IsNullOrWhiteSpace(snippet))
            {
                snippet = "No sample rows remain in this window; use the query above.";
            }

            const string heading = "\n\nRecent examples (up to 3; truncated and sensitive fields masked):\n";
            var textBytes = Encoding.UTF8.GetByteCount(sb.ToString());
            var headingBytes = Encoding.UTF8.GetByteCount(heading);
            var budget = 4000 - textBytes - headingBytes;
            if (budget > 4)
            {
                var raw = Encoding.UTF8.GetBytes(snippet);
                if (raw.Length > budget)
                {
                    snippet = Encoding.UTF8.GetString(raw, 0, budget - 3) + "...";
                }
                sb.Append(heading);
                sb.Append(snippet);
            }
        }

        return (context, sb.ToString());
    }
}
