namespace HVO.SkyMonitor.Deployment;

/// <summary>How an image change, a restore-only recovery and a backup start CameraAgent on the host as it is (#1357).</summary>
internal sealed partial class CameraAgentLifecycleManager
{
    /// <summary>
    /// The model a start on a host without CPU quota support used, beside the operation's other retained records.
    /// </summary>
    internal const string HostComposeName = "host-compose.yml";

    /// <summary>
    /// Starts CameraAgent (<c>docker compose up</c>, or <c>restart</c> when <paramref name="restart"/> is set) from
    /// <paramref name="compose"/> as Docker here can run it. Every container start and restart of an upgrade or a
    /// rollback, of their <c>--resume</c> and their recovery, of a restore-only recovery, and a backup's start of the
    /// instance it stopped (#1353) comes through here, whether
    /// it starts a candidate's staged model or the authenticated model of the image it puts back (PR-1370-R1 F1, F2; R2
    /// F3). A restart keeps each container's configuration, so it is given the same model as the start before it. Docker
    /// discards a memory or PIDs limit it cannot enforce, with its own warning,
    /// and keeps the others, so those are given as recorded. It refuses to create a container with a CPU limit on a host
    /// without CPU quota support, so there the start uses a copy of the model without the CPU limits of CameraAgent and
    /// its replay runner (<see cref="CameraAgentContainerResources.WithoutCpuLimits"/>), written beside the operation's
    /// records. The files the start is given are never changed: they keep the limits the instance records and the
    /// identity its manifest authenticates, so a start on a host that enforces them gives them all again. Every limit was
    /// warned of before anything changed (<see cref="WarnOfUnenforcedLimitsAsync"/>, the result's warnings and the
    /// install log). An upgrade is refused on such a host (<see cref="AdmitHostLimits"/>), so for it the model is
    /// started as given.
    /// </summary>
    private static async Task StartOnThisHostAsync(
        DockerClient docker,
        ComposeFiles compose,
        HostPrerequisiteReport? hostLimits,
        string operationRoot,
        CancellationToken cancellationToken,
        bool restart = false)
    {
        var composeFile = compose.ComposeFile;
        if (hostLimits is not null && HostPrerequisites.WithholdsCpuLimits(hostLimits))
        {
            composeFile = Path.Combine(operationRoot, HostComposeName);
            SafeFileSystem.WriteTextAtomic(
                composeFile,
                CameraAgentContainerResources.WithoutCpuLimits(
                    await File.ReadAllTextAsync(compose.ComposeFile, cancellationToken).ConfigureAwait(false)));
        }
        await docker.ComposeAsync(composeFile, compose.EnvironmentFile, compose.ProjectName,
            restart ? ["restart"] : ["up", "--detach", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
    }
}
