using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>How a release's CameraAgent image reaches Docker.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReleaseImageSource>))]
internal enum ReleaseImageSource
{
    /// <summary>Loaded from the release's offline archive for the platform, in the release's folder. Nothing is pulled.</summary>
    Archive,

    /// <summary>Pulled from its registry by the digest release.json names.</summary>
    Registry
}

/// <summary>
/// Reads a release from the folder holding its files (install contract section 4) and selects its CameraAgent image for
/// the platform that will run it. The trust model is the contract's: release.json must be the file SHA256SUMS names, and
/// the two must list the same files with the same SHA-256; the image is pinned to the digest release.json names; and an
/// offline archive must be the size and SHA-256 both give it, and must hold the release's image for the platform,
/// labelled with the release's version and commit (<see cref="ReleaseImageArchive"/>). SHA256SUMS itself is checked
/// against its build-provenance attestation by <c>install.sh</c> or the operator (<c>gh attestation verify</c>) before
/// the installer runs. Every refusal here happens before Docker is asked to load, pull or start anything; a pulled
/// image, which can only be read once Docker holds it, is checked before the install or upgrade claims anything
/// (<see cref="DockerClient.PullReleaseImageAsync"/>).
/// </summary>
internal static class ReleaseAcquirer
{
    private const string ThisHostAuthority = "this host's";
    private const string RecordedDaemonAuthority = "the instance's recorded Docker daemon's";

    /// <summary>
    /// The release's CameraAgent image for the architecture this host executes, which an install runs it on. Returns
    /// <see langword="null"/> when the request names no release.
    /// </summary>
    public static Task<AcquiredImage?> AcquireImageAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AcquireAsync(
            request.Release, request.ImageSource ?? ReleaseImageSource.Archive, HostImageArchitecture(), ThisHostAuthority,
            cancellationToken);
    }

    /// <summary>
    /// The release's CameraAgent image for the architecture of the Docker daemon an instance was installed against, which
    /// an upgrade runs it on, rather than the architecture of the process running the installer. Returns
    /// <see langword="null"/> when <paramref name="release"/> is <see langword="null"/>.
    /// </summary>
    public static Task<AcquiredImage?> AcquireImageAsync(
        string? release,
        ReleaseImageSource? source,
        string daemonArchitecture,
        CancellationToken cancellationToken)
        => release is null
            ? Task.FromResult<AcquiredImage?>(null)
            : AcquireAsync(
                release, source ?? ReleaseImageSource.Archive, DaemonImageArchitecture(daemonArchitecture),
                RecordedDaemonAuthority, cancellationToken);

    private static async Task<AcquiredImage?> AcquireAsync(
        string? folder,
        ReleaseImageSource source,
        string architecture,
        string selectingAuthority,
        CancellationToken cancellationToken)
    {
        if (folder is null)
        {
            return null;
        }
        var release = await ReadAsync(folder, cancellationToken).ConfigureAwait(false);
        var manifest = release.Manifest;
        var image = manifest.CameraAgent!;
        var platform = $"linux/{architecture}";
        if (!image.Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw new InstallerException(
                $"Release {manifest.Tag} publishes the CameraAgent image for {string.Join(", ", image.Platforms)} " +
                $"and does not support {selectingAuthority} {platform} architecture.");
        }
        var archive = image.ArchiveFor(platform);
        var selection = new ReleaseImageSelection(
            manifest.Tag, manifest.Version, manifest.Commit, platform, image.DigestReference,
            archive is null ? null : [image.Digest, archive.ManifestDigest, archive.ConfigDigest]);
        if (source == ReleaseImageSource.Registry)
        {
            return new AcquiredImage(release, selection, source, image.DigestReference, null, null);
        }
        if (archive is null)
        {
            throw new InstallerException(
                $"Release {manifest.Tag} has no offline archive for {platform}. Pass --image-source registry to pull " +
                $"{image.DigestReference} instead.");
        }
        var path = Path.Combine(folder, archive.Name);
        ImageInstallationIdentity archivedImage;
        await using (var stream = OpenReleaseFile(
                         path,
                         $"The offline archive {archive.Name} is not in {folder}. Copy it there from the release, or pass " +
                         $"--image-source registry to pull {image.DigestReference} instead."))
        {
            await VerifyOpenFileAsync(stream, archive.Name, archive.Size, archive.Sha256, cancellationToken).ConfigureAwait(false);
            // The image it holds is read from the same open file, before Docker is asked to load anything, so an archive
            // that is the release's file but not the release's image is refused while nothing has been claimed.
            stream.Position = 0;
            archivedImage = await ReleaseImageArchive.ReadAsync(stream, manifest, image, archive, cancellationToken)
                .ConfigureAwait(false);
        }
        // The archive is loaded by its classic-store ID, the config digest; Docker's containerd store names the loaded
        // image by its index digest instead, which the selection's other identities cover.
        return new AcquiredImage(release, selection, source, archive.ConfigDigest, path, archive.Sha256)
        {
            ArchivedImage = archivedImage
        };
    }

    /// <summary>
    /// Reads release.json and SHA256SUMS from the release's folder and checks each against the other: SHA256SUMS lists
    /// release.json with its SHA-256, and exactly the files release.json lists, each with the SHA-256 release.json gives it.
    /// </summary>
    internal static async Task<VerifiedRelease> ReadAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            throw new InstallerException($"The release folder {folder} does not exist.");
        }
        var manifestBytes = await ReadBoundedAsync(folder, ReleaseManifest.FileName, ReleaseManifest.MaximumLength, cancellationToken)
            .ConfigureAwait(false);
        var checksumBytes = await ReadBoundedAsync(folder, ReleaseChecksums.FileName, ReleaseChecksums.MaximumLength, cancellationToken)
            .ConfigureAwait(false);
        var checksums = ReleaseChecksums.Parse(checksumBytes);
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (!checksums.Files.TryGetValue(ReleaseManifest.FileName, out var listed))
        {
            throw new InstallerException($"{ReleaseChecksums.FileName} does not list {ReleaseManifest.FileName}.");
        }
        if (listed != manifestSha256)
        {
            throw new InstallerException(
                $"{ReleaseManifest.FileName} does not match its SHA-256 in {ReleaseChecksums.FileName}: it is not the " +
                "release.json the release published.");
        }
        var manifest = ReleaseManifest.Parse(manifestBytes);
        foreach (var asset in manifest.Assets)
        {
            if (!checksums.Files.TryGetValue(asset.Name, out var sum))
            {
                throw new InstallerException(
                    $"{ReleaseChecksums.FileName} does not list {asset.Name}, which {ReleaseManifest.FileName} lists.");
            }
            if (sum != asset.Sha256)
            {
                throw new InstallerException(
                    $"{ReleaseManifest.FileName} and {ReleaseChecksums.FileName} give {asset.Name} different SHA-256s.");
            }
        }
        foreach (var name in checksums.Files.Keys)
        {
            if (name != ReleaseManifest.FileName && !manifest.Assets.Any(asset => asset.Name == name))
            {
                throw new InstallerException(
                    $"{ReleaseChecksums.FileName} lists {name}, which {ReleaseManifest.FileName} does not.");
            }
        }
        return new VerifiedRelease(
            manifest, manifestSha256, Convert.ToHexStringLower(SHA256.HashData(checksumBytes)));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string folder,
        string name,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, name);
        await using var stream = OpenReleaseFile(path, $"The release folder {folder} has no {name}.");
        // Read at most one byte past the bound, so an oversized file is refused without being read whole.
        var buffer = new byte[maximumLength + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (length > maximumLength)
        {
            throw new InstallerException($"The installer will not use {name}: it is larger than {maximumLength} bytes.");
        }
        return buffer[..length];
    }

    private static async Task VerifyOpenFileAsync(
        FileStream stream,
        string name,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (stream.Length != expectedLength)
        {
            throw new InstallerException(
                $"{name} is {stream.Length} bytes, and {ReleaseManifest.FileName} gives it {expectedLength}: it is not " +
                "the file the release published.");
        }
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (actual != expectedSha256)
        {
            throw new InstallerException(
                $"{name} does not match its SHA-256 in {ReleaseManifest.FileName} and {ReleaseChecksums.FileName}: it is " +
                "not the file the release published.");
        }
    }

    /// <summary>
    /// Opens a release file only when it is a regular file reached without a symbolic link and with no other hard link, so
    /// a link or a FIFO planted under a release file's name is refused rather than followed or blocked on.
    /// </summary>
    private static FileStream OpenReleaseFile(string path, string missing)
    {
        var node = NativeLinux.TryGetNodeIdentity(path) ?? throw new InstallerException(missing);
        if (node.Type != NativeLinux.RegularFileNodeType)
        {
            throw new InstallerException($"Release file '{path}' is not a regular file.");
        }
        try
        {
            return SafeFileSystem.OpenRegularFileRead(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerException($"Release file '{path}' could not be read.", exception);
        }
    }

    /// <summary>
    /// The image architecture an instance's recorded Docker daemon executes, named the way an OCI platform names it. The
    /// installer records the daemon's architecture already normalized, but an edited or foreign manifest is refused here
    /// rather than being matched against nothing.
    /// </summary>
    internal static string DaemonImageArchitecture(string daemonArchitecture) => daemonArchitecture switch
    {
        "amd64" or "x86_64" => "amd64",
        "arm64" or "aarch64" => "arm64",
        var other => throw new InstallerException(
            $"CameraAgent images are published for linux/amd64 and linux/arm64; the instance's recorded Docker daemon reports '{other}'.")
    };

    /// <summary>The image architecture this host can execute, named the way an OCI platform names it.</summary>
    internal static string HostImageArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var other => throw new InstallerException(
            $"CameraAgent images are published for linux/amd64 and linux/arm64; this host reports {other}.")
    };
}

/// <summary>A release.json checked against SHA256SUMS, with the SHA-256 of each as read.</summary>
internal sealed record VerifiedRelease(ReleaseManifest Manifest, string ManifestSha256, string ChecksumsSha256);

/// <summary>
/// What Docker must resolve for a release's CameraAgent image on one platform: an image carrying the release's version
/// and commit, and, when the release has an offline archive for the platform, one of the identities that archive's
/// image has: the index digest (Docker's containerd image store), the platform manifest's, or the config digest (the
/// classic store's image ID).
/// </summary>
internal sealed record ReleaseImageSelection(
    string ReleaseTag,
    string Version,
    string Commit,
    string Platform,
    string DigestReference,
    IReadOnlyList<string>? ImageIds);

/// <summary>A release's CameraAgent image selected for one platform, and how it reaches Docker.</summary>
internal sealed record AcquiredImage(
    VerifiedRelease VerifiedRelease,
    ReleaseImageSelection Image,
    ReleaseImageSource Source,
    string ImageReference,
    string? ArchivePath,
    string? ArchiveSha256)
{
    public const string EvidenceFileName = "image-distribution.json";

    /// <summary>
    /// The image an offline archive holds, read from the archive before anything is loaded; <see langword="null"/> for a
    /// pulled image, which can only be read once Docker holds it.
    /// </summary>
    public ImageInstallationIdentity? ArchivedImage { get; init; }

    /// <summary>The release the image belongs to.</summary>
    public ReleaseManifest Release => VerifiedRelease.Manifest;

    /// <summary>The record of this release, for the image the instance now runs.</summary>
    public CameraAgentImageReleaseEvidence ToEvidence(ImageInstallationIdentity running)
    {
        ArgumentNullException.ThrowIfNull(running);
        if (Image.ImageIds is { } identities && !identities.Contains(running.ImageId, StringComparer.Ordinal))
        {
            throw new InstallerException("The running CameraAgent image is not the image the release names.");
        }
        return new CameraAgentImageReleaseEvidence(
            DeploymentSchemaVersions.ImageReleaseEvidence,
            Release.Version,
            Release.Tag,
            Release.Commit,
            VerifiedRelease.ManifestSha256,
            VerifiedRelease.ChecksumsSha256,
            Image.DigestReference,
            Image.Platform,
            Source == ReleaseImageSource.Archive ? "archive" : "registry",
            ArchivePath is null ? null : Path.GetFileName(ArchivePath),
            ArchiveSha256,
            running.ImageId,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Retains the release record beside the other deployment evidence once the image it describes is the one the
    /// instance runs. Writing it earlier would leave a refused or reverted operation asserting a release the running
    /// container does not carry.
    /// </summary>
    public Task WriteEvidenceAsync(InstallationPaths paths, ImageInstallationIdentity running, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(paths.DeploymentStateRoot, EvidenceFileName),
            ToEvidence(running),
            DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence,
            cancellationToken);
    }

    /// <summary>
    /// Withdraws the retained record when the instance no longer runs the image it describes, so a rollback or a reverted
    /// upgrade never leaves evidence contradicting the running container.
    /// </summary>
    public static void RemoveEvidence(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = Path.Combine(paths.DeploymentStateRoot, EvidenceFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

}

/// <summary>
/// The release record retained beside an installed instance's deployment state (schema
/// <see cref="DeploymentSchemaVersions.ImageReleaseEvidence"/>): the release the running image came from, named by its
/// version, tag and commit and by the SHA-256 of the release.json and SHA256SUMS it was verified with; the image's
/// digest reference and platform; how it reached Docker; and the image ID Docker gave it. An upgrade reads the version
/// to show the right upgrade notes and to refuse an older target.
/// </summary>
internal sealed record CameraAgentImageReleaseEvidence(
    int SchemaVersion,
    string ReleaseVersion,
    string ReleaseTag,
    string ReleaseCommit,
    string ReleaseJsonSha256,
    string ChecksumsSha256,
    string DigestReference,
    string Platform,
    string Source,
    string? ArchiveName,
    string? ArchiveSha256,
    string ImageId,
    DateTimeOffset RecordedUtc);
