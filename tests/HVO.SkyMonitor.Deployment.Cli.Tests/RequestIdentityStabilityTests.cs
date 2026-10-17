using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Retiring the signed-release options (#1309) kept their slots in the request identities, so an installation or a
/// lifecycle operation journaled by an earlier build still matches the same request made with this one, and resumes.
/// Each value was computed by the build before the retirement (development/v1 at bf66ccf6) from the same request.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RequestIdentityStabilityTests
{
    private static readonly Guid InstanceId = Guid.Parse("3c1b9e52-7d4a-4f06-8a2e-5b9d0c7f1e34");

    private static readonly InstallRequest Install = new()
    {
        FriendlyName = "North Camera",
        OwnerEmail = "owner@example.test",
        CatalogBundle = "/srv/hvo/catalog.bundle",
        ImageReference = $"ghcr.io/example/cameraagent@sha256:{new string('b', 64)}",
        LatitudeDegrees = 35.2,
        LongitudeDegrees = -114.1,
        ElevationMeters = 800,
        TimeZoneId = "America/Phoenix"
    };

    [TestMethod]
    public void OperatorImageUpgrade_KeepsTheIdentityAnEarlierBuildJournaled()
    {
        var request = new LifecycleRequest(LifecycleOperationKind.Upgrade, InstanceId, "/var/lib/hvo-sky", false, false, false)
        {
            ImageReference = $"sha256:{new string('a', 64)}",
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        Assert.AreEqual("b2bfe1908f2f261e8d7e5c902b3227508390fd9e69c1341649a10a601f86ac5a", request.ComputeRequestSha256());
    }

    [TestMethod]
    public void CatalogBundleInstall_KeepsTheIdentityAnEarlierBuildJournaled()
    {
        var request = new LifecycleRequest(LifecycleOperationKind.CatalogInstall, null, "/var/lib/hvo-sky", false, false, false)
        {
            CatalogBundle = "/srv/hvo/hyg-v4.2-p3-s2-r1.bundle"
        };

        Assert.AreEqual("39df4426a9187b68603f1e76ab65fec927555c37fede3e8c74f17dd5f761e14f", request.ComputeRequestSha256());
    }

    [TestMethod]
    public void OperatorImageInstall_KeepsTheIdentitiesAnEarlierBuildRecorded()
    {
        Assert.AreEqual(
            "97e65646ef5d42bd409f77057977b00e321c4193bdb0e4bd9ba5b210a1afe1d4",
            CameraAgentInstaller.ComputeRequestSha256(Install, InstanceId));
        Assert.AreEqual(
            "a5526fafe466076e6ca2a6028b473782914138faeab75df66e43adebd88cf578",
            CameraAgentInstaller.ComputeLegacyRequestSha256(Install, InstanceId));
    }

    [TestMethod]
    public void ReleaseUpgrade_BindsTheReleaseFolderAndImageSource()
    {
        var request = new LifecycleRequest(LifecycleOperationKind.Upgrade, InstanceId, "/var/lib/hvo-sky", false, false, false)
        {
            Release = "/srv/hvo/release-v1.2.0-dev.3"
        };

        var archive = request.ComputeRequestSha256();

        Assert.AreEqual(archive, (request with { ImageSource = ReleaseImageSource.Archive }).ComputeRequestSha256());
        Assert.AreNotEqual(archive, (request with { ImageSource = ReleaseImageSource.Registry }).ComputeRequestSha256());
        Assert.AreNotEqual(archive, (request with { Release = "/srv/hvo/release-v1.2.0-dev.4" }).ComputeRequestSha256());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReleaseInstall_BindsTheReleaseContentNotItsFolder()
    {
        using var release = ReleaseFixture.Create();
        var verified = await ReleaseAcquirer.ReadAsync(release.Folder, CancellationToken.None);
        var request = Install with { ImageReference = string.Empty, Release = release.Folder };

        var identity = CameraAgentInstaller.ComputeRequestSha256(request, InstanceId, verified);

        Assert.AreEqual(identity, CameraAgentInstaller.ComputeRequestSha256(request with { Release = "/elsewhere" }, InstanceId, verified));
        Assert.AreNotEqual(identity, CameraAgentInstaller.ComputeRequestSha256(request, InstanceId));
        Assert.AreNotEqual(
            identity,
            CameraAgentInstaller.ComputeRequestSha256(request, InstanceId, verified with { ManifestSha256 = new string('0', 64) }));
        Assert.AreNotEqual(
            identity,
            CameraAgentInstaller.ComputeRequestSha256(request with { ImageSource = ReleaseImageSource.Registry }, InstanceId, verified));
        // The legacy identity predates releases, so a release install never matches it.
        Assert.IsFalse(CameraAgentInstaller.RequestIdentityMatches(
            CameraAgentInstaller.ComputeLegacyRequestSha256(request, InstanceId), request, InstanceId, verified));
    }
}
