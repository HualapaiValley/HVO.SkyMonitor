using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>
/// Validates and loads a read-only SQLite snapshot into a connection-independent immutable cache.
/// </summary>
public sealed class SqliteCelestialCatalog : ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource
{
    private const int Sha256HexLength = 64;
    private readonly ReadOnlyCollection<CelestialCatalogObject> _objects;
    private readonly IReadOnlyDictionary<string, CelestialCatalogObject> _objectsByHipparcosId;

    /// <summary>Creates and fully loads a validated catalog snapshot.</summary>
    public SqliteCelestialCatalog(SqliteCelestialCatalogOptions options)
        : this(options, null, null)
    {
    }

    internal SqliteCelestialCatalog(
        SqliteCelestialCatalogOptions options,
        FileStream? authenticatedSource,
        string? authenticatedDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        Options = options;
        var databasePath = Path.GetFullPath(options.DatabasePath);
        ValidateNoSidecars(databasePath);
        var actualChecksum = authenticatedSource is null
            ? ValidateChecksum(databasePath, options.ExpectedSha256)
            : ValidateChecksum(authenticatedSource, options.ExpectedSha256);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(authenticatedDatabasePath ?? databasePath).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        CatalogSnapshotResolver.InvokeValidationTestHook(databasePath, CatalogSnapshotValidationPoint.BeforeSqliteOpen);
        connection.Open();

        ValidateIntegrity(connection);
        ValidateSchema(connection);
        var metadata = ReadMetadata(connection);
        ValidateVersion("schema_version", options.ExpectedSchemaVersion, metadata);
        ValidateVersion("preprocessing_version", options.ExpectedPreprocessingVersion, metadata);
        if (options.ExpectedCatalogVersion is { } expectedCatalogVersion)
        {
            ValidateVersion("catalog_version", expectedCatalogVersion, metadata);
        }
        ValidateUserVersion(connection, options.ExpectedSchemaVersion);

        Metadata = new CatalogMetadata(
            RequiredMetadata(metadata, "name"),
            RequiredMetadata(metadata, "catalog_version"),
            new Uri(RequiredMetadata(metadata, "source_url"), UriKind.Absolute),
            actualChecksum,
            RequiredMetadata(metadata, "license"),
            RequiredMetadata(metadata, "schema_version"));
        PreprocessingVersion = RequiredMetadata(metadata, "preprocessing_version");
        _objects = Array.AsReadOnly(ReadObjects(connection));
        if (options.ExpectedRowCount is { } expectedRowCount && _objects.Count != expectedRowCount)
        {
            throw new InvalidDataException(
                $"Catalog row count mismatch. Expected {expectedRowCount}, got {_objects.Count}.");
        }
        _objectsByHipparcosId = _objects
            .Where(static item => item.HipparcosId is not null)
            .ToDictionary(static item => item.HipparcosId!, StringComparer.Ordinal);
        CatalogSnapshotResolver.InvokeValidationTestHook(databasePath, CatalogSnapshotValidationPoint.AfterSqliteLoad);
        ValidateNoSidecars(databasePath);
    }

    /// <summary>Gets the immutable options used to validate this snapshot.</summary>
    public SqliteCelestialCatalogOptions Options { get; }

    /// <summary>Gets immutable source and snapshot provenance.</summary>
    public CatalogMetadata Metadata { get; }

    /// <summary>Gets the checked preprocessing format version.</summary>
    public string PreprocessingVersion { get; }

    /// <summary>Gets the number of validated catalog objects in the immutable cache.</summary>
    public int ObjectCount => _objects.Count;

    /// <inheritdoc />
    public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var count = Math.Min(FindUpperBound(query.MaximumMagnitude), query.MaximumResults);
        var result = new CelestialCatalogObject[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = _objects[index];
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
        CatalogCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var count = FindUpperBound(query.MaximumMagnitude);
        var candidates = new List<CelestialCatalogObject>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = _objects[index];
            if (query.J2000Region is not { } region ||
                region.Contains(candidate.RightAscensionHours, candidate.DeclinationDegrees))
            {
                candidates.Add(candidate);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>(candidates.ToArray());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(
        IReadOnlyCollection<string> hipparcosIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hipparcosIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (hipparcosIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Hipparcos identifiers cannot be blank.", nameof(hipparcosIds));
        }

        IReadOnlyList<CelestialCatalogObject> result = hipparcosIds
            .Distinct(StringComparer.Ordinal)
            .Select(hip => _objectsByHipparcosId.GetValueOrDefault(hip))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    private int FindUpperBound(double maximumMagnitude)
    {
        var low = 0;
        var high = _objects.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_objects[middle].Magnitude <= maximumMagnitude)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void ValidateOptions(SqliteCelestialCatalogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedPreprocessingVersion);
        if (options.ExpectedRowCount is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ExpectedRowCount must be positive and cacheable.");
        }
        if (options.ExpectedCatalogVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedCatalogVersion);
        }
        if (options.ExpectedSha256.Length != Sha256HexLength ||
            !options.ExpectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("ExpectedSha256 must contain exactly 64 hexadecimal characters.", nameof(options));
        }
    }

    private static string ValidateChecksum(string path, string expectedChecksum)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = SHA256.HashData(source);
        var expected = Convert.FromHexString(expectedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                $"Catalog snapshot SHA-256 mismatch. Expected {expectedChecksum.ToUpperInvariant()}, got {Convert.ToHexString(actual)}.");
        }

        return Convert.ToHexString(actual);
    }

    private static string ValidateChecksum(FileStream source, string expectedChecksum)
    {
        source.Position = 0;
        var actual = SHA256.HashData(source);
        source.Position = 0;
        var expected = Convert.FromHexString(expectedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                $"Catalog snapshot SHA-256 mismatch. Expected {expectedChecksum.ToUpperInvariant()}, got {Convert.ToHexString(actual)}.");
        }

        return Convert.ToHexString(actual);
    }

    private static Dictionary<string, string> ReadMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM catalog_metadata ORDER BY key";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (!result.TryAdd(reader.GetString(0), reader.GetString(1)))
            {
                throw new InvalidDataException("Catalog metadata contains duplicate keys.");
            }
        }

        return result;
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal) || reader.Read())
        {
            throw new InvalidDataException("Catalog SQLite integrity_check failed.");
        }
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        var objects = ReadSchemaObjects(connection);
        var expectedObjects = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog_metadata"] = "table",
            ["celestial_objects"] = "table",
            ["celestial_objects_magnitude_id"] = "index"
        };
        if (objects.Count != expectedObjects.Count ||
            expectedObjects.Any(expected => !objects.TryGetValue(expected.Key, out var type) || type != expected.Value))
        {
            throw new InvalidDataException("Catalog SQLite schema must contain exactly the required tables and index.");
        }

        ValidateTable(connection, "catalog_metadata",
        [
            new("key", "TEXT", true, 1),
            new("value", "TEXT", true, 0)
        ]);
        ValidateTable(connection, "celestial_objects",
        [
            new("id", "TEXT", true, 1),
            new("display_name", "TEXT", true, 0),
            new("right_ascension_hours", "REAL", true, 0),
            new("declination_degrees", "REAL", true, 0),
            new("magnitude", "REAL", true, 0),
            new("color_index", "REAL", false, 0),
            new("hipparcos_id", "TEXT", false, 0)
        ]);
        ValidateIndex(connection);
    }

    private static Dictionary<string, string> ReadSchemaObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, type FROM sqlite_schema " +
            "WHERE name NOT LIKE 'sqlite_%' AND type IN ('table', 'index', 'view', 'trigger') ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }
        return result;
    }

    private static void ValidateTable(SqliteConnection connection, string name, IReadOnlyList<ColumnDefinition> expected)
    {
        var tableListCommandText = name switch
        {
            "catalog_metadata" => "PRAGMA table_list('catalog_metadata')",
            "celestial_objects" => "PRAGMA table_list('celestial_objects')",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var tableInfoCommandText = name switch
        {
            "catalog_metadata" => "PRAGMA table_xinfo('catalog_metadata')",
            "celestial_objects" => "PRAGMA table_xinfo('celestial_objects')",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = tableListCommandText;
            using var tableReader = tableCommand.ExecuteReader();
            if (!tableReader.Read() || tableReader.GetInt32(4) != 1 || tableReader.GetInt32(5) != 0 || tableReader.Read())
            {
                throw new InvalidDataException($"Catalog table '{name}' must be a non-STRICT WITHOUT ROWID table.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = tableInfoCommandText;
        using var reader = command.ExecuteReader();
        var actual = new List<ColumnDefinition>();
        while (reader.Read())
        {
            if (reader.GetInt32(6) != 0 || !reader.IsDBNull(4))
            {
                throw new InvalidDataException($"Catalog table '{name}' has unsupported hidden columns or defaults.");
            }
            actual.Add(new ColumnDefinition(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(5)));
        }
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"Catalog table '{name}' does not match the required schema.");
        }
    }

    private static void ValidateIndex(SqliteConnection connection)
    {
        using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = "PRAGMA index_list('celestial_objects')";
            using var listReader = listCommand.ExecuteReader();
            var found = false;
            while (listReader.Read())
            {
                if (string.Equals(listReader.GetString(1), "celestial_objects_magnitude_id", StringComparison.Ordinal))
                {
                    found = listReader.GetInt32(2) == 0 && string.Equals(listReader.GetString(3), "c", StringComparison.Ordinal) &&
                        listReader.GetInt32(4) == 0;
                }
                else if (!string.Equals(listReader.GetString(3), "pk", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Catalog celestial_objects table contains an unexpected index.");
                }
            }
            if (!found)
            {
                throw new InvalidDataException("Catalog magnitude/ID index is missing or incompatible.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_xinfo('celestial_objects_magnitude_id')";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, bool Descending, string Collation, bool Key)>();
        while (reader.Read())
        {
            columns.Add((reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), reader.GetBoolean(5)));
        }
        (string Name, bool Descending, string Collation, bool Key)[] expected =
        [
            ("magnitude", false, "BINARY", true),
            ("id", false, "BINARY", true)
        ];
        if (!columns.SequenceEqual(expected))
        {
            throw new InvalidDataException("Catalog magnitude/ID index does not match the required definition.");
        }
    }

    private static void ValidateVersion(
        string key,
        string expected,
        IReadOnlyDictionary<string, string> metadata)
    {
        var actual = RequiredMetadata(metadata, key);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Catalog {key} mismatch. Expected '{expected}', got '{actual}'.");
        }
    }

    private static void ValidateUserVersion(SqliteConnection connection, string expectedSchemaVersion)
    {
        if (!int.TryParse(expectedSchemaVersion, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedUserVersion))
        {
            throw new ArgumentException("ExpectedSchemaVersion must be an integer SQLite user_version.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var actual = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (actual != expectedUserVersion)
        {
            throw new InvalidDataException(
                $"Catalog SQLite user_version mismatch. Expected {expectedUserVersion}, got {actual}.");
        }
    }

    private static string RequiredMetadata(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Catalog metadata is missing required key '{key}'.");

    private static CelestialCatalogObject[] ReadObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, display_name, right_ascension_hours, declination_degrees, magnitude, color_index, hipparcos_id " +
            "FROM celestial_objects ORDER BY magnitude, id COLLATE BINARY";
        using var reader = command.ExecuteReader();
        var objects = new List<CelestialCatalogObject>();
        var hipparcosIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var value = new CelestialCatalogObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
            ValidateObject(value);
            if (value.Id == "0")
            {
                throw new InvalidDataException("Catalog fixed-star rows must exclude Sol (ID 0).");
            }
            if (value.HipparcosId is { } hipparcosId && !hipparcosIds.Add(hipparcosId))
            {
                throw new InvalidDataException($"Catalog contains duplicate Hipparcos identifier '{hipparcosId}'.");
            }
            objects.Add(value);
        }

        return objects.ToArray();
    }

    private static void ValidateObject(CelestialCatalogObject value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.DisplayName) ||
            !double.IsFinite(value.RightAscensionHours) || value.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(value.DeclinationDegrees) || value.DeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(value.Magnitude) || value.ColorIndex is { } color && !double.IsFinite(color) ||
            value.HipparcosId is { } hipparcosId &&
            (!int.TryParse(hipparcosId, NumberStyles.None, CultureInfo.InvariantCulture, out var hip) || hip <= 0 ||
             !string.Equals(hipparcosId, hip.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Catalog object '{value.Id}' contains invalid data.");
        }
    }

    private static void ValidateNoSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
            {
                throw new InvalidDataException($"Catalog snapshot has forbidden SQLite sidecar '{suffix}'.");
            }
        }
    }

    private sealed record ColumnDefinition(string Name, string Type, bool NotNull, int PrimaryKeyOrder);
}
