using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

internal static partial class Program
{
    private const string Repository = "RoySalisbury/HVO.SkyMonitor";
    private static readonly string[] InstallerTargets = ["linux-x64", "linux-arm64"];
    private static readonly string[] SpdxCreators = ["Tool: HVO.SkyMonitor.Deployment.ReleaseTool"];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ReleaseToolException("A release-tool command is required.");
            }
            var options = ParseOptions(args[1..]);
            switch (args[0])
            {
                case "create-installer":
                    await CreateInstallerAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "create-catalog":
                    await CreateCatalogAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "create-image":
                    await CreateImageAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "verify-attestation":
                    VerifyAttestation(options);
                    break;
                case "describe-image-archive":
                    await DescribeImageArchiveAsync(options).ConfigureAwait(false);
                    break;
                case "image-index-digest":
                    await WriteImageIndexDigestAsync(options).ConfigureAwait(false);
                    break;
                case "create-index":
                    await CreateIndexAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "sign-local":
                    await SignLocalAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "digest":
                    await WriteDigestAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "write-azure-signature":
                    await WriteAzureSignatureAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "verify":
                    await VerifyAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "verify-signature":
                    await VerifySignatureAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "verify-index":
                    await VerifyIndexAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "list-assets":
                    await ListAssetsAsync(options, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    throw new ReleaseToolException($"Unknown release-tool command '{args[0]}'.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is ReleaseToolException or DistributionValidationException or
                                           IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            await Console.Error.WriteLineAsync($"release error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task CreateInstallerAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--version", "--revision", "--tree", "--created-utc", "--linux-x64", "--linux-arm64", "--notices", "--output", "--signing-key-id");
        var version = RequireSafeIdentifier(options, "--version");
        var revision = RequireGitOid(options, "--revision");
        var tree = RequireGitOid(options, "--tree");
        var createdUtc = RequireUtc(options, "--created-utc");
        var x64 = RequireExistingFile(options, "--linux-x64");
        var arm64 = RequireExistingFile(options, "--linux-arm64");
        var notices = RequireExistingFile(options, "--notices");
        var output = PrepareOutput(options);
        ValidateElf(x64, expectedMachine: 0x3e, "linux-x64");
        ValidateElf(arm64, expectedMachine: 0xb7, "linux-arm64");

        var x64Name = $"hvo-skymonitor-installer-v{version}-linux-x64.tar.gz";
        var arm64Name = $"hvo-skymonitor-installer-v{version}-linux-arm64.tar.gz";
        await WriteInstallerArchiveAsync(x64, notices, Path.Combine(output, x64Name), createdUtc, cancellationToken)
            .ConfigureAwait(false);
        await WriteInstallerArchiveAsync(arm64, notices, Path.Combine(output, arm64Name), createdUtc, cancellationToken)
            .ConfigureAwait(false);
        File.Copy(notices, Path.Combine(output, "THIRD-PARTY-NOTICES.md"));

        var provenanceName = "installer-provenance.json";
        await WriteJsonAsync(Path.Combine(output, provenanceName), new
        {
            schemaVersion = 1,
            subject = "hvo-skymonitor-installer",
            sourceRepository = Repository,
            sourceRevision = revision,
            sourceTree = tree,
            buildTimestampUtc = createdUtc,
            sdkVersion = Environment.Version.ToString(),
            targets = InstallerTargets
        }, cancellationToken).ConfigureAwait(false);
        var sbomName = "installer-sbom.spdx.json";
        await WriteSpdxAsync(
            Path.Combine(output, sbomName),
            $"hvo-skymonitor-installer-{version}",
            version,
            [Path.Combine(output, x64Name), Path.Combine(output, arm64Name)],
            createdUtc,
            cancellationToken).ConfigureAwait(false);

        var payloads = new[]
        {
            (DistributionArtifactRole.Installer, x64Name, "application/gzip", "linux", "x64"),
            (DistributionArtifactRole.Installer, arm64Name, "application/gzip", "linux", "arm64"),
            (DistributionArtifactRole.Sbom, sbomName, "application/spdx+json", (string?)null, null),
            (DistributionArtifactRole.Provenance, provenanceName, "application/json", (string?)null, null),
            (DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md", "text/markdown", (string?)null, null)
        };
        var artifacts = await CreateArtifactsAsync(output, payloads, cancellationToken).ConfigureAwait(false);
        var checksumsName = "SHA256SUMS";
        await WriteChecksumsAsync(output, artifacts, checksumsName, cancellationToken).ConfigureAwait(false);
        artifacts.Add(await CreateArtifactAsync(
            output,
            DistributionArtifactRole.Checksums,
            checksumsName,
            "text/plain",
            null,
            null,
            cancellationToken).ConfigureAwait(false));
        var manifest = new DistributionReleaseManifest(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.InstallerRelease,
            new DistributionReleaseIdentity(
                "installer", version, $"installer-v{version}", Repository, revision, tree, createdUtc),
            SigningIdentity(options),
            artifacts,
            null,
            []);
        await WriteManifestAsync(output, "release-manifest.json", manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateCatalogAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--revision", "--tree", "--created-utc", "--bundle", "--output", "--signing-key-id");
        var revision = RequireGitOid(options, "--revision");
        var tree = RequireGitOid(options, "--tree");
        var createdUtc = RequireUtc(options, "--created-utc");
        var bundle = RequireExistingDirectory(options, "--bundle");
        var output = PrepareOutput(options);
        var innerManifestPath = Path.Combine(bundle, "manifest.json");
        using var inner = JsonDocument.Parse(await File.ReadAllBytesAsync(innerManifestPath, cancellationToken).ConfigureAwait(false));
        var root = inner.RootElement;
        var packageVersion = root.GetProperty("package").GetProperty("version").GetString()
            ?? throw new ReleaseToolException("The catalog manifest omits package.version.");
        ValidateSafeIdentifier(packageVersion, "catalog package version");
        var catalogId = root.GetProperty("catalog").GetProperty("id").GetString()
            ?? throw new ReleaseToolException("The catalog manifest omits catalog.id.");
        var archiveName = $"{packageVersion}.bundle.tar.gz";
        await WriteCatalogArchiveAsync(bundle, Path.Combine(output, archiveName), packageVersion, createdUtc, cancellationToken)
            .ConfigureAwait(false);
        File.Copy(Path.Combine(bundle, "LICENSE-HYG.md"), Path.Combine(output, "LICENSE-HYG.md"));
        File.Copy(Path.Combine(bundle, "ATTRIBUTION-HYG.md"), Path.Combine(output, "ATTRIBUTION-HYG.md"));

        var provenanceName = "catalog-provenance.json";
        await WriteJsonAsync(Path.Combine(output, provenanceName), new
        {
            schemaVersion = 1,
            subject = packageVersion,
            sourceRepository = Repository,
            sourceRevision = revision,
            sourceTree = tree,
            buildTimestampUtc = createdUtc,
            innerManifestSha256 = await Sha256Async(innerManifestPath, cancellationToken).ConfigureAwait(false)
        }, cancellationToken).ConfigureAwait(false);
        var sbomName = "catalog-sbom.spdx.json";
        await WriteSpdxAsync(
            Path.Combine(output, sbomName),
            packageVersion,
            packageVersion,
            Directory.EnumerateFiles(bundle).Order(StringComparer.Ordinal).ToArray(),
            createdUtc,
            cancellationToken).ConfigureAwait(false);
        var payloads = new[]
        {
            (DistributionArtifactRole.CatalogBundle, archiveName, "application/gzip", (string?)null, (string?)null),
            (DistributionArtifactRole.Sbom, sbomName, "application/spdx+json", (string?)null, (string?)null),
            (DistributionArtifactRole.Provenance, provenanceName, "application/json", (string?)null, (string?)null),
            (DistributionArtifactRole.License, "LICENSE-HYG.md", "text/markdown", (string?)null, (string?)null),
            (DistributionArtifactRole.Attribution, "ATTRIBUTION-HYG.md", "text/markdown", (string?)null, (string?)null)
        };
        var artifacts = await CreateArtifactsAsync(output, payloads, cancellationToken).ConfigureAwait(false);
        const string checksumsName = "SHA256SUMS";
        await WriteChecksumsAsync(output, artifacts, checksumsName, cancellationToken).ConfigureAwait(false);
        artifacts.Add(await CreateArtifactAsync(
            output,
            DistributionArtifactRole.Checksums,
            checksumsName,
            "text/plain",
            null,
            null,
            cancellationToken).ConfigureAwait(false));
        var catalog = new DistributionCatalogIdentity(
            catalogId,
            packageVersion,
            root.GetProperty("package").GetProperty("kind").GetString() ?? string.Empty,
            root.GetProperty("manifestVersion").GetInt32(),
            root.GetProperty("schemaVersion").GetString() ?? string.Empty,
            root.GetProperty("preprocessingVersion").GetString() ?? string.Empty,
            await Sha256Async(innerManifestPath, cancellationToken).ConfigureAwait(false),
            root.GetProperty("database").GetProperty("sha256").GetString() ?? string.Empty,
            root.GetProperty("database").GetProperty("length").GetInt64(),
            root.GetProperty("database").GetProperty("rowCount").GetInt64(),
            root.GetProperty("license").GetProperty("identifier").GetString() ?? string.Empty,
            "LICENSE-HYG.md",
            "ATTRIBUTION-HYG.md",
            root.GetProperty("topology").GetProperty("identity").GetString() ?? string.Empty,
            root.GetProperty("topology").GetProperty("sha256").GetString() ?? string.Empty);
        var manifest = new DistributionReleaseManifest(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.CatalogRelease,
            new DistributionReleaseIdentity(
                "catalog", packageVersion, $"catalog-{packageVersion}", Repository, revision, tree, createdUtc),
            SigningIdentity(options),
            artifacts,
            catalog,
            []);
        await WriteManifestAsync(output, "catalog-manifest.json", manifest, cancellationToken).ConfigureAwait(false);
    }

    private static readonly (string Architecture, string Option)[] ImagePlatforms =
    [
        ("amd64", "--linux-amd64"),
        ("arm64", "--linux-arm64")
    ];

    private static readonly string[] SupportedScanners = ["trivy"];
    private static readonly string[] SeverityCounts = ["critical", "high", "medium", "low", "unknown"];

    private const string ComponentLabel = "io.hvo.skymonitor.component";
    private const string RevisionLabel = "org.opencontainers.image.revision";
    private const string SpdxPackageIdentifier = "SPDXRef-Package";
    private const string ScannedImageAnnotation = "ImageID: ";

    /// <summary>
    /// The smallest component count that can plausibly describe a published CameraAgent image. The image is an
    /// ASP.NET runtime on a Debian base, so a real inventory records hundreds of operating-system packages and
    /// .NET libraries; anything near zero means the inventory does not describe the image being published.
    /// </summary>
    private const int MinimumInventoryComponents = 32;

    /// <summary>
    /// Builds the signed CameraAgent image release from per-platform OCI archives. Every platform digest, immutable
    /// image ID, architecture, and compatibility label written into the manifest is read out of the archive bytes,
    /// so signed metadata cannot describe an image the release does not actually contain.
    /// </summary>
    private static async Task CreateImageAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(
            options,
            "--version", "--revision", "--tree", "--created-utc", "--repository", "--index-digest",
            "--linux-amd64", "--linux-arm64", "--component-sbom-amd64", "--component-sbom-arm64",
            "--dockerfile", "--scan-report", "--notices", "--output", "--signing-key-id");
        var version = RequireSafeIdentifier(options, "--version");
        var revision = RequireGitOid(options, "--revision");
        var tree = RequireGitOid(options, "--tree");
        var createdUtc = RequireUtc(options, "--created-utc");
        var repository = RequireImageRepository(options, "--repository");
        var indexDigest = RequireDigest(options, "--index-digest");
        var dockerfile = RequireExistingFile(options, "--dockerfile");
        var scanReport = RequireExistingFile(options, "--scan-report");
        var notices = RequireExistingFile(options, "--notices");

        var inspected = new List<(string Architecture, string Source, string AssetName, ImageArchiveIdentity Identity)>();
        foreach (var (architecture, option) in ImagePlatforms)
        {
            var source = RequireExistingFile(options, option);
            var identity = ImageArchiveInspector.Inspect(source);
            if (identity.OperatingSystem != "linux" || identity.Architecture != architecture)
            {
                throw new ReleaseToolException(
                    $"{option} contains a linux/{identity.Architecture} image but must contain linux/{architecture}.");
            }
            inspected.Add((architecture, source, $"cameraagent-image-v{version}-linux-{architecture}.tar", identity));
        }
        var compatibility = ReadImageCompatibility(inspected, revision);
        ValidateScanReport(scanReport, version, inspected.Select(static value => value.Identity.ImageId).ToArray());
        var inventories = ReadComponentInventories(options, version, inspected);

        var output = PrepareOutput(options);
        foreach (var platform in inspected)
        {
            var destination = Path.Combine(output, platform.AssetName);
            RefuseExisting(destination);
            File.Copy(platform.Source, destination);
        }
        foreach (var (architecture, source) in inventories)
        {
            var destination = Path.Combine(output, ComponentInventoryAsset(architecture));
            RefuseExisting(destination);
            File.Copy(source, destination);
        }
        File.Copy(notices, Path.Combine(output, "THIRD-PARTY-NOTICES.md"));
        const string scanName = "image-vulnerability-scan.json";
        File.Copy(scanReport, Path.Combine(output, scanName));

        const string provenanceName = "image-provenance.json";
        await WriteJsonAsync(Path.Combine(output, provenanceName), new
        {
            schemaVersion = 1,
            subject = $"{repository}@{indexDigest}",
            sourceRepository = Repository,
            sourceRevision = revision,
            sourceTree = tree,
            buildTimestampUtc = createdUtc,
            dockerfile = new
            {
                path = "src/HVO.SkyMonitor.CameraAgent/Dockerfile",
                sha256 = await Sha256Async(dockerfile, cancellationToken).ConfigureAwait(false)
            },
            imageRepository = repository,
            imageManifestDigest = indexDigest,
            platforms = inspected.Select(platform => new
            {
                os = platform.Identity.OperatingSystem,
                architecture = platform.Identity.Architecture,
                manifestDigest = platform.Identity.ManifestDigest,
                imageId = platform.Identity.ImageId,
                archiveAsset = platform.AssetName
            }).ToArray(),
            labels = inspected[0].Identity.Labels.OrderBy(static label => label.Key, StringComparer.Ordinal)
                .ToDictionary(static label => label.Key, static label => label.Value, StringComparer.Ordinal)
        }, cancellationToken).ConfigureAwait(false);
        const string sbomName = "image-sbom.spdx.json";
        await WriteSpdxAsync(
            Path.Combine(output, sbomName),
            $"hvo-skymonitor-cameraagent-{version}",
            version,
            inspected.Select(platform => Path.Combine(output, platform.AssetName)).ToArray(),
            createdUtc,
            cancellationToken).ConfigureAwait(false);

        var payloads = inspected
            .Select(platform => (DistributionArtifactRole.ImageArchive, platform.AssetName, "application/x-tar", (string?)"linux", (string?)platform.Architecture))
            // Ordered explicitly: these entries become the signed artifacts array, and dictionary enumeration
            // order is not a documented guarantee. The signature covers the exact manifest bytes, so the order
            // has to come from the data rather than from a hash table.
            .Concat(inventories.Keys.Order(StringComparer.Ordinal).Select(architecture => (
                DistributionArtifactRole.ComponentSbom,
                ComponentInventoryAsset(architecture),
                "application/spdx+json",
                (string?)"linux",
                (string?)architecture)))
            .Concat(
            [
                (DistributionArtifactRole.Sbom, sbomName, "application/spdx+json", (string?)null, (string?)null),
                (DistributionArtifactRole.Provenance, provenanceName, "application/json", (string?)null, (string?)null),
                (DistributionArtifactRole.VulnerabilityScan, scanName, "application/json", (string?)null, (string?)null),
                (DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md", "text/markdown", (string?)null, (string?)null)
            ]);
        var artifacts = await CreateArtifactsAsync(output, payloads, cancellationToken).ConfigureAwait(false);
        const string checksumsName = "SHA256SUMS";
        await WriteChecksumsAsync(output, artifacts, checksumsName, cancellationToken).ConfigureAwait(false);
        artifacts.Add(await CreateArtifactAsync(
            output,
            DistributionArtifactRole.Checksums,
            checksumsName,
            "text/plain",
            null,
            null,
            cancellationToken).ConfigureAwait(false));
        var image = new DistributionImageIdentity(
            "CameraAgent",
            repository,
            indexDigest,
            revision,
            tree,
            inspected.Select(platform => new DistributionImagePlatform(
                "linux",
                platform.Architecture,
                platform.Identity.ManifestDigest,
                platform.AssetName,
                platform.Identity.ImageId,
                inventories.Count == 0 ? null : ComponentInventoryAsset(platform.Architecture))).ToArray(),
            provenanceName,
            sbomName,
            scanName,
            compatibility);
        var manifest = new DistributionReleaseManifest(
            inventories.Count == 0
                ? DistributionSchemaVersions.ReleaseManifest
                : DistributionSchemaVersions.ReleaseManifestWithComponentSboms,
            DistributionManifestKind.ImageRelease,
            new DistributionReleaseIdentity(
                "image", version, $"image-v{version}", Repository, revision, tree, createdUtc),
            SigningIdentity(options),
            artifacts,
            null,
            [image]);
        await WriteManifestAsync(output, "image-manifest.json", manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The in-toto statement types buildx has produced. A registry push from an older BuildKit emits the v0.1
    /// statement with an SLSA v0.2 predicate, and a newer one emits the v1 statement with an SLSA v1 predicate;
    /// both are real and both are accepted, because refusing one aborts a release after the immutable push.
    /// </summary>
    private static readonly string[] StatementTypes =
    [
        "https://in-toto.io/Statement/v0.1",
        "https://in-toto.io/Statement/v1"
    ];

    private const string SbomPredicate = "https://spdx.dev/Document";

    private static readonly string[] ProvenancePredicates =
    [
        "https://slsa.dev/provenance/v0.2",
        "https://slsa.dev/provenance/v1"
    ];

    /// <summary>
    /// Verifies that an in-toto statement pulled from the registry actually attests the platform manifest the
    /// release examined. The attestation manifest's <c>vnd.docker.reference.digest</c> annotation only says what
    /// the wrapper claims; the statement's own <c>subject</c> is what the attestation is about, and for a registry
    /// push BuildKit populates it with the platform manifest digest. Checking the annotation alone would accept a
    /// correctly labelled attestation whose payload concerns different bytes.
    /// </summary>
    private static void VerifyAttestation(Dictionary<string, string> options)
    {
        RejectUnknown(options, "--manifest-digest", "--statement", "--predicate-type");
        var expected = RequireDigest(options, "--manifest-digest")["sha256:".Length..];
        var path = RequireExistingFile(options, "--statement");
        var expectedPredicate = options.TryGetValue("--predicate-type", out var configured) ? configured : null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new ReleaseToolException($"The attestation statement '{path}' is not valid JSON.", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "_type") is not { } statementType ||
                !StatementTypes.Contains(statementType, StringComparer.Ordinal))
            {
                throw new ReleaseToolException(
                    $"The attestation statement '{path}' is not an in-toto statement this release understands.");
            }
            var predicate = Text(root, "predicateType");
            if (predicate is null ||
                (predicate != SbomPredicate && !ProvenancePredicates.Contains(predicate, StringComparer.Ordinal)))
            {
                throw new ReleaseToolException(
                    $"The attestation statement '{path}' declares predicate type '{predicate ?? "<none>"}', which is " +
                    "not an SBOM or SLSA provenance statement.");
            }
            if (expectedPredicate is not null && predicate != expectedPredicate)
            {
                throw new ReleaseToolException(
                    $"The attestation statement '{path}' declares predicate type '{predicate}' but the manifest " +
                    $"layer it was read from is annotated '{expectedPredicate}'.");
            }
            if (!root.TryGetProperty("subject", out var subjects) || subjects.ValueKind != JsonValueKind.Array ||
                subjects.GetArrayLength() == 0)
            {
                throw new ReleaseToolException(
                    $"The attestation statement '{path}' names no subject, so it does not attest any image.");
            }
            // Every subject must be this platform's manifest. A push names one image under one or more tags, so
            // the digests are identical; a statement that also attests something else is not this image's
            // provenance and must not be accepted as it.
            foreach (var subject in subjects.EnumerateArray())
            {
                if (subject.ValueKind != JsonValueKind.Object ||
                    !subject.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.Object ||
                    Text(digest, "sha256") is not { Length: 64 } sha256 ||
                    !sha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    !string.Equals(sha256, expected, StringComparison.Ordinal))
                {
                    throw new ReleaseToolException(
                        $"The attestation statement '{path}' attests a subject that is not the published platform " +
                        $"manifest sha256:{expected}.");
                }
            }
        }
        Console.Out.WriteLine($"Attestation {Path.GetFileName(path)} attests sha256:{expected}.");
    }

    private static string ComponentInventoryAsset(string architecture)
        => $"image-components-linux-{architecture}.spdx.json";

    /// <summary>
    /// Resolves the per-platform component inventories this release publishes. They are supplied for every
    /// published platform or for none: an inventory for one architecture presented without the other would leave
    /// half a release describing its own contents. A candidate that omits them is accepted only for a version
    /// ending in <c>-dryrun</c>, which the tool never lets reach publication, exactly as the vulnerability scan is
    /// gated, so a publishable image release always carries an inventory for every architecture it ships.
    /// </summary>
    private static Dictionary<string, string> ReadComponentInventories(
        IReadOnlyDictionary<string, string> options,
        string version,
        List<(string Architecture, string Source, string AssetName, ImageArchiveIdentity Identity)> inspected)
    {
        var inventories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var platform in inspected)
        {
            var option = $"--component-sbom-{platform.Architecture}";
            if (options.ContainsKey(option))
            {
                inventories[platform.Architecture] = RequireExistingFile(options, option);
            }
        }
        if (inventories.Count == 0)
        {
            if (!version.EndsWith("-dryrun", StringComparison.Ordinal))
            {
                throw new ReleaseToolException(
                    "A published image release requires a component inventory for every published platform " +
                    "(--component-sbom-amd64 and --component-sbom-arm64). Only a version ending in '-dryrun', " +
                    "which is never publishable, may omit them.");
            }
            return inventories;
        }
        if (inventories.Count != inspected.Count)
        {
            throw new ReleaseToolException(
                "A component inventory must be supplied for every published platform or for none.");
        }
        foreach (var platform in inspected)
        {
            ValidateComponentInventory(inventories[platform.Architecture], platform.Architecture, platform.Identity.ImageId);
        }
        return inventories;
    }

    /// <summary>
    /// Requires a per-platform component inventory to be an SPDX 2.3 document that names the exact image this
    /// release publishes for that platform and actually enumerates components. The scanner renders the inventory
    /// from the same pass that produced the vulnerability report and records the image it scanned in the container
    /// package's annotations, so the binding rests on the scanner's own subject identity rather than on the file
    /// name the inventory was handed to the tool under. An inventory for the other architecture, for an older
    /// build, or an empty stub therefore cannot be signed as this platform's contents.
    /// </summary>
    private static void ValidateComponentInventory(string path, string architecture, string imageId)
    {
        using var document = ParseInventory(path, architecture);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            Text(root, "spdxVersion") != "SPDX-2.3" || Text(root, "SPDXID") != "SPDXRef-DOCUMENT" ||
            !root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
        {
            throw new ReleaseToolException($"The linux/{architecture} component inventory is not an SPDX 2.3 document.");
        }
        // Collect every image-subject claim in the document rather than only the expected one. A document that
        // also claims another image could otherwise keep its real subject and carry an injected annotation for
        // this platform, and would then pass as either platform's inventory.
        var claims = new List<string>();
        var components = 0;
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseToolException($"The linux/{architecture} component inventory contains an invalid package.");
            }
            var declared = ScannedImageClaims(package).ToArray();
            if (declared.Length != 0)
            {
                claims.AddRange(declared);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(Text(package, "name")) &&
                !string.IsNullOrWhiteSpace(Text(package, "versionInfo")))
            {
                components++;
            }
        }
        if (claims.Count != 1 || claims[0] != imageId)
        {
            throw new ReleaseToolException(
                $"The linux/{architecture} component inventory does not name the published image {imageId} as its " +
                $"single subject; it claims [{string.Join(", ", claims)}].");
        }
        ValidateInventoryFiles(root, architecture);
        if (components < MinimumInventoryComponents)
        {
            throw new ReleaseToolException(
                $"The linux/{architecture} component inventory records {components} components, which cannot describe " +
                $"a published CameraAgent image; at least {MinimumInventoryComponents} are required.");
        }
    }

    /// <summary>
    /// Reads a member as a string, returning null when it is absent or is present with another JSON type. Reading
    /// it with <c>GetString()</c> would throw past this tool's own error handling on a document it did not write.
    /// </summary>
    private static string? Text(JsonElement element, string member)
        => element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Parses an inventory, naming the platform and the file in the failure. The document is produced by the
    /// scanner rather than by this tool, so a malformed one is a plausible release failure and the operator needs
    /// to be told which file to look at rather than only that some JSON did not parse.
    /// </summary>
    private static JsonDocument ParseInventory(string path, string architecture)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new ReleaseToolException(
                $"The linux/{architecture} component inventory '{path}' is not valid JSON.", exception);
        }
    }

    /// <summary>Every image identity this package annotates itself with, in the scanner's <c>ImageID: </c> form.</summary>
    private static IEnumerable<string> ScannedImageClaims(JsonElement package)
    {
        if (!package.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (annotation.ValueKind == JsonValueKind.Object &&
                Text(annotation, "comment") is { } value &&
                value.StartsWith(ScannedImageAnnotation, StringComparison.Ordinal))
            {
                yield return value[ScannedImageAnnotation.Length..];
            }
        }
    }

    /// <summary>
    /// Requires every file the inventory declares to carry the SHA-1 checksum SPDX 2.3 clause 8.4 makes mandatory.
    /// The release signs this document, so it must not sign one that violates the format it declares. The scanner's
    /// container-image output carries no files, which makes this a guard against a substituted inventory rather
    /// than a check on the scanner.
    /// </summary>
    private static void ValidateInventoryFiles(JsonElement root, string architecture)
    {
        if (!root.TryGetProperty("files", out var files))
        {
            return;
        }
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new ReleaseToolException(
                $"The linux/{architecture} component inventory declares a 'files' member that is not an array.");
        }
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                !file.TryGetProperty("checksums", out var checksums) || checksums.ValueKind != JsonValueKind.Array ||
                !checksums.EnumerateArray().Any(static checksum =>
                    checksum.ValueKind == JsonValueKind.Object &&
                    checksum.TryGetProperty("algorithm", out var algorithm) &&
                    algorithm.ValueKind == JsonValueKind.String && algorithm.GetString() == "SHA1" &&
                    Text(checksum, "checksumValue") is { Length: 40 } sha1 &&
                    // The value is only required to be a well-formed SHA-1; it is never compared or reused, so
                    // rejecting a document purely for emitting uppercase digits would refuse a valid inventory.
                    sha1.All(static character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
            {
                throw new ReleaseToolException(
                    $"The linux/{architecture} component inventory declares a file without the SHA-1 checksum " +
                    "SPDX 2.3 requires.");
            }
        }
    }

    /// <summary>Prints the platform identity a single-platform image archive carries, for release scripting.</summary>
    private static async Task DescribeImageArchiveAsync(IReadOnlyDictionary<string, string> options)
    {
        RejectUnknown(options, "--archive");
        var identity = ImageArchiveInspector.Inspect(RequireExistingFile(options, "--archive"));
        await Console.Out.WriteLineAsync(
            $"{identity.ManifestDigest}\t{identity.ImageId}\t{identity.OperatingSystem}\t{identity.Architecture}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Prints the digest of the canonical OCI image index over the published platform archives. A real publication
    /// takes the multi-architecture digest from the registry it pushed to; this value lets a release that has not
    /// been pushed still name one immutable multi-architecture identity derived from the same platform manifests.
    /// </summary>
    private static async Task WriteImageIndexDigestAsync(IReadOnlyDictionary<string, string> options)
    {
        RejectUnknown(options, "--linux-amd64", "--linux-arm64");
        var descriptors = new List<object>();
        foreach (var (architecture, option) in ImagePlatforms)
        {
            var archive = RequireExistingFile(options, option);
            var identity = ImageArchiveInspector.Inspect(archive);
            if (identity.OperatingSystem != "linux" || identity.Architecture != architecture)
            {
                throw new ReleaseToolException($"{option} does not contain a linux/{architecture} image.");
            }
            descriptors.Add(new
            {
                mediaType = "application/vnd.oci.image.manifest.v1+json",
                digest = identity.ManifestDigest,
                size = ImageArchiveInspector.MeasureManifest(archive, identity.ManifestDigest),
                platform = new { architecture = identity.Architecture, os = identity.OperatingSystem }
            });
        }
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.index.v1+json",
            manifests = descriptors
        });
        await Console.Out.WriteLineAsync("sha256:" + Convert.ToHexStringLower(SHA256.HashData(index))).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the declared compatibility boundaries from the image labels and requires every published platform to
    /// agree, so a multi-architecture release cannot ship one architecture that reads different durable state.
    /// </summary>
    private static DistributionImageCompatibility ReadImageCompatibility(
        List<(string Architecture, string Source, string AssetName, ImageArchiveIdentity Identity)> platforms,
        string revision)
    {
        var reference = platforms[0].Identity.Labels;
        foreach (var platform in platforms)
        {
            if (platform.Identity.Labels.Count != reference.Count ||
                platform.Identity.Labels.Any(label => !reference.TryGetValue(label.Key, out var value) || value != label.Value))
            {
                throw new ReleaseToolException(
                    $"The linux/{platform.Architecture} image declares different labels than linux/{platforms[0].Architecture}.");
            }
        }
        if (Label(reference, ComponentLabel) != "CameraAgent")
        {
            throw new ReleaseToolException("The published image must declare the CameraAgent component label.");
        }
        if (Label(reference, RevisionLabel) != revision)
        {
            throw new ReleaseToolException("The image revision label does not match --revision.");
        }
        return new DistributionImageCompatibility(
            Label(reference, "io.hvo.skymonitor.state-compatibility"),
            Label(reference, "io.hvo.skymonitor.minimum-compatible-revision"),
            Label(reference, "io.hvo.skymonitor.identity-migration"),
            LabelVersion(reference, "io.hvo.skymonitor.raw-ingress-schema"),
            LabelVersion(reference, "io.hvo.skymonitor.catalog-manifest-version"),
            Label(reference, "io.hvo.skymonitor.configuration-contract"),
            Label(reference, "io.hvo.skymonitor.catalog-contract"),
            reference.TryGetValue("io.hvo.skymonitor.replay-runner-contract", out var replay) ? replay : null);
    }

    private static string Label(IReadOnlyDictionary<string, string> labels, string name)
        => labels.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ReleaseToolException($"The published image omits the required label '{name}'.");

    private static int LabelVersion(IReadOnlyDictionary<string, string> labels, string name)
        => int.TryParse(Label(labels, name), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ReleaseToolException($"The published image label '{name}' is not a positive integer.");

    /// <summary>
    /// Requires an image release to carry a scan report from a supported scanner that covers exactly the published
    /// image IDs and reports no critical finding. The release tool never runs a scanner itself; it refuses to build
    /// a release whose recorded scan does not describe the images being published. An unscanned candidate is
    /// permitted only for a version that names itself a dry run, so a publishable version can never carry one.
    /// Only the critical count gates the release: a base image routinely carries lower-severity findings, so the
    /// remaining counts are required to be present as evidence rather than to be zero.
    /// </summary>
    private static void ValidateScanReport(string path, string version, string[] imageIds)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        // Every member is read through the ValueKind-checked accessor. This report is produced outside the tool,
        // so a member that is present with the wrong JSON type is ordinary malformed input and must fail as a
        // release error rather than as an unhandled InvalidOperationException from GetString().
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaValue) || schemaValue != 1 ||
            string.IsNullOrWhiteSpace(Text(root, "scanner")) ||
            string.IsNullOrWhiteSpace(Text(root, "scannerVersion")) ||
            Text(root, "scannedUtc") is not { } scannedUtcValue ||
            !scannedUtcValue.EndsWith('Z') ||
            !DateTimeOffset.TryParse(scannedUtcValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ReleaseToolException("The image vulnerability scan report does not declare a scanner and scan time.");
        }
        var scannerName = Text(root, "scanner")!;
        var status = root.TryGetProperty("status", out _) ? Text(root, "status") : "scanned";
        if ((status != "scanned" || !SupportedScanners.Contains(scannerName, StringComparer.Ordinal)) &&
            !version.EndsWith("-dryrun", StringComparison.Ordinal))
        {
            throw new ReleaseToolException(
                $"A published image release requires a completed scan by a supported scanner ({string.Join(", ", SupportedScanners)}); " +
                $"this report declares scanner '{scannerName}' with status '{status}'. An unscanned candidate is only " +
                "permitted for a version ending in '-dryrun', which is never publishable.");
        }
        if (!root.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array)
        {
            throw new ReleaseToolException("The image vulnerability scan report does not list its scanned subjects.");
        }
        // Every entry is refused rather than skipped. Filtering a malformed one out would let a report that lists
        // junk alongside the right image IDs still satisfy the "covers exactly the published images" rule below,
        // because the junk would simply vanish from the comparison.
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects.EnumerateArray())
        {
            if (subject.ValueKind != JsonValueKind.Object || Text(subject, "imageId") is not { } scannedImageId ||
                string.IsNullOrWhiteSpace(scannedImageId))
            {
                throw new ReleaseToolException("The image vulnerability scan report lists an invalid scanned subject.");
            }
            scanned.Add(scannedImageId);
        }
        if (!imageIds.All(scanned.Contains) || scanned.Count != imageIds.Length)
        {
            throw new ReleaseToolException("The image vulnerability scan report does not cover exactly the published images.");
        }
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
            SeverityCounts.Any(severity => !summary.TryGetProperty(severity, out var count) ||
                count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var value) || value < 0))
        {
            throw new ReleaseToolException(
                $"The image vulnerability scan report must record every severity count ({string.Join(", ", SeverityCounts)}).");
        }
        if (summary.GetProperty("critical").GetInt32() != 0)
        {
            throw new ReleaseToolException("The image vulnerability scan report must record zero critical findings.");
        }
    }

    private static string RequireDigest(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
               value[7..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ReleaseToolException($"{name} must be a lowercase sha256:<64 hex> digest.");
    }

    /// <summary>
    /// Applies the same repository shape the signed manifest is verified against, so a candidate cannot be built
    /// with a reference that verification would later reject.
    /// </summary>
    private static string RequireImageRepository(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return ImageRepositoryRegex().IsMatch(value)
            ? value
            : throw new ReleaseToolException(
                $"{name} must be a lowercase OCI repository reference with a host and at least one path segment, " +
                "and without a tag or digest.");
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "^[a-z0-9][a-z0-9.-]{0,63}(:[0-9]{1,5})?(/[a-z0-9]+([._-][a-z0-9]+)*){1,6}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ImageRepositoryRegex();

    private static async Task SignLocalAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--manifest", "--private-key", "--signature", "--metadata-kind");
        var manifest = RequireExistingFile(options, "--manifest");
        var privateKey = RequireExistingFile(options, "--private-key");
        var output = Require(options, "--signature");
        RefuseExisting(output);
        var bytes = await File.ReadAllBytesAsync(manifest, cancellationToken).ConfigureAwait(false);
        var pem = await File.ReadAllTextAsync(privateKey, cancellationToken).ConfigureAwait(false);
        var signature = DistributionVerifier.Sign(bytes, pem);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var trustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());
        var signatureText = Encoding.ASCII.GetBytes(Convert.ToBase64String(signature));
        var metadataKind = options.TryGetValue("--metadata-kind", out var configuredKind) ? configuredKind : "manifest";
        switch (metadataKind)
        {
            case "manifest":
                _ = DistributionVerifier.VerifyManifest(bytes, signatureText, trustRoot);
                break;
            case "index":
                _ = DistributionVerifier.VerifyIndex(bytes, signatureText, trustRoot);
                break;
            case "detached":
                // A checksum list carries no signed schema of its own; the detached signature is the only claim.
                using (var verifier = trustRoot.CreateVerifier())
                {
                    if (!verifier.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    {
                        throw new ReleaseToolException("The detached signature did not verify against its own key.");
                    }
                }
                break;
            default:
                throw new ReleaseToolException("--metadata-kind must be manifest, index, or detached.");
        }
        await File.WriteAllTextAsync(output, Convert.ToBase64String(signature) + "\n", Encoding.ASCII, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateIndexAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(
            options,
            "--train", "--sequence", "--default-version", "--created-utc", "--manifest", "--manifest-signature",
            "--previous-index", "--previous-index-signature", "--public-key", "--signing-key-id", "--output");
        var train = Require(options, "--train");
        if (train is not ("installer" or "catalog" or "image"))
        {
            throw new ReleaseToolException("--train must be installer, catalog, or image.");
        }
        var sequenceText = Require(options, "--sequence");
        if (!long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
        {
            throw new ReleaseToolException("--sequence must be a positive integer.");
        }
        var createdUtc = RequireUtc(options, "--created-utc");
        var manifestPath = RequireExistingFile(options, "--manifest");
        var manifestSignaturePath = RequireExistingFile(options, "--manifest-signature");
        var trustRoot = options.ContainsKey("--public-key")
            ? DistributionTrustRoot.FromPem(await File.ReadAllTextAsync(RequireExistingFile(options, "--public-key"), cancellationToken).ConfigureAwait(false))
            : DistributionTrustRoot.Production;
        var signingIdentity = SigningIdentity(options);
        if (signingIdentity.KeyId != trustRoot.KeyId)
        {
            throw new ReleaseToolException("--signing-key-id does not match the index verification key.");
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestSignature = await File.ReadAllBytesAsync(manifestSignaturePath, cancellationToken).ConfigureAwait(false);
        var manifest = DistributionVerifier.VerifyManifest(manifestBytes, manifestSignature, trustRoot);
        if (manifest.Release.Train != train)
        {
            throw new ReleaseToolException("The release manifest does not match --train.");
        }

        var releases = new List<DistributionReleaseReference>();
        if (options.TryGetValue("--previous-index", out var previousIndexPath))
        {
            if (!options.TryGetValue("--previous-index-signature", out var previousSignaturePath))
            {
                throw new ReleaseToolException("--previous-index-signature is required with --previous-index.");
            }
            var previousBytes = await File.ReadAllBytesAsync(Path.GetFullPath(previousIndexPath), cancellationToken).ConfigureAwait(false);
            var previousSignature = await File.ReadAllBytesAsync(Path.GetFullPath(previousSignaturePath), cancellationToken).ConfigureAwait(false);
            var previous = DistributionVerifier.VerifyIndex(previousBytes, previousSignature, trustRoot);
            if (previous.Train != train || sequence != previous.Sequence + 1)
            {
                throw new ReleaseToolException("The new index must continue the same train at the next sequence.");
            }
            releases.AddRange(previous.Releases);
        }
        else if (options.ContainsKey("--previous-index-signature"))
        {
            throw new ReleaseToolException("--previous-index is required with --previous-index-signature.");
        }
        if (releases.Any(release => release.Version == manifest.Release.Version || release.Tag == manifest.Release.Tag))
        {
            throw new ReleaseToolException("The release index already contains this version or tag.");
        }
        releases.Add(new DistributionReleaseReference(
            manifest.Release.Version,
            manifest.Release.Tag,
            Path.GetFileName(manifestPath),
            manifestBytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Path.GetFileName(manifestSignaturePath)));
        var defaultVersion = options.TryGetValue("--default-version", out var configuredDefault)
            ? configuredDefault
            : manifest.Release.Version;
        if (!releases.Any(release => release.Version == defaultVersion))
        {
            throw new ReleaseToolException("--default-version is not present in the release index.");
        }
        var output = PrepareOutput(options);
        var index = new DistributionReleaseIndex(
            DistributionSchemaVersions.ReleaseIndex,
            "release-index",
            train,
            sequence,
            createdUtc,
            defaultVersion,
            signingIdentity,
            releases.OrderBy(static release => release.Version, StringComparer.Ordinal).ToArray());
        await WriteJsonAsync(
            Path.Combine(output, $"{train}-release-index.json"),
            index,
            DistributionJsonContext.Default.DistributionReleaseIndex,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDigestAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--input");
        var input = RequireExistingFile(options, "--input");
        await using var stream = File.OpenRead(input);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(Convert.ToBase64String(digest)).ConfigureAwait(false);
    }

    private static async Task WriteAzureSignatureAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--value", "--signature");
        var value = Require(options, "--value");
        var output = Require(options, "--signature");
        RefuseExisting(output);
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        var signature = Convert.FromBase64String(normalized);
        if (signature.Length != 64)
        {
            throw new ReleaseToolException("Azure Key Vault did not return a 64-byte ES256 signature.");
        }
        await File.WriteAllTextAsync(output, Convert.ToBase64String(signature) + "\n", Encoding.ASCII, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--manifest", "--signature", "--asset-root", "--public-key");
        var manifestPath = RequireExistingFile(options, "--manifest");
        var signaturePath = RequireExistingFile(options, "--signature");
        var assetRoot = RequireExistingDirectory(options, "--asset-root");
        var trustRoot = options.TryGetValue("--public-key", out var publicKey)
            ? DistributionTrustRoot.FromPem(await File.ReadAllTextAsync(publicKey, cancellationToken).ConfigureAwait(false))
            : DistributionTrustRoot.Production;
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var manifest = DistributionVerifier.VerifyManifest(manifestBytes, signature, trustRoot);
        foreach (var artifact in manifest.Artifacts)
        {
            var path = Path.Combine(assetRoot, artifact.AssetName);
            if (!File.Exists(path))
            {
                throw new ReleaseToolException($"Release asset '{artifact.AssetName}' is missing.");
            }
            await using var stream = File.OpenRead(path);
            await DistributionVerifier.VerifyAssetAsync(stream, artifact, cancellationToken).ConfigureAwait(false);
        }
        if (manifest.ManifestKind == DistributionManifestKind.ImageRelease)
        {
            VerifyImageArchives(assetRoot, manifest.Images[0]);
        }
        else if (manifest.ManifestKind == DistributionManifestKind.CatalogRelease)
        {
            var catalogArtifact = manifest.Artifacts.Single(static artifact => artifact.Role == DistributionArtifactRole.CatalogBundle);
            VerifyCatalogArchive(Path.Combine(assetRoot, catalogArtifact.AssetName), manifest.Catalog!);
        }
        else
        {
            foreach (var installer in manifest.Artifacts.Where(static artifact => artifact.Role == DistributionArtifactRole.Installer))
            {
                VerifyInstallerArchive(Path.Combine(assetRoot, installer.AssetName), installer.Architecture!);
            }
        }
        await Console.Out.WriteLineAsync(
            $"Verified {manifest.ManifestKind} {manifest.Release.Tag} with {manifest.Signing.KeyId}.").ConfigureAwait(false);
    }

    /// <summary>
    /// Re-derives every platform identity from the published archives so a release whose archive bytes no longer
    /// produce the signed digest, image ID, architecture, or compatibility labels fails verification.
    /// </summary>
    private static void VerifyImageArchives(string assetRoot, DistributionImageIdentity image)
    {
        foreach (var platform in image.Platforms)
        {
            var identity = ImageArchiveInspector.Inspect(Path.Combine(assetRoot, platform.OfflineArchiveAsset!));
            if (identity.ManifestDigest != platform.ManifestDigest || identity.ImageId != platform.OfflineArchiveImageId ||
                identity.OperatingSystem != platform.OperatingSystem || identity.Architecture != platform.Architecture)
            {
                throw new ReleaseToolException(
                    $"Image archive '{platform.OfflineArchiveAsset}' does not match its signed platform identity.");
            }
            if (Label(identity.Labels, ComponentLabel) != image.Component ||
                Label(identity.Labels, RevisionLabel) != image.SourceRevision ||
                Label(identity.Labels, "io.hvo.skymonitor.state-compatibility") != image.Compatibility.StateContract ||
                Label(identity.Labels, "io.hvo.skymonitor.minimum-compatible-revision") != image.Compatibility.MinimumCompatibleRevision ||
                Label(identity.Labels, "io.hvo.skymonitor.identity-migration") != image.Compatibility.IdentityMigration ||
                LabelVersion(identity.Labels, "io.hvo.skymonitor.raw-ingress-schema") != image.Compatibility.RawIngressSchema ||
                LabelVersion(identity.Labels, "io.hvo.skymonitor.catalog-manifest-version") != image.Compatibility.CatalogManifestVersion ||
                Label(identity.Labels, "io.hvo.skymonitor.configuration-contract") != image.Compatibility.ConfigurationContract ||
                Label(identity.Labels, "io.hvo.skymonitor.catalog-contract") != image.Compatibility.CatalogContract ||
                (identity.Labels.TryGetValue("io.hvo.skymonitor.replay-runner-contract", out var replay) ? replay : null)
                    != image.Compatibility.ReplayRunnerContract)
            {
                throw new ReleaseToolException(
                    $"Image archive '{platform.OfflineArchiveAsset}' does not carry its signed compatibility labels.");
            }
            if (platform.ComponentSbomAsset is { } inventory)
            {
                // Re-derive the inventory's own subject claim from the published bytes, so a release whose
                // component inventory describes a different image fails verification rather than being trusted
                // because the manifest names it.
                ValidateComponentInventory(Path.Combine(assetRoot, inventory), platform.Architecture, identity.ImageId);
            }
        }
    }

    private static void VerifyCatalogArchive(string archivePath, DistributionCatalogIdentity catalog)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-catalog-{Guid.NewGuid():N}");
        var versionRoot = Path.Combine(root, "versions", catalog.PackageVersion);
        Directory.CreateDirectory(versionRoot);
        try
        {
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "ATTRIBUTION-HYG.md", "LICENSE-HYG.md", "hyg_v42.sqlite", "manifest.json"
            };
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var prefix = $"{catalog.PackageVersion}.bundle/";
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (entry.EntryType != TarEntryType.RegularFile || !entry.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                    entry.DataStream is null)
                {
                    throw new ReleaseToolException("The catalog archive contains an unsupported entry.");
                }
                var name = entry.Name[prefix.Length..];
                if (name.Contains('/', StringComparison.Ordinal) || !expected.Remove(name))
                {
                    throw new ReleaseToolException("The catalog archive contains an unexpected or duplicate file.");
                }
                var destination = Path.Combine(versionRoot, name);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                entry.DataStream.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destination, UnixFileMode.UserRead);
                }
            }
            if (expected.Count != 0)
            {
                throw new ReleaseToolException("The catalog archive omits required files.");
            }
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), $"versions/{catalog.PackageVersion}");
            var resolved = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(root, catalog.CatalogId)
            {
                ExpectedSchemaVersion = catalog.SchemaVersion,
                ExpectedPreprocessingVersion = catalog.PreprocessingVersion
            });
            if (resolved.SnapshotVersion != catalog.PackageVersion ||
                !string.Equals(resolved.DatabaseSha256, catalog.DatabaseSha256, StringComparison.OrdinalIgnoreCase) ||
                resolved.DatabaseLength != catalog.DatabaseLength || resolved.RowCount != catalog.RowCount)
            {
                throw new ReleaseToolException(
                    $"The catalog archive does not match its signed internal identity " +
                    $"(package={resolved.SnapshotVersion == catalog.PackageVersion}, database={string.Equals(resolved.DatabaseSha256, catalog.DatabaseSha256, StringComparison.OrdinalIgnoreCase)}, " +
                    $"length={resolved.DatabaseLength == catalog.DatabaseLength}, rows={resolved.RowCount == catalog.RowCount}).");
            }
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(versionRoot, "manifest.json"))));
            if (manifestHash != catalog.BundleManifestSha256)
            {
                throw new ReleaseToolException("The catalog archive manifest does not match its signed checksum.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void VerifyInstallerArchive(string archivePath, string architecture)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "hvo-skymonitor", "THIRD-PARTY-NOTICES.md" };
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null || !expected.Remove(entry.Name))
            {
                throw new ReleaseToolException("The installer archive contains an unexpected, duplicate, or non-regular entry.");
            }
            if (entry.Name == "hvo-skymonitor")
            {
                if (entry.Length is < 20 or > DistributionVerifier.MaximumInstallerBytes ||
                    (entry.Mode & UnixFileMode.UserExecute) == 0 ||
                    (entry.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    throw new ReleaseToolException("The installer executable entry has an invalid size or mode.");
                }
                var header = new byte[20];
                entry.DataStream.ReadExactly(header);
                var expectedMachine = architecture switch
                {
                    "x64" => (ushort)0x3e,
                    "arm64" => (ushort)0xb7,
                    _ => throw new ReleaseToolException("The installer architecture is unsupported.")
                };
                if (!header.AsSpan(0, 4).SequenceEqual("\u007fELF"u8) ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)) != expectedMachine)
                {
                    throw new ReleaseToolException("The installer executable ELF identity does not match its signed architecture.");
                }
            }
            else if (entry.Length is <= 0 or > 8 * 1024 * 1024 ||
                     (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute |
                                    UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new ReleaseToolException("The installer notices entry has an invalid size or mode.");
            }
        }
        if (expected.Count != 0)
        {
            throw new ReleaseToolException("The installer archive omits required files.");
        }
    }

    private static async Task VerifySignatureAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--input", "--signature", "--public-key");
        var input = RequireExistingFile(options, "--input");
        var signaturePath = RequireExistingFile(options, "--signature");
        var trustRoot = options.ContainsKey("--public-key")
            ? DistributionTrustRoot.FromPem(await File.ReadAllTextAsync(RequireExistingFile(options, "--public-key"), cancellationToken).ConfigureAwait(false))
            : DistributionTrustRoot.Production;
        var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
        var signatureText = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var signatureValue = Encoding.ASCII.GetString(signatureText);
        if (signatureValue.EndsWith("\r\n", StringComparison.Ordinal))
        {
            signatureValue = signatureValue[..^2];
        }
        else if (signatureValue.EndsWith('\n'))
        {
            signatureValue = signatureValue[..^1];
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureValue);
        }
        catch (FormatException exception)
        {
            throw new ReleaseToolException("The detached signature is not base64.", exception);
        }
        if (signature.Length != 64 || Convert.ToBase64String(signature) != signatureValue)
        {
            throw new ReleaseToolException("The detached signature is not a canonical 64-byte P1363 signature.");
        }
        using var key = trustRoot.CreateVerifier();
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new ReleaseToolException("The detached signature is invalid or untrusted.");
        }
    }

    private static async Task VerifyIndexAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--index", "--signature", "--public-key");
        var indexPath = RequireExistingFile(options, "--index");
        var signaturePath = RequireExistingFile(options, "--signature");
        var trustRoot = options.ContainsKey("--public-key")
            ? DistributionTrustRoot.FromPem(await File.ReadAllTextAsync(RequireExistingFile(options, "--public-key"), cancellationToken).ConfigureAwait(false))
            : DistributionTrustRoot.Production;
        var indexBytes = await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var index = DistributionVerifier.VerifyIndex(indexBytes, signatureBytes, trustRoot);
        await Console.Out.WriteLineAsync($"Verified {index.Train} release index sequence {index.Sequence}.").ConfigureAwait(false);
    }

    private static async Task ListAssetsAsync(
        Dictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        RejectUnknown(options, "--manifest", "--signature", "--public-key");
        var manifestPath = RequireExistingFile(options, "--manifest");
        var signaturePath = RequireExistingFile(options, "--signature");
        var trustRoot = options.ContainsKey("--public-key")
            ? DistributionTrustRoot.FromPem(await File.ReadAllTextAsync(RequireExistingFile(options, "--public-key"), cancellationToken).ConfigureAwait(false))
            : DistributionTrustRoot.Production;
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var manifest = DistributionVerifier.VerifyManifest(manifestBytes, signatureBytes, trustRoot);
        foreach (var artifact in manifest.Artifacts.OrderBy(static artifact => artifact.AssetName, StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync($"{artifact.AssetName}\t{artifact.Length.ToString(CultureInfo.InvariantCulture)}")
                .ConfigureAwait(false);
        }
    }

    private static async Task<List<DistributionArtifact>> CreateArtifactsAsync(
        string root,
        IEnumerable<(DistributionArtifactRole Role, string Name, string MediaType, string? Os, string? Architecture)> inputs,
        CancellationToken cancellationToken)
    {
        var result = new List<DistributionArtifact>();
        foreach (var input in inputs)
        {
            result.Add(await CreateArtifactAsync(
                root, input.Role, input.Name, input.MediaType, input.Os, input.Architecture, cancellationToken)
                .ConfigureAwait(false));
        }
        return result;
    }

    private static async Task<DistributionArtifact> CreateArtifactAsync(
        string root,
        DistributionArtifactRole role,
        string name,
        string mediaType,
        string? operatingSystem,
        string? architecture,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, name);
        return new DistributionArtifact(
            role,
            name,
            mediaType,
            new FileInfo(path).Length,
            await Sha256Async(path, cancellationToken).ConfigureAwait(false),
            operatingSystem,
            architecture);
    }

    private static async Task WriteChecksumsAsync(
        string root,
        IReadOnlyList<DistributionArtifact> artifacts,
        string name,
        CancellationToken cancellationToken)
    {
        var text = string.Concat(artifacts.OrderBy(static value => value.AssetName, StringComparer.Ordinal)
            .Select(static value => $"{value.Sha256}  {value.AssetName}\n"));
        await File.WriteAllTextAsync(Path.Combine(root, name), text, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task WriteManifestAsync(
        string root,
        string name,
        DistributionReleaseManifest manifest,
        CancellationToken cancellationToken)
        => WriteJsonAsync(
            Path.Combine(root, name),
            manifest,
            DistributionJsonContext.Default.DistributionReleaseManifest,
            cancellationToken);

    private static async Task WriteInstallerArchiveAsync(
        string executable,
        string notices,
        string output,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        RefuseExisting(output);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true);
        await WriteTarFileAsync(tar, executable, "hvo-skymonitor", 0x1ED, timestamp, cancellationToken).ConfigureAwait(false);
        await WriteTarFileAsync(tar, notices, "THIRD-PARTY-NOTICES.md", 0x1A4, timestamp, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteCatalogArchiveAsync(
        string bundle,
        string output,
        string packageVersion,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var expected = new[] { "ATTRIBUTION-HYG.md", "LICENSE-HYG.md", "hyg_v42.sqlite", "manifest.json" };
        var actual = Directory.EnumerateFiles(bundle).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ReleaseToolException("The catalog bundle does not contain the exact release file set.");
        }
        RefuseExisting(output);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var name in expected)
        {
            await WriteTarFileAsync(
                tar,
                Path.Combine(bundle, name),
                $"{packageVersion}.bundle/{name}",
                0x124,
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTarFileAsync(
        TarWriter tar,
        string source,
        string name,
        int mode,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(source);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream,
            Gid = 0,
            GroupName = "root",
            Mode = (UnixFileMode)mode,
            ModificationTime = timestamp,
            Uid = 0,
            UserName = "root"
        };
        await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the release's file-level SPDX 2.3 document, shared by the installer, catalog, and image trains.
    /// SPDX 2.3 requires a SHA-1 checksum on every file (clause 8.4) and, for a package that declares
    /// <c>filesAnalyzed</c>, the package verification code derived from those SHA-1 values (clause 7.9). The
    /// document previously declared <c>filesAnalyzed: true</c> while supplying neither, which no SPDX consumer can
    /// validate, so both are produced here from one pass over each file rather than reading every archive twice.
    /// </summary>
    private static async Task WriteSpdxAsync(
        string output,
        string name,
        string version,
        IReadOnlyList<string> files,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        var identifiers = new List<string>();
        var sha1Values = new List<string>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var (sha1, sha256) = await FileChecksumsAsync(file, cancellationToken).ConfigureAwait(false);
            var identifier = $"SPDXRef-File-{entries.Count + 1}";
            identifiers.Add(identifier);
            sha1Values.Add(sha1);
            entries.Add(new
            {
                fileName = "./" + Path.GetFileName(file),
                SPDXID = identifier,
                checksums = new[]
                {
                    new { algorithm = "SHA1", checksumValue = sha1 },
                    new { algorithm = "SHA256", checksumValue = sha256 }
                },
                licenseConcluded = "NOASSERTION",
                copyrightText = "NOASSERTION"
            });
        }
        await WriteJsonAsync(output, new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            documentNamespace = $"https://github.com/{Repository}/spdx/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}",
            name,
            // SPDX 2.3 clause 6.9 fixes the creation timestamp at YYYY-MM-DDThh:mm:ssZ. Serializing the
            // DateTimeOffset directly emits a "+00:00" offset, which the format's own schema rejects.
            creationInfo = new { created = SpdxTimestamp(createdUtc), creators = SpdxCreators },
            packages = new[]
            {
                new
                {
                    name,
                    SPDXID = SpdxPackageIdentifier,
                    versionInfo = version,
                    downloadLocation = "NOASSERTION",
                    filesAnalyzed = true,
                    packageVerificationCode = new { packageVerificationCodeValue = PackageVerificationCode(sha1Values) },
                    licenseConcluded = "NOASSERTION",
                    licenseDeclared = "NOASSERTION",
                    copyrightText = "NOASSERTION",
                    hasFiles = identifiers.ToArray()
                }
            },
            files = entries,
            documentDescribes = new[] { SpdxPackageIdentifier },
            relationships = new[]
            {
                new
                {
                    spdxElementId = "SPDXRef-DOCUMENT",
                    relationshipType = "DESCRIBES",
                    relatedSpdxElement = SpdxPackageIdentifier
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string SpdxTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Computes the SPDX package verification code defined by SPDX 2.3 clause 7.9: the SHA-1 of the concatenated,
    /// lexically sorted, lowercase-hexadecimal SHA-1 values of every file the package analyzed. SHA-1 is the
    /// algorithm the format mandates for this value; it carries no security claim here, and the release's own
    /// integrity rests on the SHA-256 identities in the signed manifest.
    /// </summary>
#pragma warning disable CA5350 // SPDX 2.3 defines the package verification code and file checksum as SHA-1.
    private static string PackageVerificationCode(IEnumerable<string> fileSha1Values)
        => Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(fileSha1Values.Order(StringComparer.Ordinal)))));

    private static async Task<(string Sha1, string Sha256)> FileChecksumsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
        }
        return (Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }
#pragma warning restore CA5350

    private static async Task WriteJsonAsync<T>(string output, T value, CancellationToken cancellationToken)
    {
        RefuseExisting(output);
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string output,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        RefuseExisting(output);
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(args[index], args[index + 1]))
            {
                throw new ReleaseToolException("Release-tool options must be unique --name value pairs.");
            }
        }
        return options;
    }

    private static void RejectUnknown(IReadOnlyDictionary<string, string> options, params string[] allowed)
    {
        var known = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = options.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null)
        {
            throw new ReleaseToolException($"Unknown option '{unknown}'.");
        }
    }

    private static string PrepareOutput(IReadOnlyDictionary<string, string> options)
    {
        var output = Path.GetFullPath(Require(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new ReleaseToolException($"Release output '{output}' already exists.");
        }
        Directory.CreateDirectory(output);
        return output;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ReleaseToolException($"{name} is required.");

    private static string RequireExistingFile(IReadOnlyDictionary<string, string> options, string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        return File.Exists(path) ? path : throw new ReleaseToolException($"{name} must identify an existing file.");
    }

    private static string RequireExistingDirectory(IReadOnlyDictionary<string, string> options, string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        return Directory.Exists(path) ? path : throw new ReleaseToolException($"{name} must identify an existing directory.");
    }

    private static string RequireGitOid(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return value.Length == 40 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ReleaseToolException($"{name} must be a lowercase 40-character Git object ID.");
    }

    private static string RequireSafeIdentifier(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        ValidateSafeIdentifier(value, name);
        return value;
    }

    private static void ValidateSafeIdentifier(string value, string name)
    {
        if (value.Length is < 1 or > 128 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ReleaseToolException($"{name} must be a safe release identifier containing only ASCII letters, digits, periods, and hyphens.");
        }
    }

    private static DateTimeOffset RequireUtc(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Require(options, name);
        return value.EndsWith('Z') && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new ReleaseToolException($"{name} must be an ISO-8601 UTC timestamp.");
    }

    private static void ValidateElf(string path, ushort expectedMachine, string architecture)
    {
        Span<byte> header = stackalloc byte[20];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("\u007fELF"u8) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]) != expectedMachine)
        {
            throw new ReleaseToolException($"The {architecture} installer does not have the expected ELF identity.");
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static DistributionSigningIdentity SigningIdentity(IReadOnlyDictionary<string, string> options)
    {
        var keyId = options.TryGetValue("--signing-key-id", out var configured)
            ? configured
            : DistributionTrustRoot.ProductionKeyId;
        if (keyId.Length != 76 || !keyId.StartsWith("p256-sha256:", StringComparison.Ordinal) ||
            keyId[12..].Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ReleaseToolException("--signing-key-id must be a canonical P-256 public-key fingerprint.");
        }
        return new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, keyId);
    }

    private static void RefuseExisting(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ReleaseToolException($"Release output '{path}' already exists.");
        }
    }
}

internal sealed class ReleaseToolException : Exception
{
    public ReleaseToolException()
    {
    }

    public ReleaseToolException(string message)
        : base(message)
    {
    }

    public ReleaseToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
