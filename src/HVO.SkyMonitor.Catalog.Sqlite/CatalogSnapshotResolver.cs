using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Identifies whether an installed snapshot contains production or test-fixture data.</summary>
public enum CatalogSnapshotPackageKind
{
    /// <summary>A complete production catalog snapshot.</summary>
    Production,

    /// <summary>An explicitly selected test fixture.</summary>
    Fixture
}

/// <summary>Validation requirements for resolving an installed catalog snapshot.</summary>
public sealed record CatalogSnapshotResolverOptions(string InstallRoot, string ExpectedCatalogId)
{
    /// <summary>Gets the only package kind accepted by the resolver.</summary>
    public CatalogSnapshotPackageKind ExpectedPackageKind { get; init; } = CatalogSnapshotPackageKind.Production;

    /// <summary>
    /// Gets the required catalog SQLite schema version. When <see langword="null"/>, a production snapshot must
    /// match its approved specification and a fixture must declare a supported schema/preprocessing pair.
    /// </summary>
    public string? ExpectedSchemaVersion { get; init; }

    /// <summary>
    /// Gets the required deterministic preprocessing version. When <see langword="null"/>, it is taken from the
    /// approved specification for production snapshots and from the supported pairs for fixtures.
    /// </summary>
    public string? ExpectedPreprocessingVersion { get; init; }

    /// <summary>Gets the exact immutable package version to resolve instead of the shared current pointer.</summary>
    public string? ExpectedPackageVersion { get; init; }
}

/// <summary>A resolved, validated, and fully loaded installed catalog snapshot.</summary>
public sealed record CatalogSnapshotResult(
    string SnapshotVersion,
    CatalogSnapshotPackageKind PackageKind,
    int ManifestVersion,
    string CatalogId,
    string CatalogVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string ManifestPath,
    string DatabasePath,
    string DatabaseSha256,
    long DatabaseLength,
    long RowCount,
    SqliteCelestialCatalog Catalog,
    CatalogDeepSkyCounts? DeepSkyCounts = null)
{
    /// <summary>
    /// Gets how the catalog identity was established, naming the manifest version that declared it. Health and the
    /// startup log report it so an operator can see which manifest contract an installed package uses.
    /// </summary>
    public string CatalogIdentitySource => $"explicit-manifest-v{ManifestVersion.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Resolves and validates the active immutable catalog snapshot below an installation root.</summary>
public static class CatalogSnapshotResolver
{
    /// <summary>
    /// The highest catalog manifest version this resolver accepts; it accepts every version from
    /// <see cref="MinimumSupportedManifestVersion"/> through this one. Deployment images declare the same value so an
    /// upgrade preflight can refuse a selected catalog newer than the runtime that will read it.
    /// </summary>
    public const int SupportedManifestVersion = ComposedManifestVersion;

    /// <summary>The lowest catalog manifest version this resolver accepts: the stars-only schema 2 and 3 layout.</summary>
    public const int MinimumSupportedManifestVersion = 2;

    /// <summary>
    /// The manifest version of a composed schema-4 catalog, which adds deep-sky counts, its components, and its
    /// recorded transformations to the version-2 layout.
    /// </summary>
    public const int ComposedManifestVersion = 3;
    private const int Sha256HexLength = 64;
    private const int MaximumManifestLength = 65_536;
    private const int MaximumCatalogIdLength = 32;
    private const int MaximumCatalogVersionLength = 64;
    private const uint StatxType = 0x00000001;
    private const uint StatxLinkCount = 0x00000004;
    private const uint StatxInode = 0x00000100;
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsFileFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsFileShareRead = 0x00000001;
    private const int WindowsFileStandardInformationClass = 1;
    private const int WindowsFileAttributeTagInformationClass = 9;
    private const int WindowsFileIdInformationClass = 18;

    internal static Action<string, CatalogSnapshotValidationPoint>? ValidationTestHook { get; set; }

    /// <summary>Resolves the active snapshot and loads its validated immutable catalog.</summary>
    public static CatalogSnapshotResult Resolve(CatalogSnapshotResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var installRoot = Path.GetFullPath(options.InstallRoot);
        var pointer = options.ExpectedPackageVersion is null
            ? ReadPointer(installRoot, Path.Combine(installRoot, "current"))
            : ReadExactVersion(installRoot, options.ExpectedPackageVersion);
        var snapshotVersion = pointer.SnapshotVersion;
        var snapshotDirectory = pointer.SnapshotDirectory;

        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        using var manifestFile = AuthenticateFile(manifestPath, "Catalog manifest");
        InvokeValidationTestHook(manifestPath, CatalogSnapshotValidationPoint.AfterInitialAuthentication);
        var manifest = ReadManifest(manifestFile);
        InvokeValidationTestHook(manifestPath, CatalogSnapshotValidationPoint.AfterManifestRead);
        RevalidateFile(manifestFile, "Catalog manifest");
        ValidateCatalogId(manifest.Catalog.Id);
        ValidateCatalogVersion(manifest.Catalog.Version);
        if (!string.Equals(manifest.Catalog.Id, options.ExpectedCatalogId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog identity mismatch. Expected '{options.ExpectedCatalogId}', got '{manifest.Catalog.Id}'.");
        }

        var expectedKind = PackageKindValue(options.ExpectedPackageKind);
        if (!string.Equals(manifest.Package.Kind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog package kind mismatch. Expected '{expectedKind}', got '{manifest.Package.Kind}'.");
        }

        if (!string.Equals(manifest.Package.Version, snapshotVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog package version '{manifest.Package.Version}' does not match current pointer '{snapshotVersion}'.");
        }

        ApprovedCatalogSpecification? specification = null;
        if (options.ExpectedPackageKind == CatalogSnapshotPackageKind.Production &&
            !ApprovedCatalogSpecifications.TryGet(manifest.Catalog.Id, out specification))
        {
            throw new InvalidDataException(
                $"Catalog '{manifest.Catalog.Id}' is not an approved production catalog specification.");
        }
        ValidateVersion("schema", options.ExpectedSchemaVersion ?? specification?.SchemaVersion, manifest.SchemaVersion);
        ValidateVersion("preprocessing", options.ExpectedPreprocessingVersion ?? specification?.PreprocessingVersion,
            manifest.PreprocessingVersion);
        if (specification is not null)
        {
            ValidateProductionManifest(manifest, specification);
            ValidateProductionRetainedFiles(snapshotDirectory, manifest, specification);
        }
        else
        {
            ValidateFixtureRetainedFiles(snapshotDirectory, FixtureDatabaseFile(manifest));
        }
        ValidateSha256(manifest.Database.Sha256, "database.sha256");
        if (manifest.Database.Length <= 0)
        {
            throw new InvalidDataException("Catalog manifest database.length must be positive.");
        }
        if (manifest.Database.RowCount <= 0)
        {
            throw new InvalidDataException("Catalog manifest database.rowCount must be positive.");
        }

        var databasePath = GetContainedPath(snapshotDirectory, manifest.Database.RelativePath,
            "Catalog database relative path");
        EnsureParentDirectoriesAreNotLinks(snapshotDirectory, databasePath, "Catalog database relative path");
        using var databaseFile = AuthenticateFile(databasePath, "Catalog database");
        InvokeValidationTestHook(databasePath, CatalogSnapshotValidationPoint.AfterInitialAuthentication);
        if (databaseFile.Stream.Length != manifest.Database.Length)
        {
            throw new InvalidDataException(
                $"Catalog database length mismatch. Expected {manifest.Database.Length}, got {databaseFile.Stream.Length}.");
        }

        var actualSha256 = ComputeSha256(databaseFile.Stream);
        RevalidateFile(databaseFile, "Catalog database");
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256), Convert.FromHexString(manifest.Database.Sha256)))
        {
            throw new InvalidDataException(
                $"Catalog database SHA-256 mismatch. Expected {manifest.Database.Sha256.ToUpperInvariant()}, got {actualSha256}.");
        }

        var catalog = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            databasePath,
            actualSha256,
            manifest.SchemaVersion,
            manifest.PreprocessingVersion,
            manifest.Database.RowCount,
            manifest.Catalog.Version,
            manifest.DeepSkyCounts), databaseFile, manifest.Database.Length,
            new AstrometricCatalogProvenance(manifest.Catalog.Id, snapshotVersion,
                PackageKindValue(options.ExpectedPackageKind), manifest.PreprocessingVersion));
        if (!string.Equals(catalog.Metadata.Name, manifest.Catalog.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog name mismatch. Expected '{manifest.Catalog.Name}', got '{catalog.Metadata.Name}'.");
        }
        if (specification is not null)
        {
            ValidateTopologyEndpoints(catalog, manifest.Topology!, specification.Topology);
        }

        return new CatalogSnapshotResult(
            snapshotVersion,
            options.ExpectedPackageKind,
            manifest.ManifestVersion,
            manifest.Catalog.Id,
            manifest.Catalog.Version,
            manifest.SchemaVersion,
            manifest.PreprocessingVersion,
            manifestPath,
            databasePath,
            actualSha256,
            manifest.Database.Length,
            manifest.Database.RowCount,
            catalog,
            manifest.DeepSkyCounts);
    }

    private static void ValidateTopologyEndpoints(
        SqliteCelestialCatalog catalog,
        SnapshotTopology expected,
        ApprovedCatalogTopology approved)
    {
        var topology = StandardConstellationTopology.CreateD3Celestial() as InMemoryConstellationTopology
            ?? throw new InvalidDataException("The standard constellation topology has an unsupported implementation.");
        if (!string.Equals(topology.ArtifactSha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The embedded constellation topology SHA-256 does not match the production manifest.");
        }
        var segments = topology.AllSegments;
        if (segments.Count != expected.SegmentCount ||
            segments.Select(static segment => segment.ConstellationId).Distinct(StringComparer.Ordinal).LongCount() !=
            expected.ConstellationCount)
        {
            throw new InvalidDataException("The embedded constellation topology does not match the production manifest.");
        }

        var endpointIds = segments
            .SelectMany(static segment => new[] { segment.FromHipparcosId, segment.ToHipparcosId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var resolved = catalog.GetByHipparcosIdsAsync(endpointIds).AsTask().GetAwaiter().GetResult();
        var unresolved = endpointIds
            .Except(resolved.Select(static item => item.HipparcosId!), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!unresolved.SequenceEqual(approved.UnresolvedEndpoints, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog constellation topology endpoint mismatch. Expected only pinned missing HIP {string.Join(',', approved.UnresolvedEndpoints)}; got '{string.Join(',', unresolved)}'.");
        }
    }

    private static void ValidateProductionManifest(SnapshotManifest manifest, ApprovedCatalogSpecification expected)
    {
        if (!expected.IsPackageVersion(manifest.Package.Version))
        {
            throw new InvalidDataException("Catalog production package version is invalid.");
        }

        ValidateConstant("manifestVersion", expected.ManifestVersion, manifest.ManifestVersion);
        ValidateConstant("catalog.id", expected.CatalogId, manifest.Catalog.Id);
        ValidateConstant("catalog.name", expected.CatalogName, manifest.Catalog.Name);
        ValidateConstant("catalog.version", expected.CatalogVersion, manifest.Catalog.Version);
        ValidateConstant("schemaVersion", expected.SchemaVersion, manifest.SchemaVersion);
        ValidateConstant("preprocessingVersion", expected.PreprocessingVersion, manifest.PreprocessingVersion);
        ValidateConstant("source.projectUrl", expected.Source.ProjectUrl.OriginalString, manifest.Source!.ProjectUrl);
        ValidateConstant("source.downloadUrl", expected.Source.DownloadUrl.OriginalString, manifest.Source.DownloadUrl);
        ValidateConstant("source.oid", expected.Source.Oid, manifest.Source.Oid);
        ValidateConstant("source.compressed.sha256", expected.Source.Compressed.Sha256, manifest.Source.Compressed.Sha256);
        ValidateConstant("source.compressed.length", expected.Source.Compressed.Length, manifest.Source.Compressed.Length);
        ValidateConstant("source.decompressed.sha256", expected.Source.Decompressed.Sha256,
            manifest.Source.Decompressed.Sha256);
        ValidateConstant("source.decompressed.length", expected.Source.Decompressed.Length,
            manifest.Source.Decompressed.Length);
        ValidateConstant("serializer.name", expected.SerializerName, manifest.Serializer!.Name);
        ValidateConstant("serializer.version", expected.SerializerVersion, manifest.Serializer.Version);
        ValidateConstant("database.relativePath", expected.Database.RelativePath, manifest.Database.RelativePath);
        ValidateConstant("database.sha256", expected.Database.Sha256, manifest.Database.Sha256);
        ValidateConstant("database.length", expected.Database.Length, manifest.Database.Length);
        ValidateConstant("database.rowCount", expected.Database.RowCount, manifest.Database.RowCount);
        ValidateConstant("database.solCount", expected.Database.SolCount, manifest.Database.SolCount!.Value);
        ValidateConstant("database.requiredColumn", expected.Database.RequiredColumn, manifest.Database.RequiredColumn!);
        ValidateConstant("license.identifier", expected.License.Identifier, manifest.License!.Identifier);
        ValidateConstant("license.url", expected.License.Url.OriginalString, manifest.License.Url);
        ValidateFileConstant("license.file", manifest.License.File, expected.License.File);
        ValidateFileConstant("license.attribution", manifest.License.Attribution, expected.License.Attribution);
        ValidateConstant("topology.identity", expected.Topology.Identity, manifest.Topology!.Identity);
        ValidateConstant("topology.sha256", expected.Topology.Sha256, manifest.Topology.Sha256);
        ValidateConstant("topology.constellationCount", expected.Topology.ConstellationCount,
            manifest.Topology.ConstellationCount);
        ValidateConstant("topology.segmentCount", expected.Topology.SegmentCount, manifest.Topology.SegmentCount);
        // The manifest versions match, and exactly the version-3 specifications and manifests have a composition.
        if (expected.Composition is { } composition)
        {
            composition.RequireSameAs(manifest.Composition!);
        }
    }

    private static void ValidateProductionRetainedFiles(
        string snapshotDirectory,
        SnapshotManifest manifest,
        ApprovedCatalogSpecification specification)
    {
        var expectedNames = specification.RetainedFileNames.ToHashSet(StringComparer.Ordinal);
        var actualNames = Directory.EnumerateFileSystemEntries(snapshotDirectory)
            .Select(static path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidDataException("Catalog production snapshot must contain exactly its four retained files.");
        }

        using var license = AuthenticateRetainedFile(snapshotDirectory, manifest.License!.File, "Catalog license");
        using var attribution = AuthenticateRetainedFile(
            snapshotDirectory,
            manifest.License.Attribution,
            "Catalog attribution");
        ValidateRetainedFile(license, manifest.License.File, "Catalog license");
        ValidateRetainedFile(attribution, manifest.License.Attribution, "Catalog attribution");
    }

    /// <summary>
    /// The retained database file name of a supported fixture format. A fixture keeps the database file name of the
    /// production schema/preprocessing pair it imitates, so each pair has exactly one expected file set.
    /// </summary>
    private static string FixtureDatabaseFile(SnapshotManifest manifest)
        => (manifest.ManifestVersion, manifest.SchemaVersion, manifest.PreprocessingVersion) switch
        {
            (MinimumSupportedManifestVersion, "2", "3") => "hyg_v42.sqlite",
            (MinimumSupportedManifestVersion, "3", "4") => "hyg_v44.sqlite",
            (ComposedManifestVersion, "4", "5") => "hyg_v44_openngc.sqlite",
            _ => throw new InvalidDataException(
                "Catalog fixture must declare a supported manifest, schema, and preprocessing combination.")
        };

    private static void ValidateFixtureRetainedFiles(string snapshotDirectory, string databaseFile)
    {
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "manifest.json",
            databaseFile
        };
        var actualNames = Directory.EnumerateFileSystemEntries(snapshotDirectory)
            .Select(static path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidDataException(
                "Catalog fixture snapshot must contain exactly its manifest and database files.");
        }
    }

    private static void ValidateOptions(CatalogSnapshotResolverOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstallRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedCatalogId);
        if (options.ExpectedSchemaVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedSchemaVersion);
        }
        if (options.ExpectedPreprocessingVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedPreprocessingVersion);
        }
        ValidateCatalogId(options.ExpectedCatalogId);
        if (options.ExpectedPackageVersion is { } packageVersion && !IsValidVersion(packageVersion))
        {
            throw new ArgumentException("Expected package version is invalid.", nameof(options));
        }
        if (!Enum.IsDefined(options.ExpectedPackageKind))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static SnapshotPointer ReadPointer(string installRoot, string pointerPath)
    {
        var pointerInfo = new DirectoryInfo(pointerPath);
        var target = pointerInfo.LinkTarget;
        if (target is null)
        {
            if (!pointerInfo.Exists && !File.Exists(pointerPath))
            {
                throw new FileNotFoundException("Catalog current pointer is missing.", pointerPath);
            }
            throw new InvalidDataException("Catalog current pointer must be a symbolic link.");
        }

        var segments = target.Split('/', StringSplitOptions.None);
        if (Path.IsPathRooted(target) || target.Any(static value => value == '\\') || segments.Length != 2 ||
            !string.Equals(segments[0], "versions", StringComparison.Ordinal) || !IsValidVersion(segments[1]))
        {
            throw new InvalidDataException("Catalog current pointer must target 'versions/PACKAGE_VERSION'.");
        }

        var versionsDirectory = Path.Combine(installRoot, "versions");
        EnsureDirectoryIsNotLink(versionsDirectory, "Catalog versions directory");
        var snapshotDirectory = GetContainedPath(installRoot, target, "Catalog current pointer");
        EnsureDirectoryIsNotLink(snapshotDirectory, "Catalog snapshot directory");
        return new SnapshotPointer(segments[1], snapshotDirectory);
    }

    private static SnapshotPointer ReadExactVersion(string installRoot, string packageVersion)
    {
        var versionsDirectory = Path.Combine(installRoot, "versions");
        EnsureDirectoryIsNotLink(versionsDirectory, "Catalog versions directory");
        var snapshotDirectory = GetContainedPath(
            installRoot,
            $"versions/{packageVersion}",
            "Catalog package version");
        EnsureDirectoryIsNotLink(snapshotDirectory, "Catalog snapshot directory");
        return new SnapshotPointer(packageVersion, snapshotDirectory);
    }

    private static SnapshotManifest ReadManifest(AuthenticatedFile file)
    {
        if (file.Stream.Length > MaximumManifestLength)
        {
            throw new InvalidDataException($"Catalog manifest exceeds the {MaximumManifestLength}-byte limit.");
        }
        try
        {
            file.Stream.Position = 0;
            using var document = JsonDocument.Parse(file.Stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            RejectDuplicateProperties(document.RootElement);
            var root = RequireObject(document.RootElement, "manifest");
            var manifestVersion = RequireInt32(root, "manifestVersion");
            if (manifestVersion is < MinimumSupportedManifestVersion or > SupportedManifestVersion)
            {
                throw new InvalidDataException(
                    $"Catalog manifest version {manifestVersion} is unsupported. This runtime reads versions {MinimumSupportedManifestVersion} through {SupportedManifestVersion}.");
            }
            var package = RequireObject(RequireProperty(root, "package"), "package");
            RequireExactProperties(package, "package", "kind", "version");
            var packageKind = RequireString(package, "kind");
            return packageKind switch
            {
                "production" => ReadProductionManifest(root, package, manifestVersion),
                "fixture" => ReadFixtureManifest(root, package, manifestVersion),
                _ => throw new InvalidDataException($"Catalog manifest package kind '{packageKind}' is unsupported.")
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Catalog manifest is malformed JSON.", exception);
        }
    }

    private static SnapshotManifest ReadProductionManifest(JsonElement root, JsonElement package, int manifestVersion)
    {
        var composed = manifestVersion == ComposedManifestVersion;
        string[] rootProperties =
        [
            "manifestVersion", "package", "catalog", "source", "schemaVersion", "preprocessingVersion",
            "serializer", "database", "license", "topology"
        ];
        string[] databaseProperties = ["relativePath", "sha256", "length", "rowCount", "solCount", "requiredColumn"];
        RequireExactProperties(root, "manifest", composed ? [.. rootProperties, "components", "transformations"] : rootProperties);
        var catalog = RequireObject(RequireProperty(root, "catalog"), "catalog");
        var source = RequireObject(RequireProperty(root, "source"), "source");
        var compressed = RequireObject(RequireProperty(source, "compressed"), "source.compressed");
        var decompressed = RequireObject(RequireProperty(source, "decompressed"), "source.decompressed");
        var serializer = RequireObject(RequireProperty(root, "serializer"), "serializer");
        var database = RequireObject(RequireProperty(root, "database"), "database");
        var license = RequireObject(RequireProperty(root, "license"), "license");
        var licenseFile = RequireObject(RequireProperty(license, "file"), "license.file");
        var attribution = RequireObject(RequireProperty(license, "attribution"), "license.attribution");
        var topology = RequireObject(RequireProperty(root, "topology"), "topology");

        RequireExactProperties(catalog, "catalog", "id", "name", "version");
        RequireExactProperties(source, "source", "projectUrl", "downloadUrl", "oid", "compressed", "decompressed");
        RequireExactProperties(compressed, "source.compressed", "sha256", "length");
        RequireExactProperties(decompressed, "source.decompressed", "sha256", "length");
        RequireExactProperties(serializer, "serializer", "name", "version");
        RequireExactProperties(database, "database", composed ? [.. databaseProperties, "deepSky"] : databaseProperties);
        RequireExactProperties(license, "license", "identifier", "url", "file", "attribution");
        var composition = composed ? ApprovedCatalogComposition.Read(root, database) : null;
        RequireExactProperties(licenseFile, "license.file", "relativePath", "sha256", "length");
        RequireExactProperties(attribution, "license.attribution", "relativePath", "sha256", "length");
        RequireExactProperties(topology, "topology", "identity", "sha256", "constellationCount", "segmentCount");

        var manifest = new SnapshotManifest(
            manifestVersion,
            new SnapshotPackage(RequireString(package, "kind"), RequireString(package, "version")),
            new SnapshotCatalog(RequireString(catalog, "id"), RequireString(catalog, "name"), RequireString(catalog, "version")),
            RequireString(root, "schemaVersion"),
            RequireString(root, "preprocessingVersion"),
            new SnapshotDatabase(
                RequireString(database, "relativePath"),
                RequireString(database, "sha256"),
                RequireInt64(database, "length"),
                RequireInt64(database, "rowCount"),
                RequireInt64(database, "solCount"),
                RequireString(database, "requiredColumn")),
            new SnapshotSource(
                RequireString(source, "projectUrl"),
                RequireString(source, "downloadUrl"),
                RequireString(source, "oid"),
                new SnapshotEvidence(RequireString(compressed, "sha256"), RequireInt64(compressed, "length")),
                new SnapshotEvidence(RequireString(decompressed, "sha256"), RequireInt64(decompressed, "length"))),
            new SnapshotSerializer(RequireString(serializer, "name"), RequireString(serializer, "version")),
            new SnapshotLicense(
                RequireString(license, "identifier"),
                RequireString(license, "url"),
                ReadFileEvidence(licenseFile),
                ReadFileEvidence(attribution)),
            new SnapshotTopology(
                RequireString(topology, "identity"),
                RequireString(topology, "sha256"),
                RequireInt64(topology, "constellationCount"),
                RequireInt64(topology, "segmentCount")),
            composition);
        return manifest;
    }

    /// <summary>
    /// Reads a fixture manifest. A version-3 fixture adds only <c>database.deepSky</c>, so the loader can prove the
    /// fixture's deep-sky tables hold the counts it declares; it names no components or transformations.
    /// </summary>
    private static SnapshotManifest ReadFixtureManifest(JsonElement root, JsonElement package, int manifestVersion)
    {
        RequireExactProperties(root, "manifest", "manifestVersion", "package", "catalog", "schemaVersion",
            "preprocessingVersion", "database");
        var catalog = RequireObject(RequireProperty(root, "catalog"), "catalog");
        var database = RequireObject(RequireProperty(root, "database"), "database");
        RequireExactProperties(catalog, "catalog", "id", "name", "version");
        string[] databaseProperties = ["relativePath", "sha256", "length", "rowCount"];
        var composed = manifestVersion == ComposedManifestVersion;
        RequireExactProperties(database, "database", composed ? [.. databaseProperties, "deepSky"] : databaseProperties);

        var manifest = new SnapshotManifest(
            manifestVersion,
            new SnapshotPackage(RequireString(package, "kind"), RequireString(package, "version")),
            new SnapshotCatalog(RequireString(catalog, "id"), RequireString(catalog, "name"), RequireString(catalog, "version")),
            RequireString(root, "schemaVersion"),
            RequireString(root, "preprocessingVersion"),
            new SnapshotDatabase(
                RequireString(database, "relativePath"),
                RequireString(database, "sha256"),
                RequireInt64(database, "length"),
                RequireInt64(database, "rowCount"),
                null,
                null),
            null,
            null,
            null,
            null,
            null,
            composed ? ApprovedCatalogComposition.ReadDeepSkyCounts(database) : null);
        return manifest;
    }

    private static SnapshotFile ReadFileEvidence(JsonElement value)
        => new(
            RequireString(value, "relativePath"),
            RequireString(value, "sha256"),
            RequireInt64(value, "length"));

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Catalog manifest contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    internal static JsonElement RequireObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"Catalog manifest '{name}' must be an object.");

    internal static void RequireExactProperties(JsonElement element, string name, params string[] expectedNames)
    {
        var remaining = expectedNames.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException(
                    $"Catalog manifest object '{name}' contains unknown property '{property.Name}'.");
            }
        }
        if (remaining.Count != 0)
        {
            throw new InvalidDataException(
                $"Catalog manifest object '{name}' is missing required property '{remaining.OrderBy(static value => value, StringComparer.Ordinal).First()}'.");
        }
    }

    internal static JsonElement RequireProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Catalog manifest is missing required property '{name}'.");

    internal static string RequireString(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Catalog manifest property '{name}' must be a nonblank string.");
        }
        return property.GetString()!;
    }

    internal static int RequireInt32(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"Catalog manifest property '{name}' must be an integer.");
        }
        return value;
    }

    internal static long RequireInt64(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"Catalog manifest database property '{name}' must be an integer.");
        }
        return value;
    }

    private static string GetContainedPath(string root, string relativePath, string description)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Any(static value => value == '\\') ||
            segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"{description} must be a nonblank relative path.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(relativePath, fullRoot);
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{description} escapes its containing directory.");
        }
        return fullPath;
    }

    private static void EnsureParentDirectoriesAreNotLinks(string root, string path, string description)
    {
        var relativeParent = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        if (relativeParent == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relativeParent.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            EnsureDirectoryIsNotLink(current, description);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to AuthenticatedFile after identity validation.")]
    internal static AuthenticatedFile AuthenticateFile(string path, string description)
    {
        FileStream? stream = null;
        try
        {
            stream = OpenFileNoFollow(path);
            var handleIdentity = ReadHandleIdentity(stream.SafeFileHandle);
            ValidateIdentity(handleIdentity, description);
            var pathIdentity = ReadPathIdentity(path);
            ValidateIdentity(pathIdentity, description);
            if (handleIdentity != pathIdentity)
            {
                throw new InvalidDataException($"{description} path does not identify the authenticated file handle.");
            }

            var result = new AuthenticatedFile(path, stream, handleIdentity);
            return result;
        }
        catch (FileNotFoundException)
        {
            stream?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            stream?.Dispose();
            throw new InvalidDataException($"{description} identity could not be authenticated.", exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static void ValidateIdentity(FileIdentity identity, string description)
    {
        if (!identity.IsRegularFile)
        {
            throw new InvalidDataException($"{description} must be a regular file.");
        }
        if (identity.LinkCount != 1)
        {
            throw new InvalidDataException(
                $"{description} hard-link count must be exactly one; found {identity.LinkCount}.");
        }
    }

    internal static void RevalidateFile(AuthenticatedFile file, string description)
    {
        var handleIdentity = ReadHandleIdentity(file.Stream.SafeFileHandle);
        ValidateIdentity(handleIdentity, description);
        var pathIdentity = ReadPathIdentity(file.Path);
        ValidateIdentity(pathIdentity, description);
        if (handleIdentity != file.Identity || pathIdentity != file.Identity)
        {
            throw new InvalidDataException($"{description} identity changed while it was being validated.");
        }
    }

    /// <summary>
    /// Linux <c>O_NOFOLLOW</c> for <paramref name="architecture"/>: the asm-generic value on x86, loongarch,
    /// riscv, and s390, and the arm and powerpc override elsewhere. Mirrors <c>LinuxOpenFlags</c> in
    /// <c>CameraAgent.Common</c>, which this assembly does not reference.
    /// </summary>
    internal static int GetLinuxNoFollowFlag(Architecture architecture)
        => architecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => 0x8000,
            Architecture.X86 or Architecture.X64 or Architecture.LoongArch64 or Architecture.RiscV64 or Architecture.S390x => 0x20000,
            _ => throw new PlatformNotSupportedException($"Linux O_NOFOLLOW is not configured for {architecture}.")
        };

    private const int LinuxCloseOnExec = 0x80000;
    // O_NONBLOCK lets a FIFO planted under a retained name open and then fail type validation instead of blocking
    // until a writer appears; it has no effect on reads of the regular files that pass validation.
    private const int LinuxNonBlocking = 0x800;
    private static int LinuxRetainedOpenFlags =>
        GetLinuxNoFollowFlag(RuntimeInformation.ProcessArchitecture) | LinuxCloseOnExec | LinuxNonBlocking;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The returned FileStream owns the native handle.")]
    internal static FileStream OpenFileNoFollow(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            const int macOsOpenFlags = 0x01000104; // O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK
            var descriptor = Open(path, OperatingSystem.IsLinux() ? LinuxRetainedOpenFlags : macOsOpenFlags);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2)
                {
                    throw new FileNotFoundException("Catalog retained file is missing.", path);
                }
                throw new Win32Exception(error);
            }
            return new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read);
        }

        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(
                path,
                WindowsGenericRead,
                WindowsFileShareRead,
                0,
                WindowsOpenExisting,
                WindowsFileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 2 or 3)
                {
                    throw new FileNotFoundException("Catalog retained file is missing.", path);
                }
                throw new Win32Exception(error);
            }
            return new FileStream(handle, FileAccess.Read);
        }

        throw new PlatformNotSupportedException("Catalog file authentication is not supported on this platform.");
    }

    private static FileIdentity ReadPathIdentity(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            if (StatX(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymbolicLinkNoFollow,
                    StatxType | StatxLinkCount | StatxInode,
                    out var status) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if ((status.Mask & (StatxType | StatxLinkCount | StatxInode)) !=
                (StatxType | StatxLinkCount | StatxInode))
            {
                throw new InvalidDataException("The file system did not authenticate the file identity.");
            }
            return new FileIdentity(
                ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
                status.Inode,
                0,
                status.Mode & UnixFileTypeMask,
                status.LinkCount,
                (status.Mode & UnixFileTypeMask) == UnixRegularFileType);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (LStat(path, out var status) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return new FileIdentity(
                unchecked((uint)status.Device),
                status.Inode,
                0,
                (uint)status.Mode & UnixFileTypeMask,
                status.LinkCount,
                ((uint)status.Mode & UnixFileTypeMask) == UnixRegularFileType);
        }

        if (OperatingSystem.IsWindows())
        {
            using var stream = OpenFileNoFollow(path);
            return ReadWindowsHandleIdentity(stream.SafeFileHandle);
        }

        throw new PlatformNotSupportedException("Catalog file authentication is not supported on this platform.");
    }

    private static FileIdentity ReadHandleIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            if (StatX((int)handle.DangerousGetHandle(), string.Empty, AtEmptyPath,
                    StatxType | StatxLinkCount | StatxInode, out var status) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if ((status.Mask & (StatxType | StatxLinkCount | StatxInode)) !=
                (StatxType | StatxLinkCount | StatxInode))
            {
                throw new InvalidDataException("The file system did not authenticate the file identity.");
            }
            return new FileIdentity(
                ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
                status.Inode,
                0,
                status.Mode & UnixFileTypeMask,
                status.LinkCount,
                (status.Mode & UnixFileTypeMask) == UnixRegularFileType);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (FStat(handle, out var status) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return new FileIdentity(
                unchecked((uint)status.Device),
                status.Inode,
                0,
                (uint)status.Mode & UnixFileTypeMask,
                status.LinkCount,
                ((uint)status.Mode & UnixFileTypeMask) == UnixRegularFileType);
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsHandleIdentity(handle);
        }

        throw new PlatformNotSupportedException("Catalog file authentication is not supported on this platform.");
    }

    private static FileIdentity ReadWindowsHandleIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, WindowsFileIdInformationClass, out WindowsFileIdInfo fileId,
                Marshal.SizeOf<WindowsFileIdInfo>()) ||
            !GetFileInformationByHandleEx(handle, WindowsFileStandardInformationClass, out WindowsFileStandardInfo standard,
                Marshal.SizeOf<WindowsFileStandardInfo>()) ||
            !GetFileInformationByHandleEx(handle, WindowsFileAttributeTagInformationClass,
                out WindowsFileAttributeTagInfo attributes, Marshal.SizeOf<WindowsFileAttributeTagInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new FileIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh,
            attributes.FileAttributes,
            standard.NumberOfLinks,
            standard.Directory == 0 && (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0);
    }

    private static void EnsureDirectoryIsNotLink(string path, string description)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException($"{description} is missing: '{path}'.");
        }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
        }
    }

    private static void ValidateVersion(string name, string? expected, string actual)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Catalog manifest {name} version mismatch. Expected '{expected}', got '{actual}'.");
        }
    }

    private static void ValidateCatalogVersion(string value)
    {
        if (value.Length > MaximumCatalogVersionLength || !IsValidVersion(value))
        {
            throw new InvalidDataException(
                $"Catalog manifest catalog.version must contain at most {MaximumCatalogVersionLength} safe version characters.");
        }
    }

    private static void ValidateConstant(string name, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog production manifest {name} mismatch. Expected '{expected}', got '{actual}'.");
        }
    }

    private static void ValidateConstant(string name, long expected, long actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException(
                $"Catalog production manifest {name} mismatch. Expected {expected}, got {actual}.");
        }
    }

    private static void ValidateFileConstant(string name, SnapshotFile actual, ApprovedCatalogFile expected)
    {
        ValidateConstant($"{name}.relativePath", expected.RelativePath, actual.RelativePath);
        ValidateConstant($"{name}.sha256", expected.Sha256, actual.Sha256);
        ValidateConstant($"{name}.length", expected.Length, actual.Length);
    }

    private static AuthenticatedFile AuthenticateRetainedFile(
        string snapshotDirectory,
        SnapshotFile evidence,
        string description)
    {
        var path = GetContainedPath(snapshotDirectory, evidence.RelativePath, $"{description} relative path");
        EnsureParentDirectoriesAreNotLinks(snapshotDirectory, path, $"{description} relative path");
        var file = AuthenticateFile(path, description);
        InvokeValidationTestHook(path, CatalogSnapshotValidationPoint.AfterInitialAuthentication);
        return file;
    }

    private static void ValidateRetainedFile(
        AuthenticatedFile file,
        SnapshotFile evidence,
        string description)
    {
        ValidateSha256(evidence.Sha256, $"{description}.sha256");
        if (file.Stream.Length != evidence.Length)
        {
            throw new InvalidDataException(
                $"{description} length mismatch. Expected {evidence.Length}, got {file.Stream.Length}.");
        }

        var actualSha256 = ComputeSha256(file.Stream);
        RevalidateFile(file, description);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256), Convert.FromHexString(evidence.Sha256)))
        {
            throw new InvalidDataException(
                $"{description} SHA-256 mismatch. Expected {evidence.Sha256.ToUpperInvariant()}, got {actualSha256}.");
        }
    }

    private static void ValidateSha256(string value, string propertyName)
    {
        if (value.Length != Sha256HexLength || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"Catalog manifest property '{propertyName}' must contain exactly 64 hexadecimal characters.");
        }
    }

    private static string ComputeSha256(FileStream source)
    {
        source.Position = 0;
        var result = Convert.ToHexString(SHA256.HashData(source));
        source.Position = 0;
        return result;
    }

    internal static void InvokeValidationTestHook(string path, CatalogSnapshotValidationPoint point)
        => ValidationTestHook?.Invoke(path, point);

    private static string PackageKindValue(CatalogSnapshotPackageKind value)
        => value switch
        {
            CatalogSnapshotPackageKind.Production => "production",
            CatalogSnapshotPackageKind.Fixture => "fixture",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static bool IsVersionCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '+' or '-';

    private static bool IsValidVersion(string value)
        => !string.IsNullOrEmpty(value) && char.IsAsciiLetterOrDigit(value[0]) && value.All(IsVersionCharacter);

    private sealed record SnapshotManifest(
        int ManifestVersion,
        SnapshotPackage Package,
        SnapshotCatalog Catalog,
        string SchemaVersion,
        string PreprocessingVersion,
        SnapshotDatabase Database,
        SnapshotSource? Source,
        SnapshotSerializer? Serializer,
        SnapshotLicense? License,
        SnapshotTopology? Topology,
        ApprovedCatalogComposition? Composition = null,
        CatalogDeepSkyCounts? FixtureDeepSkyCounts = null)
    {
        /// <summary>Gets the deep-sky counts a composed manifest declares, or <see langword="null"/>.</summary>
        public CatalogDeepSkyCounts? DeepSkyCounts => Composition?.DeepSkyCounts ?? FixtureDeepSkyCounts;
    }

    private sealed record SnapshotPackage(string Kind, string Version);

    internal static void ValidateCatalogId(string value)
    {
        if (value.Length > MaximumCatalogIdLength ||
            !char.IsAsciiLetterOrDigit(value[0]) || char.IsAsciiLetterUpper(value[0]) ||
            value.Any(static character => character != '-' &&
                (!char.IsAsciiLetterOrDigit(character) || char.IsAsciiLetterUpper(character))))
        {
            throw new InvalidDataException($"Catalog ID '{value}' is invalid.");
        }
    }

    private sealed record SnapshotCatalog(string Id, string Name, string Version);

    private sealed record SnapshotDatabase(
        string RelativePath,
        string Sha256,
        long Length,
        long RowCount,
        long? SolCount,
        string? RequiredColumn);

    private sealed record SnapshotSource(
        string ProjectUrl,
        string DownloadUrl,
        string Oid,
        SnapshotEvidence Compressed,
        SnapshotEvidence Decompressed);

    private sealed record SnapshotEvidence(string Sha256, long Length);

    private sealed record SnapshotSerializer(string Name, string Version);

    private sealed record SnapshotLicense(
        string Identifier,
        string Url,
        SnapshotFile File,
        SnapshotFile Attribution);

    private sealed record SnapshotFile(string RelativePath, string Sha256, long Length);

    private sealed record SnapshotTopology(string Identity, string Sha256, long ConstellationCount, long SegmentCount);

    private sealed record SnapshotPointer(string SnapshotVersion, string SnapshotDirectory);

    internal readonly record struct FileIdentity(
        ulong Device,
        ulong FileIdLow,
        ulong FileIdHigh,
        uint Type,
        uint LinkCount,
        bool IsRegularFile);

    internal sealed class AuthenticatedFile(string path, FileStream stream, FileIdentity identity) : IDisposable
    {
        internal string Path { get; } = path;

        internal FileStream Stream { get; } = stream;

        internal FileIdentity Identity { get; } = identity;

        public void Dispose() => Stream.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(16)]
        internal uint LinkCount;

        [FieldOffset(28)]
        internal ushort Mode;

        [FieldOffset(32)]
        internal ulong Inode;

        [FieldOffset(136)]
        internal uint DeviceMajor;

        [FieldOffset(140)]
        internal uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacOsFileStatus
    {
        [FieldOffset(0)]
        internal int Device;

        [FieldOffset(4)]
        internal ushort Mode;

        [FieldOffset(6)]
        internal ushort LinkCount;

        [FieldOffset(8)]
        internal ulong Inode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct WindowsFileIdInfo
    {
        [FieldOffset(0)] internal ulong VolumeSerialNumber;
        [FieldOffset(8)] internal ulong FileIdLow;
        [FieldOffset(16)] internal ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct WindowsFileStandardInfo
    {
        [FieldOffset(16)]
        internal uint NumberOfLinks;

        [FieldOffset(21)]
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

#pragma warning disable SYSLIB1054 // These narrow Unix calls avoid enabling unsafe code for source-generated interop.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int StatX(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int LStat(string path, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out WindowsFileIdInfo fileInformation,
        int bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out WindowsFileStandardInfo fileInformation,
        int bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out WindowsFileAttributeTagInfo fileInformation,
        int bufferSize);
#pragma warning restore SYSLIB1054
}

internal enum CatalogSnapshotValidationPoint
{
    AfterInitialAuthentication,
    AfterManifestRead,
    BeforeSqliteOpen,
    AfterSqliteLoad
}
