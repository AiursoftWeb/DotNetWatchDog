namespace Aiursoft.DotNetWatchDog;

internal sealed record InvestigationQueryResult(string CountSql, string DetailsSql, string WhereClause);

internal static class InvestigationQueries
{
    internal static string EscapeIdent(string value) =>
        "`" + value.Replace("\\", "\\\\").Replace("`", "\\`") + "`";

    internal static InvestigationQueryResult BuildQueries(string database, string table, DateTimeOffset endUtc)
    {
        var end = endUtc.ToUniversalTime();
        var start = end.AddMinutes(-15);
        var source = $"{EscapeIdent(database)}.{EscapeIdent(table)}";
        var where = FormattableString.Invariant($"""
            EventTime >= toDateTime64('{start:yyyy-MM-dd HH:mm:ss.ffffff}', 6, 'UTC')
              AND EventTime < toDateTime64('{end:yyyy-MM-dd HH:mm:ss.ffffff}', 6, 'UTC')
              AND toInt32(LogLevel) IN (4, 5)
            """);

        var count = FormattableString.Invariant($"""
            SELECT countIf(toInt32(LogLevel) = 4) AS errors,
                   countIf(toInt32(LogLevel) = 5) AS critical, count() AS total,
                   minOrNull(EventTime) AS first_error, maxOrNull(EventTime) AS last_error
            FROM {source}
            WHERE {where};
            """);

        var details = FormattableString.Invariant($"""
            SELECT *
            FROM {source}
            WHERE {where}
            ORDER BY EventTime DESC
            LIMIT 20;
            """);

        return new InvestigationQueryResult(count, details, where);
    }

    internal static string? BuildInvestigateUrl(string? publicUrl, string? query = null)
    {
        if (string.IsNullOrWhiteSpace(publicUrl)) return null;
        var basePlay = $"{publicUrl.TrimEnd('/')}/play";
        return string.IsNullOrWhiteSpace(query) ? basePlay : $"{basePlay}?query={Uri.EscapeDataString(query)}";
    }
}
