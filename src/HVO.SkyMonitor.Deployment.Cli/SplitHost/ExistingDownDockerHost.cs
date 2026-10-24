using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed class ExistingDownDockerHost : IDeploymentHost
{
    private readonly DeploymentHost files = new();
    public IDeploymentFiles Open(DeploymentTarget target, CancellationToken token) => files.Open(target, token);
    public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token) => new ProcessRunner(4 * 1024 * 1024)
        .RunAsync("bash", ["-c", SplitHostInventory.Resource("TeardownContext.sh"), "hvo-down-docker", .. arguments], token);
}
