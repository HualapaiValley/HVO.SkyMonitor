using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo runtime's readiness (#1966, G2), the fence <c>silo-verify</c> reads back, and the install options of the
/// offline image and the optional archive.
/// </summary>
public sealed partial class SiloRuntimeTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly string[] InstallArguments =
    [
        "cameraagent", "install",
        "--friendly-name", "North Camera",
        "--catalog-bundle", "/srv/hvo/catalog.bundle",
        "--image-ref", "ghcr.io/example/cameraagent@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"
    ];

    [TestMethod]
    [DataRow(32L * GiB, 10L * GiB)]
    [DataRow(200L * GiB, 10L * GiB)]
    [DataRow(204L * GiB, 204L * GiB / 100 * 5)]
    [DataRow(1024L * GiB, 1024L * GiB / 100 * 5)]
    [DataRow(4096L * GiB, 4096L * GiB / 100 * 5)]
    public void Headroom_KeepsTheLargerOfTenGibibytesAndFivePercent(long total, long required)
    {
        Assert.AreEqual(required, SiloRuntimeProfile.RequiredFreeBytes(total));
        Assert.IsTrue(new SiloHeadroom(required, total, required).Ready);
        Assert.IsFalse(new SiloHeadroom(required - 1, total, required).Ready);
    }

    [TestMethod]
    public void Headroom_IsReadFromTheFilesystemThatWillHoldAnAbsentDataRoot()
    {
        var root = Directory.CreateTempSubdirectory("hvo-silo-headroom-");
        try
        {
            var headroom = SiloRuntimeProfile.HeadroomOf(Path.Combine(root.FullName, "instance", "silo"));
            var drive = new DriveInfo(root.FullName);
            Assert.AreEqual(drive.TotalSize, headroom.TotalBytes);
            Assert.AreEqual(SiloRuntimeProfile.RequiredFreeBytes(drive.TotalSize), headroom.RequiredBytes);
            Assert.IsGreaterThan(0L, headroom.AvailableBytes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true, null, null, "ready")]
    [DataRow(true, "Verified", "archive-verified", "ready")]
    [DataRow(true, "TemporarilyUnavailable", "archive-unreachable", "degraded")]
    [DataRow(true, "Refused", "archive-tls-untrusted", "not-ready")]
    [DataRow(false, null, null, "not-ready")]
    [DataRow(false, "TemporarilyUnavailable", "archive-timeout", "not-ready")]
    public void Readiness_IsNotReadyForLowHeadroomOrARefusedArchiveAndDegradedOnlyForAnUnreachableOne(
        bool headroomReady, string? archiveState, string? reason, string readiness)
    {
        SiloArchiveState? archive = archiveState is null ? null : Enum.Parse<SiloArchiveState>(archiveState);
        var headroom = headroomReady ? new SiloHeadroom(11 * GiB, 100 * GiB, 10 * GiB) : new SiloHeadroom(9 * GiB, 100 * GiB, 10 * GiB);
        var verification = new SiloVerification(headroom, "fence-version", "AKIAWRITER0001", false,
            archive is { } state ? new SiloArchiveObservation(state, reason!, null) : null);

        Assert.AreEqual(readiness, verification.Readiness);
    }

    [TestMethod]
    public void FenceWriter_NamesTheWriterKeyOnlyForThisInstanceBindingAndGeneration()
    {
        var instance = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2026-11-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var fence = SiloRuntimeProfile.Fence(instance, "AKIAEXAMPLEWRITER001", created);

        Assert.AreEqual("AKIAEXAMPLEWRITER001", SiloRuntimeProfile.FenceWriter(fence, instance, SiloRuntimeProfile.WriterGeneration));
        Assert.IsNull(SiloRuntimeProfile.FenceWriter(fence, Guid.NewGuid(), SiloRuntimeProfile.WriterGeneration));
        Assert.IsNull(SiloRuntimeProfile.FenceWriter(fence, instance, SiloRuntimeProfile.WriterGeneration + 1));
        Assert.IsNull(SiloRuntimeProfile.FenceWriter(SiloRuntimeProfile.Marker(instance, created), instance, SiloRuntimeProfile.WriterGeneration));
        Assert.IsNull(SiloRuntimeProfile.FenceWriter("not json"u8, instance, SiloRuntimeProfile.WriterGeneration));
        var foreignKey = Encoding.UTF8.GetString(fence).Replace("AKIAEXAMPLEWRITER001", "bad key", StringComparison.Ordinal);
        Assert.IsNull(SiloRuntimeProfile.FenceWriter(Encoding.UTF8.GetBytes(foreignKey), instance, SiloRuntimeProfile.WriterGeneration));
        // A rotation within the generation names a new key and nothing else changes.
        var rotated = SiloRuntimeProfile.Fence(instance, "AKIAEXAMPLEWRITER002", created);
        Assert.AreEqual("AKIAEXAMPLEWRITER002", SiloRuntimeProfile.FenceWriter(rotated, instance, SiloRuntimeProfile.WriterGeneration));
    }

    [TestMethod]
    public void InstallOptions_TheOfflineImageAndArchiveOptionsParseAndJoinTheRequestIdentity()
    {
        string[] silo = [.. InstallArguments, "--storage-layout", "silo"];
        string[] archive =
        [
            "--silo-archive-endpoint", "https://archive.example:9443", "--silo-archive-bucket", "hvo-archive",
            "--silo-archive-ca-file", "/etc/hvo/archive-ca.pem", "--silo-archive-credential-file", "/etc/hvo/archive-credential"
        ];

        var plain = CommandLine.Parse(silo, allowTestProductRoot: false, "/var/lib/hvo-sky");
        var staged = CommandLine.Parse([.. silo, "--silo-image-archive", "/srv/hvo/silo-amd64.tar"], allowTestProductRoot: false, "/var/lib/hvo-sky");
        var archived = CommandLine.Parse([.. silo, .. archive], allowTestProductRoot: false, "/var/lib/hvo-sky");

        Assert.AreEqual("/srv/hvo/silo-amd64.tar", staged.SiloImageArchive);
        Assert.IsFalse(plain.HasSiloArchive);
        Assert.IsTrue(archived.HasSiloArchive);
        Assert.AreEqual(("https://archive.example:9443", "hvo-archive", "/etc/hvo/archive-ca.pem", "/etc/hvo/archive-credential"),
            (archived.SiloArchiveEndpoint, archived.SiloArchiveBucket, archived.SiloArchiveCaFile, archived.SiloArchiveCredentialFile));
        var instance = Guid.NewGuid();
        var identities = new[] { plain, staged, archived }.Select(request => CameraAgentInstaller.ComputeRequestSha256(request, instance)).ToArray();
        Assert.HasCount(3, identities.Distinct(StringComparer.Ordinal));
    }

    [TestMethod]
    [DataRow("filesystem-image", "need --storage-layout silo")]
    [DataRow("filesystem-archive", "need --storage-layout silo")]
    [DataRow("partial-archive", "go together")]
    [DataRow("plain-http", "plain HTTP is not supported")]
    [DataRow("path-endpoint", "with a host and optional port only")]
    [DataRow("bucket", "--silo-archive-bucket must be")]
    public void InstallOptions_RefuseTheSiloOptionsOutsideTheirLayoutOrIncompleteOrInsecure(string change, string reason)
    {
        string[] arguments = change switch
        {
            "filesystem-image" => [.. InstallArguments, "--silo-image-archive", "/srv/hvo/silo.tar"],
            "filesystem-archive" => [.. InstallArguments, .. Archive("https://archive.example", "hvo-archive")],
            "partial-archive" => [.. InstallArguments, "--storage-layout", "silo", "--silo-archive-endpoint", "https://archive.example"],
            "plain-http" => [.. InstallArguments, "--storage-layout", "silo", .. Archive("http://archive.example:9000", "hvo-archive")],
            "path-endpoint" => [.. InstallArguments, "--storage-layout", "silo", .. Archive("https://archive.example/hvo-archive", "hvo-archive")],
            _ => [.. InstallArguments, "--storage-layout", "silo", .. Archive("https://archive.example", "Hvo_Archive")]
        };

        var refusal = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments, allowTestProductRoot: false, "/var/lib/hvo-sky"));

        StringAssert.Contains(refusal.Message, reason, StringComparison.Ordinal);

        static string[] Archive(string endpoint, string bucket) =>
        [
            "--silo-archive-endpoint", endpoint, "--silo-archive-bucket", bucket,
            "--silo-archive-ca-file", "/etc/hvo/archive-ca.pem", "--silo-archive-credential-file", "/etc/hvo/archive-credential"
        ];
    }
}
