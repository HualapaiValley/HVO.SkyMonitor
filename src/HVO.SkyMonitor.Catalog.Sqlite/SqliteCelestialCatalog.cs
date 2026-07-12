using System.Collections.ObjectModel;
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
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        Options = options;
        var actualChecksum = ValidateChecksum(options.DatabasePath, options.ExpectedSha256);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var metadata = ReadMetadata(connection);
        ValidateVersion("schema_version", options.ExpectedSchemaVersion, metadata);
        ValidateVersion("preprocessing_version", options.ExpectedPreprocessingVersion, metadata);
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
        _objectsByHipparcosId = _objects
            .Where(static item => item.HipparcosId is not null)
            .GroupBy(static item => item.HipparcosId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>Gets the immutable options used to validate this snapshot.</summary>
    public SqliteCelestialCatalogOptions Options { get; }

    /// <summary>Gets immutable source and snapshot provenance.</summary>
    public CatalogMetadata Metadata { get; }

    /// <summary>Gets the checked preprocessing format version.</summary>
    public string PreprocessingVersion { get; }

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
        var hasHipparcosId = HasHipparcosIdColumn(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, display_name, right_ascension_hours, declination_degrees, magnitude, color_index, " +
            (hasHipparcosId ? "hipparcos_id " : "NULL ") +
            "FROM celestial_objects ORDER BY magnitude, id COLLATE BINARY";
        using var reader = command.ExecuteReader();
        var objects = new List<CelestialCatalogObject>();
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
            objects.Add(value);
        }

        return objects.ToArray();
    }

    private static bool HasHipparcosIdColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(celestial_objects)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "hipparcos_id", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateObject(CelestialCatalogObject value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.DisplayName) ||
            !double.IsFinite(value.RightAscensionHours) || value.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(value.DeclinationDegrees) || value.DeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(value.Magnitude) || value.ColorIndex is { } color && !double.IsFinite(color) ||
            value.HipparcosId is { } hipparcosId &&
            (!int.TryParse(hipparcosId, out var hip) || hip <= 0))
        {
            throw new InvalidDataException($"Catalog object '{value.Id}' contains invalid data.");
        }
    }
}
