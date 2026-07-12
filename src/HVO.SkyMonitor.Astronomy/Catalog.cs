namespace HVO.SkyMonitor.Astronomy;

/// <summary>An immutable celestial catalog entry with right ascension in hours and declination in degrees.</summary>
public sealed record CelestialCatalogObject(
    string Id,
    string DisplayName,
    double RightAscensionHours,
    double DeclinationDegrees,
    double Magnitude,
    double? ColorIndex = null,
    string? HipparcosId = null);

/// <summary>Immutable provenance required to reproduce a catalog-backed derivative.</summary>
public sealed record CatalogMetadata(
    string Name,
    string Version,
    Uri SourceUrl,
    string Checksum,
    string License = "unspecified",
    string SchemaVersion = "unspecified");

/// <summary>Storage-neutral candidate criteria applied before exact sky visibility.</summary>
public sealed record CatalogCandidateQuery(double MaximumMagnitude)
{
    /// <summary>Validates the finite limiting magnitude.</summary>
    public void Validate()
    {
        if (!double.IsFinite(MaximumMagnitude))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMagnitude));
        }
    }
}

/// <summary>Criteria for deterministic catalog selection.</summary>
public sealed record CatalogQuery(double MaximumMagnitude, int MaximumResults)
{
    /// <summary>Validates the magnitude limit and positive bounded result count.</summary>
    public void Validate()
    {
        if (!double.IsFinite(MaximumMagnitude) || MaximumResults is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(CatalogQuery));
        }
    }
}

/// <summary>Queries a catalog without exposing source-file rows or persistence entities.</summary>
public interface ICelestialCatalog
{
    /// <summary>Returns matching objects ordered by brightness then stable identifier.</summary>
    IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query);

    /// <summary>
    /// Asynchronously returns all coarse-filtered candidates in magnitude-then-ID
    /// order. Exact visibility and visible-result limits are the caller's concern.
    /// </summary>
    ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
        CatalogCandidateQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes immutable provenance for a catalog that has validated its backing
/// snapshot. Consumers use this metadata rather than configuration defaults
/// when recording a reproducible scene.
/// </summary>
public interface ICelestialCatalogMetadataSource
{
    /// <summary>Gets source, version, license, schema, and checksum evidence for this catalog.</summary>
    CatalogMetadata Metadata { get; }

    /// <summary>Gets the deterministic preprocessing recipe version, or <c>unspecified</c>.</summary>
    string PreprocessingVersion => "unspecified";
}

/// <summary>Process-safe in-memory catalog with deterministic brightest-first selection.</summary>
public sealed class InMemoryCelestialCatalog : ICelestialCatalog
{
    private readonly CelestialCatalogObject[] _objects;

    /// <summary>Creates a catalog from validated immutable object records.</summary>
    public InMemoryCelestialCatalog(IEnumerable<CelestialCatalogObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        _objects = objects.Select(Validate).OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return _objects.Where(item => item.Magnitude <= query.MaximumMagnitude)
            .Take(query.MaximumResults).ToArray();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
        CatalogCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CelestialCatalogObject> result = _objects
            .Where(item => item.Magnitude <= query.MaximumMagnitude).ToArray();
        return ValueTask.FromResult(result);
    }

    private static CelestialCatalogObject Validate(CelestialCatalogObject value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
        if (!double.IsFinite(value.RightAscensionHours) || value.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(value.DeclinationDegrees) || value.DeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(value.Magnitude) || (value.ColorIndex is { } color && !double.IsFinite(color)) ||
            value.HipparcosId is { } hipparcosId &&
            (!int.TryParse(hipparcosId, out var hip) || hip <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

/// <summary>
/// Loads a simple HYG-compatible CSV catalog once and delegates all later queries
/// to an immutable in-memory index. Required headers are id, proper, ra, dec,
/// and mag; optional ci and hip supply color index and Hipparcos identity.
/// </summary>
public sealed class CsvCelestialCatalog : ICelestialCatalog
{
    private readonly InMemoryCelestialCatalog _catalog;

    /// <summary>Loads and validates catalog data from a UTF-8 CSV stream.</summary>
    public CsvCelestialCatalog(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _catalog = new InMemoryCelestialCatalog(ReadObjects(source));
    }

    /// <inheritdoc />
    public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
        CatalogCandidateQuery query,
        CancellationToken cancellationToken = default)
        => _catalog.QueryCandidatesAsync(query, cancellationToken);

    private static IEnumerable<CelestialCatalogObject> ReadObjects(Stream source)
    {
        using var reader = new StreamReader(source, leaveOpen: true);
        var header = reader.ReadLine() ?? throw new InvalidDataException("Catalog CSV is missing a header row.");
        var columns = header.Split(',').Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var id = RequiredColumn(columns, "id");
        var name = RequiredColumn(columns, "proper");
        var ra = RequiredColumn(columns, "ra");
        var dec = RequiredColumn(columns, "dec");
        var magnitude = RequiredColumn(columns, "mag");
        var hasColorIndex = columns.TryGetValue("ci", out var colorIndex);
        var hasHipparcosId = columns.TryGetValue("hip", out var hipparcosId);

        for (var lineNumber = 2; reader.ReadLine() is { } line; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length <= Math.Max(Math.Max(Math.Max(id, name), Math.Max(ra, dec)), magnitude))
            {
                throw new InvalidDataException($"Catalog row {lineNumber} has fewer fields than its header.");
            }

            yield return new CelestialCatalogObject(
                fields[id].Trim(),
                string.IsNullOrWhiteSpace(fields[name]) ? fields[id].Trim() : fields[name].Trim(),
                ParseDouble(fields[ra], lineNumber, "ra"),
                ParseDouble(fields[dec], lineNumber, "dec"),
                ParseDouble(fields[magnitude], lineNumber, "mag"),
                hasColorIndex && colorIndex < fields.Length && !string.IsNullOrWhiteSpace(fields[colorIndex])
                    ? ParseDouble(fields[colorIndex], lineNumber, "ci")
                    : null,
                hasHipparcosId && hipparcosId < fields.Length && !string.IsNullOrWhiteSpace(fields[hipparcosId])
                    ? fields[hipparcosId].Trim()
                    : null);
        }
    }

    private static int RequiredColumn(Dictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out var index)
            ? index
            : throw new InvalidDataException($"Catalog CSV is missing required '{name}' header.");

    private static double ParseDouble(string value, int lineNumber, string column)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Catalog row {lineNumber} has invalid '{column}' value.");
}
