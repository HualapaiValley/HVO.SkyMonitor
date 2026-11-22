using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo container's identity and private network as a Silo-layout verification sees them (#1966 review). The image a
/// container runs is the pinned release only when it is this daemon's own platform child of the pinned index: a
/// container created from the shared index reports the index as its image whichever child it runs, so only its
/// manifest descriptor (containerd store) or its config ID (classic store) proves it. The private network must be the
/// instance's own Compose network, internal, holding exactly the instance's Silo (under its alias) and its CameraAgent.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloRuntimeVerificationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    // Recorded at install as the loaded platform manifest (offline) or as the pulled index (online).
    [DataRow("manifest", "index", "amd64-manifest", true)]
    [DataRow("manifest", "manifest", "amd64-manifest", true)]
    [DataRow("index", "index", "amd64-manifest", true)]
    [DataRow("index", "manifest", "amd64-manifest", true)]
    [DataRow("config", "config", "none", true)]
    [DataRow("index", "index", "arm64-manifest", false)]
    [DataRow("manifest", "index", "arm64-manifest", false)]
    [DataRow("config", "arm64-config", "none", false)]
    [DataRow("index", "index", "none", false)]
    [DataRow("index", "unknown", "none", false)]
    [DataRow("index", "unknown", "amd64-manifest", false)]
    public async Task ContainerIdentity_IsThisPlatformsChildOfThePinNotTheRecordedImageString(
        string recorded, string running, string descriptor, bool accepted)
    {
        using var harness = new SiloHarness().Defaults();
        harness.SiloImage = Id(running);
        harness.SiloManifest = descriptor switch
        {
            "amd64-manifest" => (SiloHarness.Amd64.ManifestDigest, "amd64"),
            "arm64-manifest" => (SiloHarness.Arm64.ManifestDigest, "arm64"),
            _ => null
        };
        var docker = harness.Docker(out _);
        var image = new SiloImageIdentity(SiloImagePin.Reference, Id(recorded), "amd64", SiloHarness.Amd64.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);

        var verification = SiloInstallation.VerifyContainerAsync(docker, harness.InstanceId, harness.SiloName, harness.DataRoot, image,
            expectApplication: true, TestContext.CancellationToken);

        if (accepted)
        {
            await verification;
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<InstallerException>(() => verification);
            StringAssert.Contains(refusal.Message, "pinned", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("exact", null)]
    [DataRow("detached", "network")]
    [DataRow("wrong-network", "network")]
    [DataRow("second-network", "network")]
    [DataRow("not-internal", "network")]
    [DataRow("foreign-labels", "network")]
    [DataRow("no-alias", "network")]
    [DataRow("foreign-sibling", "network")]
    [DataRow("application-detached", "network")]
    [DataRow("inspect-fails", "network")]
    public async Task PrivateNetwork_IsTheInstancesOwnInternalNetworkHoldingOnlyItsSiloAndCameraAgent(string change, string? failure)
    {
        using var harness = new SiloHarness().Defaults();
        switch (change)
        {
            case "detached": harness.SiloNetworks.Clear(); harness.NetworkMembers.Remove(harness.SiloId); break;
            case "wrong-network":
                harness.SiloNetworks.Clear();
                harness.SiloNetworks["hvo-skymonitor-other_silo"] = ["hvo-silo"];
                harness.NetworkMembers.Remove(harness.SiloId);
                break;
            case "second-network": harness.SiloNetworks["bridge"] = []; break;
            case "not-internal": harness.NetworkInternal = false; break;
            case "foreign-labels": harness.NetworkLabels["com.docker.compose.project"] = "hvo-skymonitor-other"; break;
            case "no-alias": harness.SiloNetworks[harness.NetworkName] = [harness.SiloName]; break;
            case "foreign-sibling": harness.NetworkMembers.Add(new string('f', 64)); break;
            case "application-detached": harness.CameraAgentOnNetwork = false; harness.NetworkMembers.Remove(harness.CameraAgentId); break;
            case "inspect-fails": harness.NetworkInspectFails = true; break;
        }
        var docker = harness.Docker(out _);
        var image = new SiloImageIdentity(SiloImagePin.Reference, SiloImagePin.IndexDigest, "amd64", SiloHarness.Amd64.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);

        var verification = SiloInstallation.VerifyContainerAsync(docker, harness.InstanceId, harness.SiloName, harness.DataRoot, image,
            expectApplication: true, TestContext.CancellationToken);

        if (failure is null)
        {
            await verification;
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<InstallerException>(() => verification);
            StringAssert.Contains(refusal.Message, failure, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("clean", true)]
    [DataRow("listed-then-removed", true)]
    [DataRow("listing-unanswered", false)]
    [DataRow("listed-but-uninspectable", false)]
    public async Task DataRootExclusivity_FailsClosedOnAnyInventoryItCannotConclude(string change, bool concluded)
    {
        using var harness = new SiloHarness().Defaults();
        switch (change)
        {
            case "listed-then-removed": harness.ListedButGone = true; break;
            case "listing-unanswered": harness.ContainerListFails = true; break;
            case "listed-but-uninspectable": harness.ListedButUninspectable = new string('a', 64); break;
        }
        var docker = harness.Docker(out _);

        var check = docker.EnsureSiloDataRootExclusiveAsync(harness.DataRoot, harness.SiloName, TestContext.CancellationToken);

        if (concluded)
            await check;
        else
            await Assert.ThrowsAsync<InstallerException>(() => check);
    }

    internal static string Id(string name) => name switch
    {
        "index" => SiloImagePin.IndexDigest,
        "manifest" => SiloHarness.Amd64.ManifestDigest,
        "config" => SiloHarness.Amd64.ConfigDigest,
        "arm64-config" => SiloHarness.Arm64.ConfigDigest,
        _ => "sha256:" + new string('0', 64)
    };

    internal static JsonObject Labels() => new()
    {
        ["org.opencontainers.image.version"] = SiloImagePin.Release,
        ["org.opencontainers.image.revision"] = SiloImagePin.SourceRevision,
        ["org.opencontainers.image.licenses"] = SiloImagePin.License
    };
}
