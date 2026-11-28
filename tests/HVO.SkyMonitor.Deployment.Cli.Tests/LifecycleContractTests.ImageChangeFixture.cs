using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private const string CandidateReference = "ghcr.io/example/cameraagent@sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string CandidateImageId = "sha256:5555555555555555555555555555555555555555555555555555555555555555";

    private static ImageInstallationIdentity CurrentStateContract(ImageInstallationIdentity image)
        => image with
        {
            StateContract = CameraAgentStateContract.Current,
            IdentityMigration = "20261128144428_InitialIdentity",
            RawIngressSchema = "12",
            CatalogManifestVersion = "2"
        };

    private static LifecycleRequest UpgradeRequest(LifecycleFixture fixture)
        => fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = CandidateReference,
            NoDownload = true
        };
}
