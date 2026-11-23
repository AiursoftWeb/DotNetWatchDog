using System.Data.Common;
using System.Globalization;
using ClickHouse.Client.ADO;

namespace Aiursoft.DotNetWatchDog;

internal sealed class ClickHouseLogReader(Settings settings)
{
    private async Task<ClickHouseConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new ClickHouseConnection(settings.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    internal async Task<List<string>> DiscoverTablesAsync(CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmdDb = conn.CreateCommand();
        cmdDb.CommandText = "SELECT count() AS n FROM system.databases WHERE name = @db;";
        AddParam(cmdDb, "db", settings.Database);
        var dbCount = Convert.ToInt32(await cmdDb.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (dbCount != 1)
        {
            throw new InvalidOperationException($"Database '{settings.Database}' not found or inaccessible");
        }

        await using var cmdTables = conn.CreateCommand();
        cmdTables.CommandText = "SELECT name FROM system.tables WHERE database = @db;";
        AddParam(cmdTables, "db", settings.Database);
        await using var reader = await cmdTables.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        if (tables.Distinct(StringComparer.Ordinal).Count() != tables.Count)
        {
            throw new InvalidOperationException("Invalid table discovery: duplicate table names returned");
        }

        return tables;
    }

    internal async Task<string> VerifySchemaAsync(string table, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM system.columns WHERE database = @db AND table = @tbl AND name IN ('EventTime', 'LogLevel');";
        AddParam(cmd, "db", settings.Database);
        AddParam(cmd, "tbl", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var cols = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            cols[reader.GetString(0)] = reader.GetString(1);
        }

        if (cols.Count != 2 ||
            !cols.TryGetValue("LogLevel", out var logLevelType) ||
            !cols.TryGetValue("EventTime", out var eventTimeType) ||
            logLevelType is not ("Int32" or "String") ||
            !(eventTimeType == "DateTime" || eventTimeType.StartsWith("DateTime64(") || eventTimeType.StartsWith("DateTime(")))
        {
            throw new InvalidOperationException($"Unsupported logger schema in table '{table}'");
        }

        return logLevelType;
    }

    internal async Task<ErrorCounts> CountErrorsAsync(string table, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        var logLevelType = await VerifySchemaAsync(table, ct);
        var levelExpr = logLevelType == "String" ? "toInt32(LogLevel)" : "LogLevel";
        var tableEscaped = $"{InvestigationQueries.EscapeIdent(settings.Database)}.{InvestigationQueries.EscapeIdent(table)}";

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = FormattableString.Invariant($"""
            SELECT countIf({levelExpr} = 4) AS error,
                   countIf({levelExpr} = 5) AS critical,
                   count() AS total
            FROM {tableEscaped}
            WHERE EventTime >= parseDateTime64BestEffort('{startUtc:yyyy-MM-dd HH:mm:ss.ffffff}', 6, 'UTC')
              AND EventTime < parseDateTime64BestEffort('{endUtc:yyyy-MM-dd HH:mm:ss.ffffff}', 6, 'UTC')
              AND {levelExpr} IN (4, 5)
            """);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Failed to query error counts for table '{table}'");
        }

        var error = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        var critical = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        var total = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);

        if (error + critical != total)
        {
            throw new InvalidOperationException($"Inconsistent counts for table '{table}': error={error}, critical={critical}, total={total}");
        }

        return new ErrorCounts(error, critical, total);
    }

    internal async Task<IReadOnlyList<LogSample>> FetchSamplesAsync(string table, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmdCols = conn.CreateCommand();
        cmdCols.CommandText = "SELECT name FROM system.columns WHERE database = @db AND table = @tbl;";
        AddParam(cmdCols, "db", settings.Database);
        AddParam(cmdCols, "tbl", table);

        await using var colReader = await cmdCols.ExecuteReaderAsync(ct);
        var existingCols = new HashSet<string>(StringComparer.Ordinal);
        while (await colReader.ReadAsync(ct))
        {
            existingCols.Add(colReader.GetString(0));
        }

        var selectParts = new List<string> { "EventTime", "toInt32(LogLevel) AS LogLevel" };
        var limits = new[] { ("Category", 100), ("Message", 500), ("Exception", 800) };
        foreach (var (colName, maxLen) in limits)
        {
            if (existingCols.Contains(colName))
            {
                selectParts.Add(FormattableString.Invariant($"substringUTF8(toString({InvestigationQueries.EscapeIdent(colName)}), 1, {maxLen}) AS {InvestigationQueries.EscapeIdent(colName)}"));
            }
        }

        var tableEscaped = $"{InvestigationQueries.EscapeIdent(settings.Database)}.{InvestigationQueries.EscapeIdent(table)}";
        var queryInfo = InvestigationQueries.BuildQueries(settings.Database, table, endUtc);

        await using var cmdSamples = conn.CreateCommand();
        cmdSamples.CommandText = FormattableString.Invariant($"""
            SELECT {string.Join(", ", selectParts)}
            FROM {tableEscaped}
            WHERE {queryInfo.WhereClause}
            ORDER BY EventTime DESC
            LIMIT 3
            SETTINGS max_execution_time=5, max_result_rows=3, max_result_bytes=20000, result_overflow_mode='throw'
            """);

        await using var sampleReader = await cmdSamples.ExecuteReaderAsync(ct);
        var samples = new List<LogSample>();
        while (await sampleReader.ReadAsync(ct))
        {
            var eventTime = Convert.ToDateTime(sampleReader.GetValue(0), CultureInfo.InvariantCulture);
            var logLevel = Convert.ToInt32(sampleReader.GetValue(1), CultureInfo.InvariantCulture);
            string? category = null;
            string? message = null;
            string? exception = null;

            for (var i = 2; i < sampleReader.FieldCount; i++)
            {
                var colName = sampleReader.GetName(i);
                var val = sampleReader.IsDBNull(i) ? null : sampleReader.GetString(i);
                if (colName.Equals("Category", StringComparison.OrdinalIgnoreCase)) category = val;
                else if (colName.Equals("Message", StringComparison.OrdinalIgnoreCase)) message = val;
                else if (colName.Equals("Exception", StringComparison.OrdinalIgnoreCase)) exception = val;
            }

            samples.Add(new LogSample(new DateTimeOffset(eventTime, TimeSpan.Zero), logLevel, category, message, exception));
        }

        return samples;
    }
}
