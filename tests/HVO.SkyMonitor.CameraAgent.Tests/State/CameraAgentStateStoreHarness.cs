using System.Globalization;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.State;

/// <summary>
/// Creates every CameraAgent SQLite store with the current code, at the place a running instance keeps it, under an
/// instance state tree laid out as the installer lays it out. Each store is initialized through the same code path
/// the runtime uses, so its schema is exactly the one an instance of this release has.
/// </summary>
internal sealed class CameraAgentStateStoreHarness : IDisposable
{
    private static readonly (string Container, string Directory)[] Mounts =
        [("/app/App_Data", "identity"), ("/app/data/raw", "raw"), ("/app/data/archive", "archive")];

    private CameraAgentStateStoreHarness(string root, IReadOnlyList<string> storageRoots)
    {
        Root = root;
        StateRoot = Path.Combine(root, "state");
        StorageRoots = storageRoots;
    }

    public string Root { get; }

    /// <summary>The instance state root, holding the <c>identity</c>, <c>raw</c> and <c>archive</c> bind sources.</summary>
    public string StateRoot { get; }

    /// <summary>The configured FileStorage roots, as container paths.</summary>
    public IReadOnlyList<string> StorageRoots { get; }

    public string RawRoot => Path.Combine(StateRoot, "raw");

    /// <summary>A state tree whose FileStorage steps write to <paramref name="storageRoots"/>, given as container paths.</summary>
    public static CameraAgentStateStoreHarness Create(IReadOnlyList<string> storageRoots)
    {
        var harness = new CameraAgentStateStoreHarness(
            Path.Combine(Path.GetTempPath(), $"hvo-store-harness-{Guid.NewGuid():N}"), storageRoots);
        foreach (var directory in Mounts.Select(mount => Path.Combine(harness.StateRoot, mount.Directory)))
        {
            Directory.CreateDirectory(directory);
        }
        return harness;
    }

    /// <summary>The host directory a container storage root maps to.</summary>
    public string HostPath(string containerRoot)
    {
        var (container, directory) = Mounts.Single(mount => containerRoot == mount.Container || containerRoot.StartsWith(mount.Container + "/", StringComparison.Ordinal));
        return Path.Combine(StateRoot, directory, containerRoot[container.Length..].TrimStart('/'));
    }

    /// <summary>Initializes every store; over existing stores this is what an upgraded instance does on startup.</summary>
    public async Task InitializeStoresAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The host registers Data Protection for the deployment location; the stores only need it to exist.
        services.AddSingleton<HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.IDeploymentLocationProtector>(new PassThroughProtector());
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = RawRoot
        }).Build());
        var provider = services.BuildServiceProvider();
        await using var disposeProvider = provider.ConfigureAwait(false);
        await new SqliteRawCaptureJournal(Path.Combine(RawRoot, "journal", "raw-ingress.db"), 5)
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteCaptureProcessingStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteTransientRuntimeStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var storageRoot in StorageRoots)
        {
            await new SqliteArtifactOutbox().InitializeAsync(HostPath(storageRoot), CancellationToken.None).ConfigureAwait(false);
        }
        using (var fleet = new SqliteFleetStatusOutbox())
        {
            _ = await fleet.GetSnapshotAsync(RawRoot, CancellationToken.None).ConfigureAwait(false);
        }
        await provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>().InitializeAsync(RawRoot, CancellationToken.None).ConfigureAwait(false);
        await ((SqliteExecutionEvidenceOutbox)provider.GetRequiredService<IExecutionEvidenceOutbox>())
            .InitializeAsync(RawRoot, CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteTimeLapseStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await provider.GetRequiredService<SqliteNightlyProductStore>()
            .ListAsync(new DateOnly(2026, 10, 16), CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteLocalAutomationStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        // The host initializes the orbital store through OrbitalElementsAvailability, which calls this; disposing the
        // provider disposes the singleton store and releases its exclusive lock.
        await provider.GetRequiredService<SqliteOrbitalElementsStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteSwpcStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        // The host creates the calibration-family store on its first use; disposing the provider releases its root lock.
        await provider.GetRequiredService<SqliteCalibrationFamilyStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var identity = Path.Combine(StateRoot, "identity", "cameraagent_identity.db");
        var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={identity};Pooling=False").Options);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every SQLite store under the state tree, relative to the state root with <c>/</c> separators.</summary>
    public IReadOnlyList<string> StoreLocations()
        => Directory.EnumerateFiles(StateRoot, "*.db", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(StateRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    public string StorePath(string location) => Path.Combine(StateRoot, location.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The complete schema of a store as the previous release's exact validators see it: every object's type, owning
    /// table and SQL (whitespace-normalized), the user version, and the rows that record the store's version, since a
    /// version kept in a row (a <c>*_schema</c> or <c>*_metadata</c> table's version column, or the EF migration
    /// history) can change without any DDL changing.
    /// </summary>
    public static SortedDictionary<string, string> ReadSchema(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        var schema = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            schema["pragma:user_version"] = Convert.ToString(version.ExecuteScalar(), CultureInfo.InvariantCulture)!;
        }
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                schema[$"{reader.GetString(0)}:{reader.GetString(1)}"] =
                    $"{reader.GetString(2)}|{(reader.IsDBNull(3) ? string.Empty : Normalize(reader.GetString(3)))}";
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        foreach (var table in tables)
        {
            foreach (var column in Columns(connection, table).Where(column => IsVersionColumn(table, column)))
            {
                using var values = connection.CreateCommand();
#pragma warning disable CA2100 // Table and column names come from sqlite_schema and are quoted as identifiers.
                values.CommandText = $"SELECT DISTINCT CAST({Quote(column)} AS TEXT) FROM {Quote(table)} ORDER BY 1;";
#pragma warning restore CA2100
                using var reader = values.ExecuteReader();
                var recorded = new List<string>();
                while (reader.Read()) recorded.Add(reader.IsDBNull(0) ? "NULL" : reader.GetString(0));
                schema[$"version:{table}.{column}"] = string.Join(",", recorded);
            }
        }
        return schema;
    }

    private static bool IsVersionColumn(string table, string column)
        => table == "__EFMigrationsHistory"
            ? column == "MigrationId"
            : (table.EndsWith("_schema", StringComparison.Ordinal) || table.EndsWith("_metadata", StringComparison.Ordinal)) &&
              column is "version" or "schema_version";

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>The objects whose definitions differ between two schemas, by key.</summary>
    public static IReadOnlyList<string> Differences(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
        => expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)
            .Where(key => !expected.TryGetValue(key, out var left) || !actual.TryGetValue(key, out var right) ||
                          !string.Equals(left, right, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// A store as SQL that rebuilds it: every table, then every row (a fresh store holds only its few metadata rows),
    /// then indexes, triggers and views, then the user version. Line endings are written as LF; the schema comparison
    /// normalizes whitespace, so a definition stored with CRLF (as EF Core writes the Identity schema) compares equal.
    /// </summary>
    public static string Dump(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        var objects = new List<(string Type, string Name, string Sql)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2).ReplaceLineEndings("\n")));
        }
        var dump = new StringBuilder();
        foreach (var table in objects.Where(static item => item.Type == "table"))
        {
            dump.Append(table.Sql).Append(";\n");
        }
        foreach (var table in objects.Where(static item => item.Type == "table"))
        {
            using var rows = connection.CreateCommand();
#pragma warning disable CA2100 // The table name comes from sqlite_schema and is quoted as an identifier.
            rows.CommandText = $"SELECT * FROM \"{table.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
#pragma warning restore CA2100
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(index => Literal(reader.GetValue(index)));
                dump.Append(CultureInfo.InvariantCulture, $"INSERT INTO \"{table.Name}\" VALUES({string.Join(", ", values)});\n");
            }
        }
        foreach (var item in objects.Where(static item => item.Type != "table"))
        {
            dump.Append(item.Sql).Append(";\n");
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            dump.Append(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version.ExecuteScalar()};\n");
        }
        return dump.ToString();
    }

    /// <summary>Builds a store at <paramref name="path"/> from a dump.</summary>
    public static void Rebuild(string path, string dump)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // A frozen test fixture, not input.
        command.CommandText = "PRAGMA foreign_keys = OFF;\n" + dump;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Each harness has its own root, so an idle pooled connection to a deleted store is never reused.
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private sealed class PassThroughProtector : HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.IDeploymentLocationProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Normalize(string sql)
        => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Literal(object value) => value switch
    {
        DBNull => "NULL",
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''", StringComparison.Ordinal)}'"
    };
}
