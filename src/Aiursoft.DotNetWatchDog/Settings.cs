namespace Aiursoft.DotNetWatchDog;

internal sealed record Settings(
    string NodeName,
    string Database,
    string ConnectionString,
    string AlertCenterUrl,
    string AlertCenterKey,
    string DataDirectory,
    string? PublicUrl,
    string Source)
{
    internal static Settings Load()
    {
        static string? GetEnv(string name) => Environment.GetEnvironmentVariable(name)?.Trim();
        static string Required(string name, params string[] fallbacks)
        {
            var val = GetEnv(name);
            if (!string.IsNullOrWhiteSpace(val)) return val;
            foreach (var fb in fallbacks)
            {
                val = GetEnv(fb);
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            throw new InvalidOperationException($"{name} is required.");
        }

        var nodeName = Required("NODE_NAME", "SERVER_NAME");
        var database = GetEnv("CLICKHOUSE_DATABASE") ?? "AspNetCoreLogs";
        var alertCenterUrl = Required("ALERTCENTER_URL").TrimEnd('/');
        if (!Uri.TryCreate(alertCenterUrl, UriKind.Absolute, out var acUri) || acUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(acUri.UserInfo) || !string.IsNullOrEmpty(acUri.Query) || !string.IsNullOrEmpty(acUri.Fragment))
        {
            throw new InvalidOperationException("ALERTCENTER_URL must be an HTTP(S) endpoint without credentials, query or fragment.");
        }

        var alertCenterKey = Required("ALERTCENTER_API_KEY");
        var dataDirectory = GetEnv("STATE_DIR") ?? GetEnv("DATA_DIRECTORY") ?? "/data";
        var publicUrl = GetEnv("CLICKHOUSE_PUBLIC_URL")?.TrimEnd('/');
        if (!string.IsNullOrEmpty(publicUrl))
        {
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var pUri) || pUri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(pUri.UserInfo) || !string.IsNullOrEmpty(pUri.Query) || !string.IsNullOrEmpty(pUri.Fragment))
            {
                throw new InvalidOperationException("CLICKHOUSE_PUBLIC_URL must not contain credentials, query or fragment.");
            }
        }

        var source = GetEnv("SOURCE_NAME") ?? "clickhouse-app-log-error-monitor";

        string connectionString;
        var customConn = GetEnv("CLICKHOUSE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(customConn))
        {
            connectionString = customConn;
        }
        else
        {
            var chUrl = GetEnv("CLICKHOUSE_URL") ?? "http://clickhouse_clickhouse:8123";
            var chUser = GetEnv("CLICKHOUSE_USER") ?? "default";
            var chPass = GetEnv("CLICKHOUSE_PASSWORD") ?? "";
            var uri = new Uri(chUrl);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 8123;
            connectionString = $"Host={host};Port={port};Database={database};Username={chUser};Password={chPass}";
        }

        return new Settings(
            NodeName: nodeName,
            Database: database,
            ConnectionString: connectionString,
            AlertCenterUrl: alertCenterUrl,
            AlertCenterKey: alertCenterKey,
            DataDirectory: dataDirectory,
            PublicUrl: publicUrl,
            Source: source
        );
    }
}
