using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Byte evidence for one retained or source file.</summary>
public sealed record ApprovedCatalogFile(string RelativePath, string Sha256, long Length);

/// <summary>Length and SHA-256 of one source representation.</summary>
public sealed record ApprovedCatalogEvidence(string Sha256, long Length);

/// <summary>The pinned upstream source of an approved catalog.</summary>
public sealed record ApprovedCatalogSource(
    Uri ProjectUrl,
    Uri DownloadUrl,
    string Oid,
    ApprovedCatalogEvidence Compressed,
    ApprovedCatalogEvidence Decompressed);

/// <summary>The exact database an approved catalog package must contain.</summary>
public sealed record ApprovedCatalogDatabase(
    string RelativePath,
    string Sha256,
    long Length,
    long RowCount,
    long SolCount,
    string RequiredColumn);

/// <summary>The license chain an approved catalog package must retain.</summary>
public sealed record ApprovedCatalogLicense(
    string Identifier,
    Uri Url,
    ApprovedCatalogFile File,
    ApprovedCatalogFile Attribution);

/// <summary>The constellation topology an approved catalog must satisfy, including its pinned missing endpoints.</summary>
public sealed record ApprovedCatalogTopology(
    string Identity,
    string Sha256,
    long ConstellationCount,
    long SegmentCount,
    IReadOnlyList<string> UnresolvedEndpoints);

/// <summary>
/// One reviewed, source-controlled catalog specification. A package manifest is accepted only when it matches
/// the specification its catalog ID names; a manifest can never approve itself. A manifest-version-3 specification
/// also names its <see cref="Composition"/>; earlier versions have none.
/// </summary>
public sealed record ApprovedCatalogSpecification(
    string CatalogId,
    string PackageLineage,
    string PackageVersionPrefix,
    int ManifestVersion,
    string CatalogName,
    string CatalogVersion,
    ApprovedCatalogSource Source,
    string SchemaVersion,
    string PreprocessingVersion,
    string SerializerName,
    string SerializerVersion,
    ApprovedCatalogDatabase Database,
    ApprovedCatalogLicense License,
    ApprovedCatalogTopology Topology,
    ApprovedCatalogComposition? Composition = null)
{
    /// <summary>Gets the exact file names a production snapshot of this specification retains.</summary>
    public IReadOnlyList<string> RetainedFileNames =>
        ["manifest.json", Database.RelativePath, License.File.RelativePath, License.Attribution.RelativePath];

    /// <summary>Returns whether a package version is a positive revision of this specification.</summary>
    public bool IsPackageVersion(string? packageVersion)
    {
        if (packageVersion is null || !packageVersion.StartsWith(PackageVersionPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var revision = packageVersion.AsSpan(PackageVersionPrefix.Length);
        if (revision.Length == 0 || revision[0] == '0')
        {
            return false;
        }
        foreach (var character in revision)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return int.TryParse(revision, out var parsedRevision) && parsedRevision > 0;
    }
}

/// <summary>
/// The fail-closed registry of approved production catalogs, embedded at build time. Unknown catalog IDs have
/// no specification and are refused by every consumer.
/// </summary>
public static class ApprovedCatalogSpecifications
{
    /// <summary>
    /// The embedded registry version. Version 2 (issue #525) adds composed manifest-v3 specifications to the
    /// version-1 set; a CameraAgent image declares it as <c>hvo-approved-catalogs-v2</c>.
    /// </summary>
    public const int RegistryVersion = 2;

    private const string ResourceName = "HVO.SkyMonitor.Catalog.Sqlite.ApprovedCatalogSpecifications.json";
    private static readonly Lazy<IReadOnlyDictionary<string, ApprovedCatalogSpecification>> Registry =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets every approved specification ordered by catalog ID.</summary>
    public static IReadOnlyList<ApprovedCatalogSpecification> All =>
        Registry.Value.Values.OrderBy(static item => item.CatalogId, StringComparer.Ordinal).ToArray();

    /// <summary>Returns the approved specification for a catalog ID, or <see langword="false"/> when unapproved.</summary>
    public static bool TryGet(string? catalogId, [NotNullWhen(true)] out ApprovedCatalogSpecification? specification)
    {
        specification = null;
        return catalogId is not null && Registry.Value.TryGetValue(catalogId, out specification);
    }

    /// <summary>Returns the approved specification for a catalog ID or throws when it is unapproved.</summary>
    public static ApprovedCatalogSpecification Get(string catalogId)
        => TryGet(catalogId, out var specification)
            ? specification
            : throw new InvalidDataException($"Catalog '{catalogId}' is not an approved catalog specification.");

    /// <summary>Returns the approved specification whose package-version prefix owns a package version.</summary>
    public static bool TryGetByPackageVersion(
        string? packageVersion,
        [NotNullWhen(true)] out ApprovedCatalogSpecification? specification)
    {
        specification = Registry.Value.Values.SingleOrDefault(item => item.IsPackageVersion(packageVersion));
        return specification is not null;
    }

    /// <summary>Parses a registry document strictly. Exposed internally so tests can prove refusal behavior.</summary>
    internal static IReadOnlyDictionary<string, ApprovedCatalogSpecification> Parse(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        CatalogSnapshotResolver.RejectDuplicateProperties(document.RootElement);
        var root = CatalogSnapshotResolver.RequireObject(document.RootElement, "registry");
        CatalogSnapshotResolver.RequireExactProperties(root, "registry", "registryVersion", "specifications");
        if (CatalogSnapshotResolver.RequireInt32(root, "registryVersion") != RegistryVersion)
        {
            throw new InvalidDataException("Approved catalog registry version is unsupported.");
        }
        var specifications = CatalogSnapshotResolver.RequireProperty(root, "specifications");
        if (specifications.ValueKind != JsonValueKind.Array || specifications.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Approved catalog registry must contain at least one specification.");
        }

        var result = new Dictionary<string, ApprovedCatalogSpecification>(StringComparer.Ordinal);
        foreach (var element in specifications.EnumerateArray())
        {
            var specification = ReadSpecification(element);
            if (!result.TryAdd(specification.CatalogId, specification) ||
                result.Values.Count(item => item.PackageLineage == specification.PackageLineage) != 1 ||
                result.Values.Count(item => item.PackageVersionPrefix.StartsWith(specification.PackageVersionPrefix, StringComparison.Ordinal) ||
                    specification.PackageVersionPrefix.StartsWith(item.PackageVersionPrefix, StringComparison.Ordinal)) != 1)
            {
                throw new InvalidDataException(
                    $"Approved catalog registry repeats catalog '{specification.CatalogId}' or overlaps its lineage or versions.");
            }
        }
        foreach (var specification in result.Values)
        {
            ValidateStarsComponent(specification, result);
        }
        return result;
    }

    /// <summary>
    /// Requires a composed specification's stars component to be an approved, uncomposed package of this registry,
    /// carried byte for byte: its database evidence and upstream source are that package's.
    /// </summary>
    private static void ValidateStarsComponent(
        ApprovedCatalogSpecification specification,
        Dictionary<string, ApprovedCatalogSpecification> registry)
    {
        if (specification.Composition is not { Stars: var stars })
        {
            return;
        }
        if (!registry.TryGetValue(stars.CatalogId, out var component) || component.Composition is not null ||
            !component.IsPackageVersion(stars.PackageVersion) ||
            stars.Database != new ApprovedCatalogComponentDatabase(
                component.Database.Sha256, component.Database.Length, component.Database.RowCount) ||
            specification.Source != component.Source)
        {
            throw new InvalidDataException(
                $"Approved catalog specification '{specification.CatalogId}' names a stars component that is not an approved package of this registry.");
        }
    }

    private static IReadOnlyDictionary<string, ApprovedCatalogSpecification> LoadEmbedded()
    {
        using var stream = typeof(ApprovedCatalogSpecifications).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The approved catalog registry resource is missing.");
        return Parse(stream);
    }

    private static ApprovedCatalogSpecification ReadSpecification(JsonElement element)
    {
        var value = CatalogSnapshotResolver.RequireObject(element, "specification");
        var manifestVersion = CatalogSnapshotResolver.RequireInt32(value, "manifestVersion");
        var composed = manifestVersion == CatalogSnapshotResolver.ComposedManifestVersion;
        string[] specificationProperties =
        [
            "catalogId", "packageLineage", "packageVersionPrefix", "manifestVersion", "catalog", "source",
            "schemaVersion", "preprocessingVersion", "serializer", "database", "license", "topology"
        ];
        string[] databaseProperties = ["relativePath", "sha256", "length", "rowCount", "solCount", "requiredColumn"];
        CatalogSnapshotResolver.RequireExactProperties(value, "specification",
            composed ? [.. specificationProperties, "components", "transformations"] : specificationProperties);
        var catalog = Object(value, "catalog", "name", "version");
        var source = Object(value, "source", "projectUrl", "downloadUrl", "oid", "compressed", "decompressed");
        var serializer = Object(value, "serializer", "name", "version");
        var database = Object(value, "database", composed ? [.. databaseProperties, "deepSky"] : databaseProperties);
        var license = Object(value, "license", "identifier", "url", "file", "attribution");
        var topology = Object(value, "topology", "identity", "sha256", "constellationCount", "segmentCount",
            "unresolvedEndpoints");

        var catalogId = String(value, "catalogId");
        CatalogSnapshotResolver.ValidateCatalogId(catalogId);
        var endpoints = CatalogSnapshotResolver.RequireProperty(topology, "unresolvedEndpoints");
        if (endpoints.ValueKind != JsonValueKind.Array ||
            endpoints.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String ||
                !int.TryParse(item.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var hip) || hip <= 0))
        {
            throw new InvalidDataException("Approved catalog topology endpoints must be positive Hipparcos IDs.");
        }

        var specification = new ApprovedCatalogSpecification(
            catalogId,
            String(value, "packageLineage"),
            String(value, "packageVersionPrefix"),
            manifestVersion,
            String(catalog, "name"),
            String(catalog, "version"),
            new ApprovedCatalogSource(
                AbsoluteUri(source, "projectUrl"),
                AbsoluteUri(source, "downloadUrl"),
                Sha256(source, "oid"),
                Evidence(source, "compressed"),
                Evidence(source, "decompressed")),
            String(value, "schemaVersion"),
            String(value, "preprocessingVersion"),
            String(serializer, "name"),
            String(serializer, "version"),
            new ApprovedCatalogDatabase(
                FileName(database, "relativePath"),
                Sha256(database, "sha256"),
                Positive(database, "length"),
                Positive(database, "rowCount"),
                CatalogSnapshotResolver.RequireInt64(database, "solCount"),
                String(database, "requiredColumn")),
            new ApprovedCatalogLicense(
                String(license, "identifier"),
                AbsoluteUri(license, "url"),
                File(license, "file"),
                File(license, "attribution")),
            new ApprovedCatalogTopology(
                String(topology, "identity"),
                Sha256(topology, "sha256"),
                Positive(topology, "constellationCount"),
                Positive(topology, "segmentCount"),
                endpoints.EnumerateArray().Select(static item => item.GetString()!)
                    .Order(StringComparer.Ordinal).ToArray()),
            composed ? ApprovedCatalogComposition.Read(value, database) : null);
        var supportedLayout = specification.ManifestVersion switch
        {
            CatalogSnapshotResolver.MinimumSupportedManifestVersion => specification.SchemaVersion is "2" or "3",
            CatalogSnapshotResolver.ComposedManifestVersion => specification.SchemaVersion is "4" &&
                specification.Composition?.Stars.Database.RowCount == specification.Database.RowCount,
            _ => false
        };
        if (!supportedLayout ||
            specification.Database.SolCount != 0 ||
            !specification.PackageVersionPrefix.EndsWith("-r", StringComparison.Ordinal) ||
            specification.RetainedFileNames.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new InvalidDataException($"Approved catalog specification '{catalogId}' is not supported by this build.");
        }
        return specification;
    }

    private static JsonElement Object(JsonElement parent, string name, params string[] properties)
    {
        var value = CatalogSnapshotResolver.RequireObject(CatalogSnapshotResolver.RequireProperty(parent, name), name);
        CatalogSnapshotResolver.RequireExactProperties(value, name, properties);
        return value;
    }

    private static string String(JsonElement parent, string name) => CatalogSnapshotResolver.RequireString(parent, name);

    private static Uri AbsoluteUri(JsonElement parent, string name)
    {
        var value = String(parent, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidDataException($"Approved catalog property '{name}' must be an absolute HTTPS URL.");
    }

    private static long Positive(JsonElement parent, string name)
    {
        var value = CatalogSnapshotResolver.RequireInt64(parent, name);
        return value > 0 ? value : throw new InvalidDataException($"Approved catalog property '{name}' must be positive.");
    }

    private static string Sha256(JsonElement parent, string name)
    {
        var value = String(parent, name);
        return value.Length == 64 && value.All(static item => item is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException($"Approved catalog property '{name}' must be a lowercase SHA-256.");
    }

    private static string FileName(JsonElement parent, string name)
    {
        var value = String(parent, name);
        return value.IndexOfAny(['/', '\\']) < 0 && value is not ("." or "..")
            ? value
            : throw new InvalidDataException($"Approved catalog property '{name}' must be a plain file name.");
    }

    private static ApprovedCatalogEvidence Evidence(JsonElement parent, string name)
    {
        var value = Object(parent, name, "sha256", "length");
        return new ApprovedCatalogEvidence(Sha256(value, "sha256"), Positive(value, "length"));
    }

    private static ApprovedCatalogFile File(JsonElement parent, string name)
    {
        var value = Object(parent, name, "relativePath", "sha256", "length");
        return new ApprovedCatalogFile(FileName(value, "relativePath"), Sha256(value, "sha256"), Positive(value, "length"));
    }
}
