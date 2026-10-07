using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>
/// Validates and loads a read-only SQLite snapshot into a connection-independent immutable cache.
/// </summary>
public sealed class SqliteCelestialCatalog : ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource,
    IAstrometricCatalogSource, ICelestialCatalogDetailsSource, IDeepSkyCatalogSource
{
    private const int Sha256HexLength = 64;
    private const string DetailedSchemaVersion = "3";
    // Schema 4 is schema 3's stars tables, unchanged, plus a deep-sky collection.
    private const string DeepSkySchemaVersion = "4";
    private const string DeepSkyAxisUnit = "arcminute";
    private const string DeepSkyOutlineLevelConvention = "1-widest-2-standard-3-narrowest";
    private const string DeepSkyPositionAngleConvention = "degrees-north-through-east-0-inclusive-to-180-exclusive";
    private const string DeepSkySurfaceBrightnessUnit = "b-mag-per-square-arcsecond-within-25-mag-isophote";
    private const string CoordinateFrame = "equatorial-j2000-icrs-aligned";
    private const string CoordinateEpoch = "J2000.0";
    private const string ProperMotionConvention = "mu-alpha-cos-delta-and-mu-delta-mas-per-year";
    private const string ProperMotionUnknownRule = "null-when-upstream-pair-is-zero-or-reaches-9999.99";
    private static readonly string[] DetailedMetadataKeys =
    [
        "catalog_version", "color_index", "coordinate_epoch", "coordinate_equinox", "coordinate_frame", "license",
        "magnitude_band", "name", "preprocessing_version", "proper_motion_convention", "proper_motion_unknown_rule",
        "schema_version", "source_commit", "source_url"
    ];
    private static readonly string[] DeepSkyMetadataKeys =
    [
        "deep_sky_axis_unit", "deep_sky_coordinate_epoch", "deep_sky_coordinate_frame", "deep_sky_license",
        "deep_sky_outline_level_convention", "deep_sky_position_angle_convention", "deep_sky_source_commit",
        "deep_sky_source_name", "deep_sky_source_url", "deep_sky_source_version", "deep_sky_surface_brightness_unit"
    ];
    private const string DeepSkyObjectQuery =
        "SELECT id, designation, display_name, object_type, right_ascension_hours, declination_degrees, constellation, " +
        "major_axis_arcminutes, minor_axis_arcminutes, position_angle_degrees, b_magnitude, v_magnitude, " +
        "surface_brightness, hubble_type, messier_number, caldwell_number, hipparcos_id, common_name " +
        "FROM deep_sky_objects ORDER BY id COLLATE BINARY";
    private const string DeepSkyAliasQuery =
        "SELECT alias, object_id, kind FROM deep_sky_aliases ORDER BY alias COLLATE BINARY, object_id COLLATE BINARY";
    private const string DeepSkyTombstoneQuery =
        "SELECT id, designation, reason, candidates FROM deep_sky_tombstones ORDER BY id COLLATE BINARY";
    private const string DeepSkyOutlineQuery =
        "SELECT object_id, level, ring, sequence, right_ascension_degrees, declination_degrees " +
        "FROM deep_sky_outline_points ORDER BY object_id COLLATE BINARY, level, ring, sequence";
    private const string LegacyObjectQuery =
        "SELECT id, display_name, right_ascension_hours, declination_degrees, magnitude, color_index, hipparcos_id " +
        "FROM celestial_objects ORDER BY magnitude, id COLLATE BINARY";
    private const string DetailedObjectQuery =
        "SELECT id, display_name, right_ascension_hours, declination_degrees, magnitude, color_index, hipparcos_id, " +
        "proper_motion_ra_cos_dec_mas_per_year, proper_motion_dec_mas_per_year, proper_name, bayer, flamsteed, " +
        "constellation, bayer_flamsteed, henry_draper_id, harvard_revised_id, gliese_id " +
        "FROM celestial_objects ORDER BY magnitude, id COLLATE BINARY";
    private static readonly string[] AliasKinds = ["bayer", "flamsteed", "gliese", "hd", "hr", "proper"];
    private readonly ReadOnlyCollection<CelestialCatalogObject> _objects;
    private readonly IReadOnlyDictionary<string, CelestialCatalogObject> _objectsByHipparcosId;
    private readonly Dictionary<string, CelestialCatalogObjectDetails> _detailsById;
    private readonly Lazy<Dictionary<string, CelestialCatalogAlias[]>> _aliases;

    /// <summary>Creates and fully loads a validated catalog snapshot.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The chained constructor disposes the authenticated source in its finally block.")]
    public SqliteCelestialCatalog(SqliteCelestialCatalogOptions options)
        : this(options, AuthenticateDatabase(options), expectedDatabaseLength: null, ownsAuthenticatedSource: true, astrometricProvenance: null)
    {
    }

    internal SqliteCelestialCatalog(
        SqliteCelestialCatalogOptions options,
        CatalogSnapshotResolver.AuthenticatedFile authenticatedSource,
        long expectedDatabaseLength,
        AstrometricCatalogProvenance astrometricProvenance)
        : this(options, authenticatedSource, expectedDatabaseLength, ownsAuthenticatedSource: false, astrometricProvenance)
    {
    }

    private SqliteCelestialCatalog(
        SqliteCelestialCatalogOptions options,
        CatalogSnapshotResolver.AuthenticatedFile authenticatedSource,
        long? expectedDatabaseLength,
        bool ownsAuthenticatedSource,
        AstrometricCatalogProvenance? astrometricProvenance)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(authenticatedSource);
            ValidateOptions(options);

            Options = options;
            AstrometricProvenance = astrometricProvenance;
            var databasePath = Path.GetFullPath(options.DatabasePath);
            ValidateNoSidecars(databasePath);
            using var privateSnapshot = PrivateSqliteSnapshot.Create(
                authenticatedSource.Stream,
                expectedDatabaseLength ?? authenticatedSource.Stream.Length,
                options.ExpectedSha256);
            CatalogSnapshotResolver.RevalidateFile(authenticatedSource, "Catalog database");

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            CatalogSnapshotResolver.InvokeValidationTestHook(databasePath, CatalogSnapshotValidationPoint.BeforeSqliteOpen);
            connection.Open();
            privateSnapshot.Load(connection);

            ValidateIntegrity(connection);
            var deepSky = string.Equals(options.ExpectedSchemaVersion, DeepSkySchemaVersion, StringComparison.Ordinal);
            var detailed = deepSky ||
                string.Equals(options.ExpectedSchemaVersion, DetailedSchemaVersion, StringComparison.Ordinal);
            if (!deepSky && options.ExpectedDeepSkyCounts is not null)
            {
                throw new ArgumentException(
                    $"Only a schema {DeepSkySchemaVersion} snapshot has deep-sky counts to expect.", nameof(options));
            }
            ValidateSchema(connection, detailed, deepSky);
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
                privateSnapshot.Sha256,
                RequiredMetadata(metadata, "license"),
                RequiredMetadata(metadata, "schema_version"));
            PreprocessingVersion = RequiredMetadata(metadata, "preprocessing_version");
            Semantics = detailed ? ReadSemantics(metadata, deepSky) : CatalogSemantics.LegacyFixedJ2000;
            var details = new Dictionary<string, CelestialCatalogObjectDetails>(StringComparer.Ordinal);
            _objects = Array.AsReadOnly(ReadObjects(connection, detailed ? details : null));
            _detailsById = details;
            if (detailed)
            {
                ValidateAliases(connection, details);
            }
            // The alias table is proven equal to the aliases derived from the retained designations, so the lookup
            // index is built from them on first use rather than held by every host that never searches by name.
            _aliases = new Lazy<Dictionary<string, CelestialCatalogAlias[]>>(
                () => BuildAliasIndex(details), LazyThreadSafetyMode.ExecutionAndPublication);
            if (options.ExpectedRowCount is { } expectedRowCount && _objects.Count != expectedRowCount)
            {
                throw new InvalidDataException(
                    $"Catalog row count mismatch. Expected {expectedRowCount}, got {_objects.Count}.");
            }
            if (deepSky)
            {
                var collection = ReadDeepSky(connection, ReadDeepSkySemantics(metadata));
                ValidateDeepSkyCounts(collection, options.ExpectedDeepSkyCounts);
                DeepSky = collection;
            }
            _objectsByHipparcosId = _objects
                .Where(static item => item.HipparcosId is not null)
                .ToDictionary(static item => item.HipparcosId!, StringComparer.Ordinal);
            CatalogSnapshotResolver.InvokeValidationTestHook(databasePath, CatalogSnapshotValidationPoint.AfterSqliteLoad);
            ValidateNoSidecars(databasePath);
        }
        finally
        {
            if (ownsAuthenticatedSource)
            {
                authenticatedSource.Dispose();
            }
        }
    }

    /// <summary>Gets the immutable options used to validate this snapshot.</summary>
    public SqliteCelestialCatalogOptions Options { get; }

    /// <summary>Gets immutable source and snapshot provenance.</summary>
    public CatalogMetadata Metadata { get; }

    /// <summary>Gets the checked preprocessing format version.</summary>
    public string PreprocessingVersion { get; }

    /// <summary>Gets package identity only when this instance was loaded by the installed-snapshot resolver.</summary>
    public AstrometricCatalogProvenance? AstrometricProvenance { get; }

    /// <summary>Gets the installed catalog identity, or <see langword="null"/> for a direct database load.</summary>
    public string? CatalogId => AstrometricProvenance?.CatalogId;

    /// <summary>Gets the installed package version, or <see langword="null"/> for a direct database load.</summary>
    public string? CatalogPackageVersion => AstrometricProvenance?.PackageVersion;

    /// <summary>
    /// Gets the declared catalog semantics. Snapshots before schema 3 predate declared semantics and
    /// report <see cref="CatalogSemantics.LegacyFixedJ2000"/>.
    /// </summary>
    public CatalogSemantics Semantics { get; }

    /// <summary>Gets the number of validated catalog objects in the immutable cache.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>
    /// Gets the validated deep-sky collection of a schema-4 snapshot, or <see langword="null"/> for an earlier schema.
    /// Its objects are separate from <see cref="ObjectCount"/>, which stays the star count.
    /// </summary>
    public DeepSkyCatalog? DeepSky { get; }

    IDeepSkyCatalog? IDeepSkyCatalogSource.DeepSky => DeepSky;

    /// <summary>
    /// Copies at most the requested solver bound from the immutable magnitude index. Only a fully
    /// validated production installation can declare complete sky coverage; fixtures and direct
    /// database loads remain explicitly incomplete even when every stored row fits the bound.
    /// </summary>
    public ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(maximumMagnitude))
            throw new ArgumentOutOfRangeException(nameof(maximumMagnitude));
        if (maximumEntries is < 1 or > AstrometricCatalogData.MaximumEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        cancellationToken.ThrowIfCancellationRequested();

        var matchingCount = FindUpperBound(maximumMagnitude);
        var selected = new CelestialCatalogObject[Math.Min(matchingCount, maximumEntries)];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected[index] = _objects[index];
        }
        var result = new AstrometricCatalogData(Metadata, selected,
            AstrometricProvenance?.PackageKind == "production" && matchingCount <= maximumEntries,
            maximumMagnitude, provenance: AstrometricProvenance);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

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

    /// <inheritdoc />
    public bool TryGetDetails(string objectId, out CelestialCatalogObjectDetails details)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return _detailsById.TryGetValue(objectId, out details!);
    }

    /// <inheritdoc />
    public IReadOnlyList<CelestialCatalogAlias> FindByAlias(string designation)
    {
        ArgumentNullException.ThrowIfNull(designation);
        return _aliases.Value.TryGetValue(designation.Trim(), out var matches) ? matches : [];
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

    private static CatalogSnapshotResolver.AuthenticatedFile AuthenticateDatabase(
        SqliteCelestialCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var databasePath = Path.GetFullPath(options.DatabasePath);
        ValidateNoSidecars(databasePath);
        return CatalogSnapshotResolver.AuthenticateFile(databasePath, "Catalog database");
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

    private static void ValidateSchema(SqliteConnection connection, bool detailed, bool deepSky)
    {
        var objects = ReadSchemaObjects(connection);
        var expectedObjects = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog_metadata"] = "table",
            ["celestial_objects"] = "table",
            ["celestial_objects_magnitude_id"] = "index"
        };
        if (detailed)
        {
            expectedObjects.Add("celestial_object_aliases", "table");
        }
        if (deepSky)
        {
            expectedObjects.Add("deep_sky_objects", "table");
            expectedObjects.Add("deep_sky_aliases", "table");
            expectedObjects.Add("deep_sky_tombstones", "table");
            expectedObjects.Add("deep_sky_outline_points", "table");
        }
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
        ColumnDefinition[] objectColumns =
        [
            new("id", "TEXT", true, 1),
            new("display_name", "TEXT", true, 0),
            new("right_ascension_hours", "REAL", true, 0),
            new("declination_degrees", "REAL", true, 0),
            new("magnitude", "REAL", true, 0),
            new("color_index", "REAL", false, 0),
            new("hipparcos_id", "TEXT", false, 0)
        ];
        if (detailed)
        {
            objectColumns =
            [
                .. objectColumns,
                new("proper_motion_ra_cos_dec_mas_per_year", "REAL", false, 0),
                new("proper_motion_dec_mas_per_year", "REAL", false, 0),
                new("proper_name", "TEXT", false, 0),
                new("bayer", "TEXT", false, 0),
                new("flamsteed", "TEXT", false, 0),
                new("constellation", "TEXT", false, 0),
                new("bayer_flamsteed", "TEXT", false, 0),
                new("henry_draper_id", "TEXT", false, 0),
                new("harvard_revised_id", "TEXT", false, 0),
                new("gliese_id", "TEXT", false, 0)
            ];
        }
        ValidateTable(connection, "celestial_objects", objectColumns);
        if (detailed)
        {
            ValidateTable(connection, "celestial_object_aliases",
            [
                new("alias", "TEXT", true, 1),
                new("object_id", "TEXT", true, 2),
                new("kind", "TEXT", true, 0)
            ]);
            ValidatePrimaryKeyOnly(connection, "celestial_object_aliases");
        }
        if (deepSky)
        {
            ValidateDeepSkySchema(connection);
        }
        ValidateIndex(connection);
    }

    private static void ValidateDeepSkySchema(SqliteConnection connection)
    {
        ValidateTable(connection, "deep_sky_objects",
        [
            new("id", "TEXT", true, 1),
            new("designation", "TEXT", true, 0),
            new("display_name", "TEXT", true, 0),
            new("object_type", "TEXT", true, 0),
            new("right_ascension_hours", "REAL", true, 0),
            new("declination_degrees", "REAL", true, 0),
            new("constellation", "TEXT", true, 0),
            new("major_axis_arcminutes", "REAL", false, 0),
            new("minor_axis_arcminutes", "REAL", false, 0),
            new("position_angle_degrees", "REAL", false, 0),
            new("b_magnitude", "REAL", false, 0),
            new("v_magnitude", "REAL", false, 0),
            new("surface_brightness", "REAL", false, 0),
            new("hubble_type", "TEXT", false, 0),
            new("messier_number", "INTEGER", false, 0),
            new("caldwell_number", "INTEGER", false, 0),
            new("hipparcos_id", "TEXT", false, 0),
            new("common_name", "TEXT", false, 0)
        ]);
        ValidateTable(connection, "deep_sky_aliases",
        [
            new("alias", "TEXT", true, 1),
            new("object_id", "TEXT", true, 2),
            new("kind", "TEXT", true, 0)
        ]);
        ValidateTable(connection, "deep_sky_tombstones",
        [
            new("id", "TEXT", true, 1),
            new("designation", "TEXT", true, 0),
            new("reason", "TEXT", true, 0),
            new("candidates", "TEXT", false, 0)
        ]);
        ValidateTable(connection, "deep_sky_outline_points",
        [
            new("object_id", "TEXT", true, 1),
            new("level", "INTEGER", true, 2),
            new("ring", "INTEGER", true, 3),
            new("sequence", "INTEGER", true, 4),
            new("right_ascension_degrees", "REAL", true, 0),
            new("declination_degrees", "REAL", true, 0)
        ]);
        foreach (var table in (string[])["deep_sky_objects", "deep_sky_aliases", "deep_sky_tombstones", "deep_sky_outline_points"])
        {
            ValidatePrimaryKeyOnly(connection, table);
        }
    }

    private static void ValidatePrimaryKeyOnly(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT origin FROM pragma_index_list($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var primaryKeys = 0;
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), "pk", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Catalog {table} table contains an unexpected index.");
            }
            primaryKeys++;
        }
        if (primaryKeys != 1)
        {
            throw new InvalidDataException($"Catalog {table} primary key is missing.");
        }
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
            "celestial_object_aliases" => "PRAGMA table_list('celestial_object_aliases')",
            "deep_sky_objects" => "PRAGMA table_list('deep_sky_objects')",
            "deep_sky_aliases" => "PRAGMA table_list('deep_sky_aliases')",
            "deep_sky_tombstones" => "PRAGMA table_list('deep_sky_tombstones')",
            "deep_sky_outline_points" => "PRAGMA table_list('deep_sky_outline_points')",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var tableInfoCommandText = name switch
        {
            "catalog_metadata" => "PRAGMA table_xinfo('catalog_metadata')",
            "celestial_objects" => "PRAGMA table_xinfo('celestial_objects')",
            "celestial_object_aliases" => "PRAGMA table_xinfo('celestial_object_aliases')",
            "deep_sky_objects" => "PRAGMA table_xinfo('deep_sky_objects')",
            "deep_sky_aliases" => "PRAGMA table_xinfo('deep_sky_aliases')",
            "deep_sky_tombstones" => "PRAGMA table_xinfo('deep_sky_tombstones')",
            "deep_sky_outline_points" => "PRAGMA table_xinfo('deep_sky_outline_points')",
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

    private static CatalogSemantics ReadSemantics(Dictionary<string, string> metadata, bool deepSky)
    {
        string[] keys = deepSky ? [.. DetailedMetadataKeys, .. DeepSkyMetadataKeys] : DetailedMetadataKeys;
        if (metadata.Count != keys.Length || keys.Any(key => !metadata.ContainsKey(key)))
        {
            throw new InvalidDataException(
                $"Catalog schema {(deepSky ? DeepSkySchemaVersion : DetailedSchemaVersion)} metadata must contain exactly the declared semantic keys.");
        }
        ValidateVersion("coordinate_frame", CoordinateFrame, metadata);
        ValidateVersion("coordinate_equinox", CoordinateEpoch, metadata);
        ValidateVersion("coordinate_epoch", CoordinateEpoch, metadata);
        ValidateVersion("proper_motion_convention", ProperMotionConvention, metadata);
        ValidateVersion("proper_motion_unknown_rule", ProperMotionUnknownRule, metadata);
        var sourceCommit = RequiredMetadata(metadata, "source_commit");
        if (sourceCommit.Length != 40 || !sourceCommit.All(static item => item is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException("Catalog source_commit must be a lowercase 40-character commit ID.");
        }
        return new CatalogSemantics(
            CoordinateFrame,
            CoordinateEpoch,
            CoordinateEpoch,
            RequiredMetadata(metadata, "magnitude_band"),
            RequiredMetadata(metadata, "color_index"),
            ProperMotionConvention);
    }

    /// <summary>
    /// Proves in one streaming pass that the alias table holds exactly the aliases preprocessing derives from each
    /// object's designations: every row is trimmed, typed, names a catalog object, and equals that object's derived
    /// alias of its kind, and the row count equals the number of derived aliases. The (alias, object) primary key
    /// makes each row claim a distinct object/kind slot, so equal counts mean no derived alias is missing.
    /// </summary>
    private static void ValidateAliases(
        SqliteConnection connection,
        Dictionary<string, CelestialCatalogObjectDetails> details)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias, object_id, kind FROM celestial_object_aliases";
        using var reader = command.ExecuteReader();
        long rows = 0;
        while (reader.Read())
        {
            var alias = reader.GetString(0);
            var kind = reader.GetString(2);
            if (string.IsNullOrWhiteSpace(alias) || !string.Equals(alias, alias.Trim(), StringComparison.Ordinal) ||
                Array.IndexOf(AliasKinds, kind) < 0 || !details.TryGetValue(reader.GetString(1), out var target) ||
                !MatchesDerivedAlias(alias, kind, target.Designations))
            {
                throw new InvalidDataException(
                    "Catalog alias rows must be trimmed, typed, and equal an alias derived from a catalog object.");
            }
            rows++;
        }

        long expected = 0;
        foreach (var item in details.Values)
        {
            foreach (var kind in AliasKinds)
            {
                if (DerivedAlias(item.Designations, kind) is not null)
                {
                    expected++;
                }
            }
        }
        if (rows != expected)
        {
            throw new InvalidDataException(
                $"Catalog alias table holds {rows} aliases; its designations derive {expected}.");
        }
    }

    private static bool MatchesDerivedAlias(string alias, string kind, CelestialObjectDesignations designations)
    {
        var value = alias.AsSpan();
        return kind switch
        {
            "proper" => designations.ProperName is { } proper && value.SequenceEqual(proper),
            "bayer" => MatchesConstellationAlias(value, designations.Bayer, designations.Constellation),
            "flamsteed" => MatchesConstellationAlias(value, designations.Flamsteed, designations.Constellation),
            "hd" => designations.HenryDraperId is { } hd && value.StartsWith("HD ") && value[3..].SequenceEqual(hd),
            "hr" => designations.HarvardRevisedId is { } hr && value.StartsWith("HR ") && value[3..].SequenceEqual(hr),
            "gliese" => designations.GlieseId is { } gliese && value.SequenceEqual(gliese),
            _ => false
        };
    }

    private static bool MatchesConstellationAlias(ReadOnlySpan<char> alias, string? designation, string? constellation)
        => designation is not null && constellation is not null &&
           alias.Length == designation.Length + 1 + constellation.Length &&
           alias.StartsWith(designation) && alias[designation.Length] == ' ' &&
           alias[(designation.Length + 1)..].SequenceEqual(constellation);

    /// <summary>Derives one alias exactly as preprocessing version 4 writes it, or <see langword="null"/>.</summary>
    private static string? DerivedAlias(CelestialObjectDesignations designations, string kind) => kind switch
    {
        "proper" => designations.ProperName,
        "bayer" => designations is { Bayer: { } bayer, Constellation: { } constellation } ? $"{bayer} {constellation}" : null,
        "flamsteed" => designations is { Flamsteed: { } flamsteed, Constellation: { } constellation }
            ? $"{flamsteed} {constellation}"
            : null,
        "hd" => designations.HenryDraperId is { } hd ? $"HD {hd}" : null,
        "hr" => designations.HarvardRevisedId is { } hr ? $"HR {hr}" : null,
        "gliese" => designations.GlieseId,
        _ => null
    };

    private static Dictionary<string, CelestialCatalogAlias[]> BuildAliasIndex(
        Dictionary<string, CelestialCatalogObjectDetails> details)
    {
        var grouped = new Dictionary<string, List<CelestialCatalogAlias>>(StringComparer.OrdinalIgnoreCase);
        var ids = details.Keys.ToArray();
        Array.Sort(ids, StringComparer.Ordinal);
        var derived = new List<CelestialCatalogAlias>(AliasKinds.Length);
        foreach (var id in ids)
        {
            var designations = details[id].Designations;
            derived.Clear();
            foreach (var kind in AliasKinds)
            {
                if (DerivedAlias(designations, kind) is { } alias)
                {
                    derived.Add(new CelestialCatalogAlias(alias, id, kind));
                }
            }
            // Objects are visited in ordinal ID order, so each group is ordered by object ID and then alias.
            derived.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));
            foreach (var item in derived)
            {
                if (!grouped.TryGetValue(item.Alias, out var matches))
                {
                    grouped.Add(item.Alias, matches = []);
                }
                matches.Add(item);
            }
        }

        var result = new Dictionary<string, CelestialCatalogAlias[]>(grouped.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, matches) in grouped)
        {
            result.Add(alias, matches.ToArray());
        }
        return result;
    }

    /// <summary>
    /// Reads the deep-sky provenance and requires each convention to be the one this runtime interprets, so that a
    /// snapshot declaring another unit or convention fails closed instead of being drawn with the wrong meaning.
    /// </summary>
    private static DeepSkySemantics ReadDeepSkySemantics(IReadOnlyDictionary<string, string> metadata)
    {
        ValidateVersion("deep_sky_coordinate_frame", CoordinateFrame, metadata);
        ValidateVersion("deep_sky_coordinate_epoch", CoordinateEpoch, metadata);
        ValidateVersion("deep_sky_axis_unit", DeepSkyAxisUnit, metadata);
        ValidateVersion("deep_sky_position_angle_convention", DeepSkyPositionAngleConvention, metadata);
        ValidateVersion("deep_sky_outline_level_convention", DeepSkyOutlineLevelConvention, metadata);
        ValidateVersion("deep_sky_surface_brightness_unit", DeepSkySurfaceBrightnessUnit, metadata);
        if (!Uri.TryCreate(RequiredMetadata(metadata, "deep_sky_source_url"), UriKind.Absolute, out var sourceUrl) ||
            sourceUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Catalog deep_sky_source_url must be an absolute HTTPS URL.");
        }
        return new DeepSkySemantics(
            RequiredMetadata(metadata, "deep_sky_source_name"),
            RequiredMetadata(metadata, "deep_sky_source_version"),
            RequiredMetadata(metadata, "deep_sky_source_commit"),
            sourceUrl,
            RequiredMetadata(metadata, "deep_sky_license"),
            CoordinateFrame,
            CoordinateEpoch,
            DeepSkyAxisUnit,
            DeepSkyPositionAngleConvention,
            DeepSkyOutlineLevelConvention,
            DeepSkySurfaceBrightnessUnit);
    }

    private static DeepSkyCatalog ReadDeepSky(SqliteConnection connection, DeepSkySemantics semantics)
    {
        try
        {
            return new DeepSkyCatalog(
                semantics,
                ReadDeepSkyObjects(connection),
                ReadDeepSkyAliases(connection),
                ReadDeepSkyTombstones(connection),
                ReadDeepSkyOutlines(connection));
        }
        catch (InvalidCastException exception)
        {
            throw new InvalidDataException("Catalog deep-sky tables contain a value of an unsupported type.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Catalog deep-sky tables contain an out-of-range number.", exception);
        }
    }

    private static List<DeepSkyObject> ReadDeepSkyObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = DeepSkyObjectQuery;
        using var reader = command.ExecuteReader();
        var result = new List<DeepSkyObject>();
        // OpenNGC names one of 89 constellation abbreviations and a few dozen type codes; each is retained once.
        var pool = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(new DeepSkyObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Shared(pool, reader.GetString(3)),
                reader.GetDouble(4),
                reader.GetDouble(5),
                Shared(pool, reader.GetString(6)),
                OptionalDouble(reader, 7),
                OptionalDouble(reader, 8),
                OptionalDouble(reader, 9),
                OptionalDouble(reader, 10),
                OptionalDouble(reader, 11),
                OptionalDouble(reader, 12),
                reader.IsDBNull(13) ? null : Shared(pool, reader.GetString(13)),
                OptionalInt32(reader, 14),
                OptionalInt32(reader, 15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17)));
        }
        return result;
    }

    private static List<DeepSkyAlias> ReadDeepSkyAliases(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = DeepSkyAliasQuery;
        using var reader = command.ExecuteReader();
        var result = new List<DeepSkyAlias>();
        var pool = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            // Object IDs and kinds repeat across alias rows; one string per distinct value is retained.
            result.Add(new DeepSkyAlias(reader.GetString(0), Shared(pool, reader.GetString(1)), Shared(pool, reader.GetString(2))));
        }
        return result;
    }

    private static List<DeepSkyTombstone> ReadDeepSkyTombstones(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = DeepSkyTombstoneQuery;
        using var reader = command.ExecuteReader();
        var result = new List<DeepSkyTombstone>();
        while (reader.Read())
        {
            result.Add(new DeepSkyTombstone(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? [] : reader.GetString(3).Split(',')));
        }
        return result;
    }

    /// <summary>
    /// Reads outline points in key order. Within each object and level the rings must be numbered 0, 1, ... and each
    /// ring's points 0, 1, ..., so a gap or a renumbering fails here rather than silently joining two rings.
    /// </summary>
    private static List<DeepSkyOutline> ReadDeepSkyOutlines(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = DeepSkyOutlineQuery;
        using var reader = command.ExecuteReader();
        var result = new List<DeepSkyOutline>();
        var rings = new List<DeepSkyOutlineRing>();
        var points = new List<DeepSkyOutlinePoint>();
        string? objectId = null;
        var level = 0L;
        var ring = 0L;
        while (reader.Read())
        {
            var rowObject = reader.GetString(0);
            var rowLevel = reader.GetInt64(1);
            var rowRing = reader.GetInt64(2);
            var rowSequence = reader.GetInt64(3);
            var sameSet = string.Equals(rowObject, objectId, StringComparison.Ordinal) && rowLevel == level;
            if (!sameSet || rowRing != ring)
            {
                if (objectId is not null)
                {
                    rings.Add(new DeepSkyOutlineRing(points.ToArray()));
                    points.Clear();
                }
                if (!sameSet)
                {
                    if (objectId is not null)
                    {
                        result.Add(new DeepSkyOutline(objectId, checked((int)level), rings.ToArray()));
                        rings.Clear();
                    }
                    objectId = rowObject;
                    level = rowLevel;
                }
                ring = rowRing;
                if (rowRing != rings.Count)
                {
                    throw new InvalidDataException($"Catalog outline rings for '{rowObject}' are not numbered from zero.");
                }
            }
            if (rowSequence != points.Count)
            {
                throw new InvalidDataException($"Catalog outline points for '{rowObject}' are not numbered from zero.");
            }
            points.Add(new DeepSkyOutlinePoint(reader.GetDouble(4), reader.GetDouble(5)));
        }
        if (objectId is not null)
        {
            rings.Add(new DeepSkyOutlineRing(points.ToArray()));
            result.Add(new DeepSkyOutline(objectId, checked((int)level), rings.ToArray()));
        }
        return result;
    }

    private static void ValidateDeepSkyCounts(DeepSkyCatalog collection, CatalogDeepSkyCounts? expected)
    {
        var actual = new CatalogDeepSkyCounts(
            collection.Objects.Count,
            collection.AliasCount,
            collection.Tombstones.Count,
            collection.OutlineObjectCount,
            collection.OutlineCount,
            collection.OutlineRingCount,
            collection.OutlinePointCount);
        if (expected is not null && actual != expected)
        {
            throw new InvalidDataException($"Catalog deep-sky counts mismatch. Expected {expected}, got {actual}.");
        }
    }

    private static double? OptionalDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static int? OptionalInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : checked((int)reader.GetInt64(ordinal));

    private static string Shared(HashSet<string> pool, string value)
        => pool.TryGetValue(value, out var shared) ? shared : Add(pool, value);

    private static CelestialCatalogObject[] ReadObjects(
        SqliteConnection connection,
        Dictionary<string, CelestialCatalogObjectDetails>? details)
    {
        using var command = connection.CreateCommand();
        if (details is null)
        {
            command.CommandText = LegacyObjectQuery;
        }
        else
        {
            command.CommandText = DetailedObjectQuery;
        }
        using var reader = command.ExecuteReader();
        var objects = new List<CelestialCatalogObject>();
        var hipparcosIds = new HashSet<string>(StringComparer.Ordinal);
        // Every row names one of 88 constellation abbreviations; one shared string per abbreviation is retained.
        var constellations = new HashSet<string>(StringComparer.Ordinal);
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
            details?.Add(value.Id, ReadDetails(reader, value.Id, constellations));
            objects.Add(value);
        }

        return objects.ToArray();
    }

    private static CelestialCatalogObjectDetails ReadDetails(SqliteDataReader reader, string id, HashSet<string> constellations)
    {
        CatalogProperMotion? properMotion = (reader.IsDBNull(7), reader.IsDBNull(8)) switch
        {
            (true, true) => null,
            (false, false) => new CatalogProperMotion(reader.GetDouble(7), reader.GetDouble(8)),
            _ => throw new InvalidDataException($"Catalog object '{id}' has a half-known proper motion.")
        };
        if (properMotion is { } motion &&
            (!double.IsFinite(motion.RightAscensionCosDeclinationMasPerYear) || !double.IsFinite(motion.DeclinationMasPerYear)))
        {
            throw new InvalidDataException($"Catalog object '{id}' has a non-finite proper motion.");
        }

        return new CelestialCatalogObjectDetails(id, properMotion, new CelestialObjectDesignations(
            Designation(reader, 9, id),
            Designation(reader, 10, id),
            Designation(reader, 11, id),
            Designation(reader, 12, id) is { } constellation
                ? constellations.TryGetValue(constellation, out var shared) ? shared : Add(constellations, constellation)
                : null,
            Designation(reader, 13, id),
            Designation(reader, 14, id),
            Designation(reader, 15, id),
            Designation(reader, 16, id)));
    }

    private static string Add(HashSet<string> pool, string value)
    {
        pool.Add(value);
        return value;
    }

    private static string? Designation(SqliteDataReader reader, int ordinal, string id)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        var value = reader.GetString(ordinal);
        return string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new InvalidDataException($"Catalog object '{id}' has a blank or untrimmed designation.")
            : value;
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
