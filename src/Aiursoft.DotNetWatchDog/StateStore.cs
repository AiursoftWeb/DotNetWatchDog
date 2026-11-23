using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Aiursoft.DotNetWatchDog;

internal sealed class StateStore : IDisposable
{
    private readonly FileStream _lockStream;
    private readonly SqliteConnection _db;

    public StateStore(Settings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        var lockPath = Path.Combine(settings.DataDirectory, "monitor.lock");
        _lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var dbPath = Path.Combine(settings.DataDirectory, "state.sqlite3");
        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _db = new SqliteConnection(connectionStringBuilder.ConnectionString);
        _db.Open();

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA quick_check;";
            var checkResult = cmd.ExecuteScalar()?.ToString();
            if (checkResult != "ok")
            {
                throw new InvalidOperationException("State integrity check failed");
            }
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS config (scope TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }

        var expectedScope = JsonSerializer.Serialize(new[] { settings.NodeName, settings.Database });
        string? existingScope;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT scope FROM config LIMIT 1;";
            existingScope = cmd.ExecuteScalar()?.ToString();
        }

        if (existingScope != null && existingScope != expectedScope)
        {
            throw new InvalidOperationException("State directory scope changed");
        }

        if (existingScope == null)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO config VALUES ($scope);";
            cmd.Parameters.AddWithValue("$scope", expectedScope);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS pending (name TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS known_tables (name TEXT PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    internal HashSet<string> KnownNames()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT name FROM known_tables;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    internal void Remember(IEnumerable<string> names)
    {
        using var transaction = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT OR IGNORE INTO known_tables VALUES ($name);";
        var param = cmd.Parameters.Add("$name", SqliteType.Text);

        foreach (var name in names)
        {
            param.Value = name;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal List<string> PendingNames()
    {
        var result = new List<string>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT name FROM pending;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    internal string? Pending(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM pending WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar()?.ToString();
    }

    internal void Save(string name, string payloadJson)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO pending VALUES ($name, $payload);";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.ExecuteNonQuery();
    }

    internal void Delete(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM pending WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        _lockStream.Dispose();
    }
}
