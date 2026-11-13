using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>
/// Read-only local integrity rehearsal, never publisher authorization or permission to install. Uses the installer's
/// actual release/checksum/archive readers; no Docker, network, extraction, staging or instance state is involved.
/// </summary>
internal static class LogicHostCandidate
{
    internal const string MetadataLabel = "io.hvo.skymonitor.logichost-release";
    private const int MaximumMetadataLength = 16 * 1024;
    private const long MaximumCandidateBytes = 128L * 1024 * 1024 * 1024;
    private static readonly string[] MetadataFields =
    [
        "schemaVersion", "minimumVerifierVersion", "installationMode", "migrationId", "initializationTarget",
        "generatedSqlSha256", "seedManifestSha256", "configurationVersion", "objectLayoutVersion",
        "checkpointFormatVersion", "backupFormatVersion", "requiredCatalogId", "requiredPackageVersion"
    ];

    public static async Task<ReleaseManifest> VerifyAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        // AuthenticateFile refuses non-regular/multiply-linked leaves; also refuse linked directory ancestors.
        for (var ancestor = root; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.LinkTarget is not null)
                throw Invalid("the candidate directory traverses a symbolic link");
        }
        var manifestBytes = ReadMetadata(root.FullName, ReleaseManifest.FileName, ReleaseManifest.MaximumLength);
        var sums = ReleaseChecksums.Parse(ReadMetadata(root.FullName, ReleaseChecksums.FileName, ReleaseChecksums.MaximumLength));
        var release = ReleaseManifest.ParseLogicHost(manifestBytes);
        using var document = ReleaseJson.Parse(manifestBytes, ReleaseManifest.FileName);
        var envelope = document.RootElement;
        if (Text(envelope, "product") != Program.ReleaseProduct ||
            !envelope.TryGetProperty("images", out var images) || images.EnumerateObject().Count() != 1 ||
            !envelope.TryGetProperty("componentMetadata", out var components) || components.ValueKind != JsonValueKind.Object ||
            components.EnumerateObject().Count() != 1 || !components.TryGetProperty("logicHost", out var metadata))
            throw Invalid("it must describe only the LogicHost component of HVO SkyMonitor");
        ValidateMetadata(metadata);
        var image = release.LogicHost!;
        // This slice deliberately permits only the approved initial full-stack platform, with all its offline bytes.
        if (!image.Platforms.SequenceEqual(["linux/amd64"], StringComparer.Ordinal) || image.OfflineArchives.Count != 1)
            throw Invalid("the initial LogicHost candidate requires exactly one linux/amd64 offline archive");
        var expected = release.Assets.Select(static asset => asset.Name).Append(ReleaseManifest.FileName).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(sums.Files.Keys) || !sums.Files.TryGetValue(ReleaseManifest.FileName, out var manifestHash) ||
            manifestHash != Hash(manifestBytes))
            throw Invalid("SHA256SUMS must cover exactly release.json and every asset, with matching hashes");
        var entries = root.EnumerateFileSystemInfos().Take(ReleaseManifest.MaximumAssets + 3).ToArray();
        if (entries.Any(static entry => entry is not FileInfo || entry.LinkTarget is not null) ||
            !expected.Append(ReleaseChecksums.FileName).ToHashSet(StringComparer.Ordinal).SetEquals(entries.Select(static entry => entry.Name)))
            throw Invalid("the candidate must contain exactly its manifest, checksums and assets as regular files");
        long total = 0;
        foreach (var asset in release.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Size > MaximumCandidateBytes - total)
                throw Invalid("the candidate exceeds the 128 GiB local verification bound");
            total += asset.Size;
            await VerifyAssetAsync(root.FullName, asset, sums, release, metadata, cancellationToken).ConfigureAwait(false);
        }
        return release;
    }

    private static async Task VerifyAssetAsync(
        string root, ReleaseAsset asset, ReleaseChecksums sums, ReleaseManifest release, JsonElement metadata,
        CancellationToken cancellationToken)
    {
        using var input = Open(root, asset.Name);
        if (input.Stream.Length != asset.Size || sums.Files[asset.Name] != asset.Sha256 ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(input.Stream, cancellationToken).ConfigureAwait(false)) != asset.Sha256)
            throw Invalid("an asset's length or SHA-256 disagrees with release.json or SHA256SUMS");
        Revalidate(input);
        var archive = release.LogicHost!.OfflineArchives.Single();
        if (archive.Name != asset.Name)
            return;
        input.Stream.Position = 0;
        var labels = await ReleaseImageArchive.ReadLogicHostLabelsAsync(
            input.Stream, release, release.LogicHost, archive, cancellationToken).ConfigureAwait(false);
        if (!labels.TryGetProperty(MetadataLabel, out var label) || label.ValueKind != JsonValueKind.String ||
            label.GetString() is not { } text || text.Length > MaximumMetadataLength)
            throw Invalid("the image omits its bounded LogicHost compatibility declaration");
        using var declaration = ReleaseJson.Parse(System.Text.Encoding.UTF8.GetBytes(text), "the LogicHost image declaration");
        ValidateMetadata(declaration.RootElement);
        if (!JsonElement.DeepEquals(metadata, declaration.RootElement))
            throw Invalid("the LogicHost image and release metadata declare different compatibility identities");
        Revalidate(input);
    }

    private static void ValidateMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object || metadata.GetRawText().Length > MaximumMetadataLength ||
            !MetadataFields.ToHashSet(StringComparer.Ordinal).SetEquals(metadata.EnumerateObject().Select(static property => property.Name)))
            throw Invalid("LogicHost metadata must contain exactly the supported bounded fields");
        if (Integer(metadata, "schemaVersion") != 1 || Integer(metadata, "minimumVerifierVersion") != 1 ||
            Text(metadata, "installationMode") != "fresh-database-only")
            throw Invalid("the LogicHost metadata schema, verifier version or installation policy is unsupported");
        foreach (var name in new[] { "configurationVersion", "objectLayoutVersion", "checkpointFormatVersion", "backupFormatVersion" })
        {
            if (Integer(metadata, name) != 1)
                throw Invalid("the LogicHost candidate declares an unsupported configuration or object/recovery format");
        }
        var migration = Text(metadata, "migrationId");
        var target = Text(metadata, "initializationTarget");
        if (!Identifier(migration, 150) || !Identifier(target, 128) ||
            !ReleaseManifest.Sha256Pattern().IsMatch(Text(metadata, "generatedSqlSha256") ?? string.Empty) ||
            !ReleaseManifest.Sha256Pattern().IsMatch(Text(metadata, "seedManifestSha256") ?? string.Empty))
            throw Invalid("the LogicHost schema, initialization, SQL or seed identity is missing or malformed");
        _ = ReleaseCatalogs.Get(Text(metadata, "requiredCatalogId"), Text(metadata, "requiredPackageVersion"));
    }

    private static bool Identifier(string? text, int maximum)
        => text is { Length: > 0 } && text.Length <= maximum && char.IsAsciiLetterOrDigit(text[0]) &&
           !text.Contains("..", StringComparison.Ordinal) && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static int? Integer(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] ReadMetadata(string root, string name, int maximum)
    {
        using var input = Open(root, name);
        var bytes = new byte[maximum + 1];
        var length = input.Stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return length <= maximum ? bytes[..length] : throw Invalid("a candidate metadata file exceeds its size bound");
    }

    private static CatalogSnapshotResolver.AuthenticatedFile Open(string root, string name)
    {
        try
        {
            return CatalogSnapshotResolver.AuthenticateFile(Path.Combine(root, name), "Local candidate input");
        }
        catch (InvalidDataException exception)
        {
            throw new ReleaseToolException("A candidate input is not a regular singly linked file.", exception);
        }
    }

    private static void Revalidate(CatalogSnapshotResolver.AuthenticatedFile input)
    {
        try
        {
            CatalogSnapshotResolver.RevalidateFile(input, "Local candidate input");
        }
        catch (InvalidDataException exception)
        {
            throw new ReleaseToolException("A candidate input changed identity while it was being verified.", exception);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ReleaseToolException Invalid(string reason) => new($"Local LogicHost candidate refused: {reason}.");
}
