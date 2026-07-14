using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

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
public sealed record CatalogSnapshotResolverOptions(string InstallRoot)
{
    /// <summary>Gets the only package kind accepted by the resolver.</summary>
    public CatalogSnapshotPackageKind ExpectedPackageKind { get; init; } = CatalogSnapshotPackageKind.Production;

    /// <summary>Gets the supported snapshot manifest format version.</summary>
    public int ExpectedManifestVersion { get; init; } = 1;

    /// <summary>Gets the required catalog SQLite schema version.</summary>
    public string ExpectedSchemaVersion { get; init; } = "2";

    /// <summary>Gets the required deterministic preprocessing version.</summary>
    public string ExpectedPreprocessingVersion { get; init; } = "3";
}

/// <summary>A resolved, validated, and fully loaded installed catalog snapshot.</summary>
public sealed record CatalogSnapshotResult(
    string SnapshotVersion,
    CatalogSnapshotPackageKind PackageKind,
    int ManifestVersion,
    string CatalogVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string ManifestPath,
    string DatabasePath,
    string DatabaseSha256,
    long DatabaseLength,
    long RowCount,
    SqliteCelestialCatalog Catalog);

/// <summary>Resolves and validates the active immutable catalog snapshot below an installation root.</summary>
public static class CatalogSnapshotResolver
{
    private const int Sha256HexLength = 64;
    private const int MaximumManifestLength = 65_536;
    private const int MaximumCatalogVersionLength = 64;
    private const string ProductionCatalogName = "HYG 4.2";
    private const string ProductionCatalogVersion = "4.2";
    private const string ProductionSourceProjectUrl = "https://codeberg.org/astronexus/hyg";
    private const string ProductionSourceOid = "5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601";
    private const string ProductionSourceDownloadUrl =
        "https://codeberg.org/astronexus/hyg.git/info/lfs/objects/5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601";
    private const string ProductionDecompressedSha256 =
        "b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2";
    private const string ProductionDatabaseSha256 =
        "b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2";
    private const string ProductionLicenseSha256 =
        "9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05";
    private const string ProductionAttributionSha256 =
        "e3addc3480a0d0f07129f332b0dea592fa315373f21111d54ac8e2aebd03b5f1";
    private const string ProductionTopologySha256 =
        "70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621";

    /// <summary>Resolves the active snapshot and loads its validated immutable catalog.</summary>
    public static CatalogSnapshotResult Resolve(CatalogSnapshotResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var installRoot = Path.GetFullPath(options.InstallRoot);
        var pointerPath = Path.Combine(installRoot, "current");
        var pointer = ReadPointer(installRoot, pointerPath);
        var snapshotVersion = pointer.SnapshotVersion;
        var snapshotDirectory = pointer.SnapshotDirectory;

        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        EnsureFileIsNotLink(manifestPath, "Catalog manifest");
        var manifest = ReadManifest(manifestPath);
        ValidateCatalogVersion(manifest.Catalog.Version);

        if (manifest.ManifestVersion != options.ExpectedManifestVersion)
        {
            throw new InvalidDataException(
                $"Catalog manifest version mismatch. Expected {options.ExpectedManifestVersion}, got {manifest.ManifestVersion}.");
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

        ValidateVersion("schema", options.ExpectedSchemaVersion, manifest.SchemaVersion);
        ValidateVersion("preprocessing", options.ExpectedPreprocessingVersion, manifest.PreprocessingVersion);
        if (options.ExpectedPackageKind == CatalogSnapshotPackageKind.Production)
        {
            ValidateProductionManifest(manifest);
            ValidateProductionRetainedFiles(snapshotDirectory, manifest);
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
        EnsureFileIsNotLink(databasePath, "Catalog database");
        var databaseInfo = new FileInfo(databasePath);
        if (databaseInfo.Length != manifest.Database.Length)
        {
            throw new InvalidDataException(
                $"Catalog database length mismatch. Expected {manifest.Database.Length}, got {databaseInfo.Length}.");
        }

        var actualSha256 = ComputeSha256(databasePath);
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
            manifest.Catalog.Version));
        if (!string.Equals(catalog.Metadata.Name, manifest.Catalog.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog name mismatch. Expected '{manifest.Catalog.Name}', got '{catalog.Metadata.Name}'.");
        }
        if (options.ExpectedPackageKind == CatalogSnapshotPackageKind.Production)
        {
            ValidateTopologyEndpoints(catalog, manifest.Topology!);
        }

        return new CatalogSnapshotResult(
            snapshotVersion,
            options.ExpectedPackageKind,
            manifest.ManifestVersion,
            manifest.Catalog.Version,
            manifest.SchemaVersion,
            manifest.PreprocessingVersion,
            manifestPath,
            databasePath,
            actualSha256,
            manifest.Database.Length,
            manifest.Database.RowCount,
            catalog);
    }

    private static void ValidateTopologyEndpoints(SqliteCelestialCatalog catalog, SnapshotTopology expected)
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
            .ToArray();
        if (!unresolved.SequenceEqual(["55203"], StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog constellation topology endpoint mismatch. Expected only pinned missing HIP 55203; got '{string.Join(',', unresolved)}'.");
        }
    }

    private static void ValidateProductionManifest(SnapshotManifest manifest)
    {
        if (!IsProductionPackageVersion(manifest.Package.Version))
        {
            throw new InvalidDataException("Catalog production package version is invalid.");
        }

        ValidateConstant("manifestVersion", 1, manifest.ManifestVersion);
        ValidateConstant("catalog.name", ProductionCatalogName, manifest.Catalog.Name);
        ValidateConstant("catalog.version", ProductionCatalogVersion, manifest.Catalog.Version);
        ValidateConstant("schemaVersion", "2", manifest.SchemaVersion);
        ValidateConstant("preprocessingVersion", "3", manifest.PreprocessingVersion);
        ValidateConstant("source.projectUrl", ProductionSourceProjectUrl, manifest.Source!.ProjectUrl);
        ValidateConstant("source.downloadUrl", ProductionSourceDownloadUrl, manifest.Source.DownloadUrl);
        ValidateConstant("source.oid", ProductionSourceOid, manifest.Source.Oid);
        ValidateConstant("source.compressed.sha256", ProductionSourceOid, manifest.Source.Compressed.Sha256);
        ValidateConstant("source.compressed.length", 13_636_976, manifest.Source.Compressed.Length);
        ValidateConstant("source.decompressed.sha256", ProductionDecompressedSha256,
            manifest.Source.Decompressed.Sha256);
        ValidateConstant("source.decompressed.length", 33_932_800, manifest.Source.Decompressed.Length);
        ValidateConstant("serializer.name", "sqlite3", manifest.Serializer!.Name);
        ValidateConstant("serializer.version", "3.45.1", manifest.Serializer.Version);
        ValidateConstant("database.relativePath", "hyg_v42.sqlite", manifest.Database.RelativePath);
        ValidateConstant("database.sha256", ProductionDatabaseSha256, manifest.Database.Sha256);
        ValidateConstant("database.length", 9_302_016, manifest.Database.Length);
        ValidateConstant("database.rowCount", 119_625, manifest.Database.RowCount);
        ValidateConstant("database.solCount", 0, manifest.Database.SolCount!.Value);
        ValidateConstant("database.requiredColumn", "hipparcos_id", manifest.Database.RequiredColumn!);
        ValidateConstant("license.identifier", "CC BY-SA 4.0", manifest.License!.Identifier);
        ValidateConstant("license.url", "https://creativecommons.org/licenses/by-sa/4.0/", manifest.License.Url);
        ValidateFileConstant("license.file", manifest.License.File, "LICENSE-HYG.md", ProductionLicenseSha256, 423);
        ValidateFileConstant("license.attribution", manifest.License.Attribution, "ATTRIBUTION-HYG.md",
            ProductionAttributionSha256, 1_361);
        ValidateConstant("topology.identity", "d3-celestial-v0.7.32-hip-coordinate-map-v1",
            manifest.Topology!.Identity);
        ValidateConstant("topology.sha256", ProductionTopologySha256, manifest.Topology.Sha256);
        ValidateConstant("topology.constellationCount", 88, manifest.Topology.ConstellationCount);
        ValidateConstant("topology.segmentCount", 743, manifest.Topology.SegmentCount);
    }

    private static void ValidateProductionRetainedFiles(string snapshotDirectory, SnapshotManifest manifest)
    {
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "manifest.json",
            manifest.Database.RelativePath,
            manifest.License!.File.RelativePath,
            manifest.License.Attribution.RelativePath
        };
        var actualNames = Directory.EnumerateFileSystemEntries(snapshotDirectory)
            .Select(static path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidDataException("Catalog production snapshot must contain exactly its four retained files.");
        }

        ValidateRetainedFile(snapshotDirectory, manifest.License.File, "Catalog license");
        ValidateRetainedFile(snapshotDirectory, manifest.License.Attribution, "Catalog attribution");
    }

    private static void ValidateOptions(CatalogSnapshotResolverOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstallRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedPreprocessingVersion);
        if (options.ExpectedManifestVersion <= 0 || !Enum.IsDefined(options.ExpectedPackageKind))
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

    private static SnapshotManifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > MaximumManifestLength)
        {
            throw new InvalidDataException($"Catalog manifest exceeds the {MaximumManifestLength}-byte limit.");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            RejectDuplicateProperties(document.RootElement);
            var root = RequireObject(document.RootElement, "manifest");
            var package = RequireObject(RequireProperty(root, "package"), "package");
            RequireExactProperties(package, "package", "kind", "version");
            var packageKind = RequireString(package, "kind");
            return packageKind switch
            {
                "production" => ReadProductionManifest(root, package),
                "fixture" => ReadFixtureManifest(root, package),
                _ => throw new InvalidDataException($"Catalog manifest package kind '{packageKind}' is unsupported.")
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Catalog manifest is malformed JSON.", exception);
        }
    }

    private static SnapshotManifest ReadProductionManifest(JsonElement root, JsonElement package)
    {
        RequireExactProperties(root, "manifest",
            "manifestVersion", "package", "catalog", "source", "schemaVersion", "preprocessingVersion",
            "serializer", "database", "license", "topology");
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

        RequireExactProperties(catalog, "catalog", "name", "version");
        RequireExactProperties(source, "source", "projectUrl", "downloadUrl", "oid", "compressed", "decompressed");
        RequireExactProperties(compressed, "source.compressed", "sha256", "length");
        RequireExactProperties(decompressed, "source.decompressed", "sha256", "length");
        RequireExactProperties(serializer, "serializer", "name", "version");
        RequireExactProperties(database, "database", "relativePath", "sha256", "length", "rowCount", "solCount",
            "requiredColumn");
        RequireExactProperties(license, "license", "identifier", "url", "file", "attribution");
        RequireExactProperties(licenseFile, "license.file", "relativePath", "sha256", "length");
        RequireExactProperties(attribution, "license.attribution", "relativePath", "sha256", "length");
        RequireExactProperties(topology, "topology", "identity", "sha256", "constellationCount", "segmentCount");

        return new SnapshotManifest(
            RequireInt32(root, "manifestVersion"),
            new SnapshotPackage(RequireString(package, "kind"), RequireString(package, "version")),
            new SnapshotCatalog(RequireString(catalog, "name"), RequireString(catalog, "version")),
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
                RequireInt64(topology, "segmentCount")));
    }

    private static SnapshotManifest ReadFixtureManifest(JsonElement root, JsonElement package)
    {
        RequireExactProperties(root, "manifest", "manifestVersion", "package", "catalog", "schemaVersion",
            "preprocessingVersion", "database");
        var catalog = RequireObject(RequireProperty(root, "catalog"), "catalog");
        var database = RequireObject(RequireProperty(root, "database"), "database");
        RequireExactProperties(catalog, "catalog", "name", "version");
        RequireExactProperties(database, "database", "relativePath", "sha256", "length", "rowCount");

        return new SnapshotManifest(
            RequireInt32(root, "manifestVersion"),
            new SnapshotPackage(RequireString(package, "kind"), RequireString(package, "version")),
            new SnapshotCatalog(RequireString(catalog, "name"), RequireString(catalog, "version")),
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
            null);
    }

    private static SnapshotFile ReadFileEvidence(JsonElement value)
        => new(
            RequireString(value, "relativePath"),
            RequireString(value, "sha256"),
            RequireInt64(value, "length"));

    private static void RejectDuplicateProperties(JsonElement element)
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

    private static JsonElement RequireObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"Catalog manifest '{name}' must be an object.");

    private static void RequireExactProperties(JsonElement element, string name, params string[] expectedNames)
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

    private static JsonElement RequireProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Catalog manifest is missing required property '{name}'.");

    private static string RequireString(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Catalog manifest property '{name}' must be a nonblank string.");
        }
        return property.GetString()!;
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        var property = RequireProperty(element, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"Catalog manifest property '{name}' must be an integer.");
        }
        return value;
    }

    private static long RequireInt64(JsonElement element, string name)
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

    private static void EnsureFileIsNotLink(string path, string description)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"{description} is missing.", path);
        }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
        }
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

    private static void ValidateVersion(string name, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
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

    private static void ValidateFileConstant(
        string name,
        SnapshotFile actual,
        string expectedPath,
        string expectedSha256,
        long expectedLength)
    {
        ValidateConstant($"{name}.relativePath", expectedPath, actual.RelativePath);
        ValidateConstant($"{name}.sha256", expectedSha256, actual.Sha256);
        ValidateConstant($"{name}.length", expectedLength, actual.Length);
    }

    private static void ValidateRetainedFile(string snapshotDirectory, SnapshotFile evidence, string description)
    {
        ValidateSha256(evidence.Sha256, $"{description}.sha256");
        var path = GetContainedPath(snapshotDirectory, evidence.RelativePath, $"{description} relative path");
        EnsureParentDirectoriesAreNotLinks(snapshotDirectory, path, $"{description} relative path");
        EnsureFileIsNotLink(path, description);
        var info = new FileInfo(path);
        if (info.Length != evidence.Length)
        {
            throw new InvalidDataException(
                $"{description} length mismatch. Expected {evidence.Length}, got {info.Length}.");
        }

        var actualSha256 = ComputeSha256(path);
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

    private static string ComputeSha256(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(source));
    }

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

    private static bool IsProductionPackageVersion(string value)
    {
        const string prefix = "hyg-v4.2-p3-s2-r";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var revision = value.AsSpan(prefix.Length);
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
        SnapshotTopology? Topology);

    private sealed record SnapshotPackage(string Kind, string Version);

    private sealed record SnapshotCatalog(string Name, string Version);

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
}
