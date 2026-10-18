using System.Diagnostics;
using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Reading a release from its folder: release.json is the file SHA256SUMS names, the two list the same files with the
/// same SHA-256, the image is selected for the platform that will run it, and an offline archive is the size and SHA-256
/// release.json gives it. Every other case fails closed with the reason, before Docker is asked anything.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReleaseAcquirerTests
{
    private static readonly string HostPlatform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_Archive_SelectsThisHostsArchiveAndVerifiesIt()
    {
        using var release = ReleaseFixture.Create();

        var image = (await ReleaseAcquirer.AcquireImageAsync(Request(release.Folder), CancellationToken.None))!;

        Assert.AreEqual(ReleaseImageSource.Archive, image.Source);
        Assert.AreEqual(release.ArchivePath(HostPlatform), image.ArchivePath);
        Assert.AreEqual((string)release.Archive(HostPlatform)["sha256"]!, image.ArchiveSha256);
        // The archive is loaded by its classic-store image ID; the selection lists every identity Docker may give it.
        Assert.AreEqual(release.ConfigDigest(HostPlatform), image.ImageReference);
        CollectionAssert.AreEqual(
            new[] { release.Digest, release.ManifestDigest(HostPlatform), release.ConfigDigest(HostPlatform) },
            image.Image.ImageIds!.ToArray());
        Assert.AreEqual(new ReleaseImageSelection(
            $"v{release.Version}", release.Version, ReleaseFixture.Commit, ReleaseFixture.Created, HostPlatform, release.DigestReference,
            image.Image.ImageIds), image.Image);
        Assert.AreEqual(release.Version, image.Release.Version);
        Assert.AreEqual(Sha256(Path.Combine(release.Folder, "release.json")), image.VerifiedRelease.ManifestSha256);
        Assert.AreEqual(Sha256(Path.Combine(release.Folder, "SHA256SUMS")), image.VerifiedRelease.ChecksumsSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_Registry_PullsTheDigestReferenceAndNeedsNoArchive()
    {
        using var release = ReleaseFixture.Create();
        File.Delete(release.ArchivePath(HostPlatform));

        var image = (await ReleaseAcquirer.AcquireImageAsync(
            Request(release.Folder) with { ImageSource = ReleaseImageSource.Registry }, CancellationToken.None))!;

        Assert.AreEqual(ReleaseImageSource.Registry, image.Source);
        Assert.AreEqual(release.DigestReference, image.ImageReference);
        Assert.IsNull(image.ArchivePath);
        Assert.IsNull(image.ArchiveSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_RegistryReleaseWithoutAnArchiveForThePlatform_RequiresOnlyTheDigest()
    {
        using var release = ReleaseFixture.Create(archivePlatforms: []);

        var image = (await ReleaseAcquirer.AcquireImageAsync(
            Request(release.Folder) with { ImageSource = ReleaseImageSource.Registry }, CancellationToken.None))!;

        // Without an archive the release names no per-platform identity, so Docker's repository digest alone binds it.
        Assert.IsNull(image.Image.ImageIds);
        Assert.AreEqual(release.DigestReference, image.ImageReference);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AcquireImageAsync_RecordedDaemon_SelectsItsArchitectureNotTheProcesses()
    {
        var daemon = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var release = ReleaseFixture.Create();

        var image = (await ReleaseAcquirer.AcquireImageAsync(release.Folder, null, daemon, CancellationToken.None))!;

        Assert.AreEqual($"linux/{daemon}", image.Image.Platform);
        Assert.AreEqual(release.ArchivePath($"linux/{daemon}"), image.ArchivePath);
    }

    [TestMethod]
    public async Task AcquireImageAsync_NoRelease_SelectsNothing()
    {
        Assert.IsNull(await ReleaseAcquirer.AcquireImageAsync(Request(null), CancellationToken.None));
        Assert.IsNull(await ReleaseAcquirer.AcquireImageAsync(null, null, "amd64", CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("folder-missing", "The release folder {folder} does not exist.")]
    [DataRow("release-json-missing", "The release folder {folder} has no release.json.")]
    [DataRow("checksums-missing", "The release folder {folder} has no SHA256SUMS.")]
    [DataRow("checksums-without-release-json", "SHA256SUMS does not list release.json.")]
    [DataRow("release-json-changed", "release.json does not match its SHA-256 in SHA256SUMS: it is not the release.json the release published.")]
    [DataRow("checksums-extra-file", "SHA256SUMS lists extra.bin, which release.json does not.")]
    [DataRow("checksums-missing-asset", "SHA256SUMS does not list hvo-sky-install-linux-arm64, which release.json lists.")]
    [DataRow("checksums-disagree", "release.json and SHA256SUMS give hvo-sky-install-linux-arm64 different SHA-256s.")]
    [DataRow("checksums-malformed", "The installer will not use SHA256SUMS: line 1 is not a SHA-256 and a release file name.")]
    [DataRow("schema-unknown", "The installer will not use release.json: it is schema 2, and this installer reads schema 1.")]
    [DataRow("digest-malformed", "The installer will not use release.json: the skymonitor-cameraagent image's digest is not sha256:<64 lowercase hex digits>.")]
    [DataRow("platform-unpublished", "Release v1.2.0-dev.3 publishes the CameraAgent image for {other} and does not support this host's {host} architecture.")]
    [DataRow("archive-missing", "The offline archive {archive} is not in {folder}. Copy it there from the release, or pass --image-source registry to pull {reference} instead.")]
    [DataRow("archive-unlisted", "Release v1.2.0-dev.3 has no offline archive for {host}. Pass --image-source registry to pull {reference} instead.")]
    [DataRow("archive-truncated", "{archive} is {length} bytes, and release.json gives it {size}: it is not the file the release published.")]
    [DataRow("archive-changed", "{archive} does not match its SHA-256 in release.json and SHA256SUMS: it is not the file the release published.")]
    [DataRow("release-json-oversized", "The installer will not use release.json: it is larger than 1048576 bytes.")]
    [DataRow("release-json-symlink", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("release-json-directory", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("release-json-fifo", "Release file '{folder}/release.json' is not a regular file.")]
    [DataRow("archive-symlink", "Release file '{folder}/{archive}' is not a regular file.")]
    [DataRow("release-json-hard-link", "Input file '{folder}/release.json' must not have hard links.")]
    public async Task AcquireImageAsync_InvalidRelease_FailsClosedWithTheReason(string fault, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var other = HostPlatform == "linux/amd64" ? "linux/arm64" : "linux/amd64";
        using var release = ReleaseFixture.Create(
            platforms: fault == "platform-unpublished" ? [other] : null,
            archivePlatforms: fault == "archive-unlisted" ? [] : null);
        var archive = fault is "platform-unpublished" or "archive-unlisted" ? null : release.ArchivePath(HostPlatform);
        var folder = release.Folder;
        var outside = Path.Combine(Path.GetDirectoryName(folder)!, "outside");
        var length = 0L;
        switch (fault)
        {
            case "folder-missing": folder = Path.Combine(folder, "absent"); break;
            case "release-json-missing": File.Delete(Path.Combine(folder, "release.json")); break;
            case "checksums-missing": File.Delete(Path.Combine(folder, "SHA256SUMS")); break;
            case "checksums-without-release-json":
                release.WriteChecksums(string.Concat(release.ReadChecksums().Split('\n')
                    .Where(static line => line.Length > 0 && !line.EndsWith("  release.json", StringComparison.Ordinal))
                    .Select(static line => line + "\n")));
                break;
            case "release-json-changed": await File.AppendAllTextAsync(Path.Combine(folder, "release.json"), " "); break;
            case "checksums-extra-file": release.WriteChecksums(release.ReadChecksums() + $"{new string('e', 64)}  extra.bin\n"); break;
            case "checksums-missing-asset":
                release.WriteChecksums(string.Concat(release.ReadChecksums().Split('\n')
                    .Where(static line => line.Length > 0 && !line.EndsWith("  hvo-sky-install-linux-arm64", StringComparison.Ordinal))
                    .Select(static line => line + "\n")));
                break;
            case "checksums-disagree":
                var sums = release.ReadChecksums();
                var installerLine = sums.Split('\n').Single(static line => line.EndsWith("  hvo-sky-install-linux-arm64", StringComparison.Ordinal));
                release.WriteChecksums(sums.Replace(installerLine, $"{new string('0', 64)}  hvo-sky-install-linux-arm64", StringComparison.Ordinal));
                break;
            case "checksums-malformed": release.WriteChecksums(release.ReadChecksums().Replace("  ", " *", StringComparison.Ordinal)); break;
            case "schema-unknown": release.Rewrite(static root => root["schemaVersion"] = 2); break;
            case "digest-malformed":
                release.Rewrite(static root => root["images"]![ReleaseFixture.ImageKey]!["digest"] = "sha256:short");
                break;
            case "archive-missing": File.Delete(archive!); break;
            case "archive-truncated":
                await File.WriteAllTextAsync(archive!, "short");
                length = 5;
                break;
            case "archive-changed":
                var bytes = await File.ReadAllBytesAsync(archive!);
                bytes[0] ^= 0xFF;
                await File.WriteAllBytesAsync(archive!, bytes);
                break;
            case "release-json-oversized":
                await File.WriteAllBytesAsync(Path.Combine(folder, "release.json"), new byte[ReleaseManifest.MaximumLength + 1]);
                break;
            case "release-json-symlink":
                File.Move(Path.Combine(folder, "release.json"), outside);
                File.CreateSymbolicLink(Path.Combine(folder, "release.json"), outside);
                break;
            case "release-json-directory":
                File.Delete(Path.Combine(folder, "release.json"));
                Directory.CreateDirectory(Path.Combine(folder, "release.json"));
                break;
            case "release-json-fifo":
                File.Delete(Path.Combine(folder, "release.json"));
                using (var mkfifo = Process.Start("mkfifo", Path.Combine(folder, "release.json")))
                {
                    await mkfifo.WaitForExitAsync();
                    Assert.AreEqual(0, mkfifo.ExitCode);
                }
                break;
            case "archive-symlink":
                File.Move(archive!, outside);
                File.CreateSymbolicLink(archive!, outside);
                break;
            case "release-json-hard-link": NativeLinux.CreateHardLink(Path.Combine(folder, "release.json"), outside); break;
            case "platform-unpublished" or "archive-unlisted": break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
        var expected = message
            .Replace("{folder}", folder, StringComparison.Ordinal)
            .Replace("{archive}", archive is null ? string.Empty : Path.GetFileName(archive), StringComparison.Ordinal)
            .Replace("{reference}", release.DigestReference, StringComparison.Ordinal)
            .Replace("{host}", HostPlatform, StringComparison.Ordinal)
            .Replace("{other}", other, StringComparison.Ordinal)
            .Replace("{length}", length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{size}", archive is null ? string.Empty : ((int)release.Archive(HostPlatform)["size"]!).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => ReleaseAcquirer.AcquireImageAsync(Request(folder), CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
        Assert.IsTrue(exception.IsRefusal, "the release is checked before Docker is contacted, so it is a refusal (exit 3)");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ToEvidence_RecordsTheRunningImageOnlyWhenTheReleaseNamesIt()
    {
        using var release = ReleaseFixture.Create();
        var image = (await ReleaseAcquirer.AcquireImageAsync(Request(release.Folder), CancellationToken.None))!;
        var running = new ImageInstallationIdentity(
            "archive", release.Digest, release.Digest, ReleaseAcquirer.HostImageArchitecture(), image.ArchiveSha256);

        var evidence = image.ToEvidence(running);

        Assert.AreEqual(DeploymentSchemaVersions.ImageReleaseEvidence, evidence.SchemaVersion);
        Assert.AreEqual(release.Version, evidence.ReleaseVersion);
        Assert.AreEqual(release.Digest, evidence.ImageId);
        Assert.AreEqual("archive", evidence.Source);
        var stranger = running with { ImageId = $"sha256:{new string('d', 64)}" };
        var exception = Assert.ThrowsExactly<InstallerException>(() => image.ToEvidence(stranger));
        Assert.AreEqual("The running CameraAgent image is not the image the release names.", exception.Message);
    }

    private static InstallRequest Request(string? release) => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "owner@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        Release = release
    };

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
