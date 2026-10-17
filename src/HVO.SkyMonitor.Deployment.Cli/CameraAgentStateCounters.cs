using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The CameraAgent counters a snapshot restore must never rewind, because each hands out identities that leave the
/// instance or that clients hold.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>raw_capture_sequences.last_sequence</c>, per agent: capture identities are <c>(agent, sequence)</c>, and a
/// sequence is reserved before its files exist, so a reserved but fileless sequence cannot be rebuilt from frames.</item>
/// <item><c>capture_control_state.version</c>: the capture-control version clients and the installer use as an
/// optimistic-concurrency token.</item>
/// <item><c>fleet_status_metadata.next_sequence</c>: fleet reports leave the instance numbered by it, and keep flowing
/// while capture is paused.</item>
/// </list>
/// Every other store identifies what leaves the instance by random or content-derived identity (artifact, observation,
/// candidate and evidence IDs), or numbers it per boot session (execution-evidence origins), so a rewind cannot reuse an
/// identity there; local row IDs never leave the instance.
/// </remarks>
internal static class CameraAgentStateCounters
{
    internal sealed record Counter(string Store, string Name, string Table, string KeyColumn, string ValueColumn, bool Upsert);

    public static IReadOnlyList<Counter> All { get; } =
    [
        new("raw/journal/raw-ingress.db", "capture-sequence", "raw_capture_sequences", "agent_id", "last_sequence", Upsert: true),
        new("raw/journal/raw-ingress.db", "capture-control-version", "capture_control_state", "state_key", "version", Upsert: false),
        new("raw/.fleet/fleet-status.db", "fleet-report-sequence", "fleet_status_metadata", "metadata_key", "next_sequence", Upsert: false)
    ];

    /// <summary>Every counter's current value in the stores under <paramref name="stateRoot"/>; the instance is stopped.</summary>
    public static IReadOnlyList<StateCounterFloor> Read(string stateRoot)
    {
        var floors = new List<StateCounterFloor>();
        foreach (var counter in All)
        {
            var path = CameraAgentStateSnapshot.StorePath(stateRoot, counter.Store);
            if (!File.Exists(path)) continue;
            using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(path);
            floors.AddRange(Read(database.Connection, counter));
        }
        return floors;
    }

    /// <summary>The higher of each counter's two values, so a repeated restore keeps the highest mark it ever saw.</summary>
    public static IReadOnlyList<StateCounterFloor> Merge(
        IEnumerable<StateCounterFloor>? recorded,
        IEnumerable<StateCounterFloor> observed)
        => (recorded ?? []).Concat(observed)
            .GroupBy(static floor => (floor.Store, floor.Counter, floor.Key))
            .Select(static group => group.MaxBy(static floor => floor.Value)!)
            .OrderBy(static floor => floor.Store, StringComparer.Ordinal)
            .ThenBy(static floor => floor.Counter, StringComparer.Ordinal)
            .ThenBy(static floor => floor.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Whether every floor recorded for <paramref name="relative"/> can be carried into the store at
    /// <paramref name="path"/>: its table and columns exist, and a counter that is updated in place has its row.
    /// </summary>
    public static void EnsureApplicable(string path, string relative, IReadOnlyList<StateCounterFloor> floors)
    {
        var pending = floors.Where(floor => floor.Store == relative).ToArray();
        if (pending.Length == 0) return;
        using var connection = OpenImmutable(path);
        foreach (var floor in pending)
        {
            var counter = Find(floor);
            var existing = Read(connection, counter);
            if (!HasColumns(connection, counter) || !counter.Upsert && existing.All(value => value.Key != floor.Key))
                throw new InstallerException(
                    $"The snapshot of '{relative}' cannot carry the {counter.Name} counter forward, so restoring it could hand out an identity twice.");
        }
    }

    /// <summary>
    /// Raises each counter in the restored store at <paramref name="path"/> to at least its floor, in one transaction,
    /// and proves the result. SQLite syncs the database on commit; the caller syncs the directory.
    /// </summary>
    public static void Apply(string path, string relative, IReadOnlyList<StateCounterFloor> floors)
    {
        var pending = floors.Where(floor => floor.Store == relative).ToArray();
        if (pending.Length == 0) return;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using (var synchronous = connection.CreateCommand())
            {
                synchronous.CommandText = "PRAGMA synchronous = FULL;";
                synchronous.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            foreach (var floor in pending)
            {
                var counter = Find(floor);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
                command.CommandText = counter.Upsert
                    ? $"INSERT INTO {counter.Table}({counter.KeyColumn}, {counter.ValueColumn}) VALUES ($key, $value) " +
                      $"ON CONFLICT({counter.KeyColumn}) DO UPDATE SET {counter.ValueColumn} = MAX({counter.ValueColumn}, excluded.{counter.ValueColumn});"
                    : $"UPDATE {counter.Table} SET {counter.ValueColumn} = MAX({counter.ValueColumn}, $value) WHERE {counter.KeyColumn} = $key;";
#pragma warning restore CA2100
                command.Parameters.AddWithValue("$key", KeyValue(connection, transaction, counter, floor.Key));
                command.Parameters.AddWithValue("$value", floor.Value);
                if (command.ExecuteNonQuery() != 1)
                    throw new InstallerException($"The restored '{relative}' did not accept its {counter.Name} counter floor.");
            }
            transaction.Commit();
        }
        using var verify = OpenImmutable(path);
        foreach (var floor in pending)
        {
            var counter = Find(floor);
            if (Read(verify, counter).SingleOrDefault(value => value.Key == floor.Key) is not { } carried || carried.Value < floor.Value)
                throw new InstallerException($"The restored '{relative}' rewound its {counter.Name} counter.");
        }
    }

    private static Counter Find(StateCounterFloor floor)
        => All.SingleOrDefault(counter => counter.Store == floor.Store && counter.Name == floor.Counter)
           ?? throw new InstallerException($"The journal records an unknown CameraAgent counter '{floor.Counter}'.");

    private static List<StateCounterFloor> Read(SqliteConnection connection, Counter counter)
    {
        if (!HasColumns(connection, counter)) return [];
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
        command.CommandText = $"SELECT CAST({counter.KeyColumn} AS TEXT), {counter.ValueColumn} FROM {counter.Table};";
#pragma warning restore CA2100
        using var reader = command.ExecuteReader();
        var values = new List<StateCounterFloor>();
        while (reader.Read())
        {
            values.Add(new StateCounterFloor(counter.Store, counter.Name, reader.GetString(0), reader.GetInt64(1)));
        }
        return values;
    }

    private static bool HasColumns(SqliteConnection connection, Counter counter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", counter.Table);
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns.Contains(counter.KeyColumn) && columns.Contains(counter.ValueColumn);
    }

    /// <summary>Binds the key with the column's own type, so an integer key matches its row.</summary>
    private static object KeyValue(SqliteConnection connection, SqliteTransaction transaction, Counter counter, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", counter.Table);
        command.Parameters.AddWithValue("$column", counter.KeyColumn);
        var type = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        return type.Contains("INT", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(key, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : key;
    }

    private static SqliteConnection OpenImmutable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
