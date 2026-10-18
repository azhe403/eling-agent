using System.Text.Json;
using Microsoft.Extensions.Logging;

using Eling.Core.Serialization;

namespace Eling.Core.Database;

public sealed class JsonDatabaseConnectionStore : IDatabaseConnectionStore, IDisposable
{
    private const string MutexName = @"Global\eling-databases-registry";
    private const string TempSuffix = ".tmp";

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private static readonly Mutex Gate = new(initiallyOwned: false, MutexName);

    private readonly string _filePath;
    private readonly ILogger<JsonDatabaseConnectionStore>? _logger;

    public JsonDatabaseConnectionStore(
        string filePath,
        ILogger<JsonDatabaseConnectionStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
    }

    public Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult<IReadOnlyList<DatabaseConnection>>(WithGate(() => LoadCore()));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list database connections from {Path}.", _filePath);
            return Task.FromResult<IReadOnlyList<DatabaseConnection>>([]);
        }
    }

    public Task<DatabaseConnection?> FindAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<DatabaseConnection?>(null);
        try
        {
            return Task.FromResult(
                WithGate(() =>
                {
                    var rows = LoadCore();
                    return rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to find database connection {Id} in {Path}.", id, _filePath);
            return Task.FromResult<DatabaseConnection?>(null);
        }
    }

    public Task UpsertAsync(DatabaseConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        WithGate(() =>
        {
            var rows = LoadCore();
            var id = connection.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = System.Ulid.NewUlid().ToString().ToLowerInvariant();
                connection = connection with { Id = id };
            }

            var idx = rows.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (idx >= 0)
            {
                rows[idx] = connection;
            }
            else
            {
                rows.Add(connection);
            }

            SaveCore(rows);
        });
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);
        try
        {
            return Task.FromResult(
                WithGate(() =>
                {
                    var rows = LoadCore();
                    var removed = rows.RemoveAll(r => string.Equals(r.Id, id, StringComparison.Ordinal));
                    if (removed > 0)
                    {
                        SaveCore(rows);
                    }

                    return removed > 0;
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove database connection {Id} from {Path}.", id, _filePath);
            return Task.FromResult(false);
        }
    }

    public Task MarkConnectedAsync(string id, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.CompletedTask;
        try
        {
            WithGate(() =>
            {
                var rows = LoadCore();
                var idx = rows.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));
                if (idx < 0) return;
                rows[idx] = rows[idx] with { LastConnectedAt = at };
                SaveCore(rows);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to mark database connection {Id} connected in {Path}.", id, _filePath);
        }
        return Task.CompletedTask;
    }

    private void WithGate(Action action)
    {
        EnterGate();
        try
        {
            action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private T WithGate<T>(Func<T> action)
    {
        EnterGate();
        try
        {
            return action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private void EnterGate()
    {
        try
        {
            if (!Gate.WaitOne(GateTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {GateTimeout} waiting for the database registry gate.");
            }
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private List<DatabaseConnection> LoadCore()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<DatabaseConnection>>(json, JsonDefaults.Shared) ?? [];
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable database registry at {Path}.", _filePath);
            return [];
        }
    }

    private void SaveCore(List<DatabaseConnection> rows)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(rows, JsonDefaults.Shared);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public void Dispose()
    {
    }
}
