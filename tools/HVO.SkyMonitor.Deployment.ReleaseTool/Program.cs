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

internal static class Program
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
        if (metadataKind == "manifest")
        {
            _ = DistributionVerifier.VerifyManifest(bytes, signatureText, trustRoot);
        }
        else if (metadataKind == "index")
        {
            _ = DistributionVerifier.VerifyIndex(bytes, signatureText, trustRoot);
        }
        else
        {
            throw new ReleaseToolException("--metadata-kind must be manifest or index.");
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
        if (train is not ("installer" or "catalog"))
        {
            throw new ReleaseToolException("--train must be installer or catalog.");
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
        if (manifest.ManifestKind == DistributionManifestKind.CatalogRelease)
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

    private static async Task WriteSpdxAsync(
        string output,
        string name,
        string version,
        IReadOnlyList<string> files,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            entries.Add(new
            {
                fileName = Path.GetFileName(file),
                SPDXID = $"SPDXRef-File-{entries.Count + 1}",
                checksums = new[] { new { algorithm = "SHA256", checksumValue = await Sha256Async(file, cancellationToken).ConfigureAwait(false) } },
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
            creationInfo = new { created = createdUtc, creators = SpdxCreators },
            packages = new[] { new { name, SPDXID = "SPDXRef-Package", versionInfo = version, downloadLocation = "NOASSERTION", filesAnalyzed = true } },
            files = entries
        }, cancellationToken).ConfigureAwait(false);
    }

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
