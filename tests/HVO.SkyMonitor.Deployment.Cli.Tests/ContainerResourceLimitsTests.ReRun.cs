using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The limits a re-run of a completed install moves the instance to (install contract section 7, #1315): a limit given
/// replaces the recorded one, a limit left out is kept, and an instance recorded without limits keeps its original ones
/// until a re-run gives one. Also the limits read back from a Compose file, which a re-run's plan compares with.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContainerResourceLimitsReRunTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    private static readonly ContainerResourceLimits OperatorSet = new(3m, 6 * Gibibyte, 256, true, true);

    [TestMethod]
    public void ReRunTarget_NoLimitGiven_KeepsExactlyWhatIsRecorded()
    {
        Assert.AreEqual(OperatorSet, CameraAgentContainerResources.ReRunTarget(OperatorSet, null, null));
        Assert.AreEqual(
            CameraAgentContainerResources.Default,
            CameraAgentContainerResources.ReRunTarget(CameraAgentContainerResources.Default, null, null));
        Assert.IsNull(CameraAgentContainerResources.ReRunTarget(null, null, null));
    }

    [TestMethod]
    public void ReRunTarget_OneLimitGiven_ReplacesItAndKeepsTheOther()
    {
        Assert.AreEqual(
            new ContainerResourceLimits(3m, 8 * Gibibyte, 256, true, true),
            CameraAgentContainerResources.ReRunTarget(OperatorSet, null, "8g"));
        Assert.AreEqual(
            new ContainerResourceLimits(4m, 4 * Gibibyte, 256, true, false),
            CameraAgentContainerResources.ReRunTarget(CameraAgentContainerResources.Default, 4m, null));
    }

    [TestMethod]
    public void ReRunTarget_TheDefaultGivenExplicitly_IsRecordedAsSetByTheOperator()
    {
        Assert.AreEqual(
            new ContainerResourceLimits(2m, 4 * Gibibyte, 256, true, false),
            CameraAgentContainerResources.ReRunTarget(CameraAgentContainerResources.Default, 2m, null));
    }

    [TestMethod]
    public void ReRunTarget_InstanceRecordedWithoutLimits_MovesTheLimitNotGivenToTheDefault()
    {
        // The original 1 CPU and 1 GiB are below the floor, so they are never kept beside a limit the operator gives.
        Assert.AreEqual(
            new ContainerResourceLimits(2m, 6 * Gibibyte, 256, false, true),
            CameraAgentContainerResources.ReRunTarget(null, null, "6g"));
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("1.99")]
    public void ReRunTarget_CpusBelowTheFloor_IsRefusedAsAUsageError(string cpus)
    {
        ArgumentNullException.ThrowIfNull(cpus);
        Assert.ThrowsExactly<InstallUsageException>(() => CameraAgentContainerResources.ReRunTarget(
            OperatorSet, decimal.Parse(cpus, System.Globalization.CultureInfo.InvariantCulture), null));
    }

    [TestMethod]
    public void ReRunTarget_MemoryBelowTheFloor_IsRefusedAsAUsageError()
        => Assert.ThrowsExactly<InstallUsageException>(() => CameraAgentContainerResources.ReRunTarget(OperatorSet, null, "3g"));

    [TestMethod]
    public void TryReadLimits_ReadsTheCameraAgentBlockAndIgnoresTheReplayRunner()
    {
        const string compose =
            "services:\n  cameraagent:\n    image: x\n    cpus: \"3.5\"\n    mem_limit: 6144M\n    pids_limit: 256\n" +
            "\n  replay-runner:\n    cpus: \"0.5\"\n    mem_limit: 2G\n    pids_limit: 64\n";

        Assert.AreEqual(
            new ContainerResourceLimits(3.5m, 6 * Gibibyte, 256, false, false),
            CameraAgentContainerResources.TryReadLimits(compose));
        Assert.IsNull(CameraAgentContainerResources.TryReadLimits("services:\n  cameraagent:\n    image: x\n"));
    }
}
