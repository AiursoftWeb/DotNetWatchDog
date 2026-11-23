using System.Text.Json;

namespace Aiursoft.DotNetWatchDog;

internal sealed class StateStore : IDisposable
{
    private readonly FileStream _lockStream;
    private readonly string _knownTablesPath;
    private readonly string _pendingPath;
    private readonly object _sync = new();

    private HashSet<string>? _knownTables;
    private Dictionary<string, string>? _pending;

    public StateStore(Settings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        var lockPath = Path.Combine(settings.DataDirectory, "monitor.lock");
        _lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var scopePath = Path.Combine(settings.DataDirectory, "scope.json");
        _knownTablesPath = Path.Combine(settings.DataDirectory, "known_tables.json");
        _pendingPath = Path.Combine(settings.DataDirectory, "pending.json");

        var expectedScope = JsonSerializer.Serialize(new[] { settings.NodeName, settings.Database });
        if (File.Exists(scopePath))
        {
            var existingScope = File.ReadAllText(scopePath);
            if (existingScope != expectedScope)
            {
                throw new InvalidOperationException("State directory scope changed");
            }
        }
        else
        {
            AtomicWrite(scopePath, new[] { settings.NodeName, settings.Database });
        }
    }

    private HashSet<string> KnownTables
    {
        get
        {
            lock (_sync)
            {
                if (_knownTables != null) return _knownTables;
                if (File.Exists(_knownTablesPath))
                {
                    try
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(_knownTablesPath));
                        _knownTables = new HashSet<string>(list ?? [], StringComparer.Ordinal);
                    }
                    catch
                    {
                        _knownTables = new HashSet<string>(StringComparer.Ordinal);
                    }
                }
                else
                {
                    _knownTables = new HashSet<string>(StringComparer.Ordinal);
                }
                return _knownTables;
            }
        }
    }

    private Dictionary<string, string> PendingMap
    {
        get
        {
            lock (_sync)
            {
                if (_pending != null) return _pending;
                if (File.Exists(_pendingPath))
                {
                    try
                    {
                        _pending = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_pendingPath))
                            ?? new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                    catch
                    {
                        _pending = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                }
                else
                {
                    _pending = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                return _pending;
            }
        }
    }

    internal HashSet<string> KnownNames()
    {
        lock (_sync)
        {
            return [.. KnownTables];
        }
    }

    internal void Remember(IEnumerable<string> names)
    {
        lock (_sync)
        {
            var modified = false;
            foreach (var name in names)
            {
                if (KnownTables.Add(name))
                {
                    modified = true;
                }
            }

            if (modified)
            {
                AtomicWrite(_knownTablesPath, KnownTables.ToList());
            }
        }
    }

    internal List<string> PendingNames()
    {
        lock (_sync)
        {
            return [.. PendingMap.Keys];
        }
    }

    internal string? Pending(string name)
    {
        lock (_sync)
        {
            return PendingMap.GetValueOrDefault(name);
        }
    }

    internal void Save(string name, string payloadJson)
    {
        lock (_sync)
        {
            PendingMap[name] = payloadJson;
            AtomicWrite(_pendingPath, PendingMap);
        }
    }

    internal void Delete(string name)
    {
        lock (_sync)
        {
            if (PendingMap.Remove(name))
            {
                AtomicWrite(_pendingPath, PendingMap);
            }
        }
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose()
    {
        _lockStream.Dispose();
    }
}
