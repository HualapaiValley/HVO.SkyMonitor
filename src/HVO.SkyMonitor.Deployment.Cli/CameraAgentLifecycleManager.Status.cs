using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentLifecycleManager
{
    private static async Task<LifecycleResult> ObserveRuntimeAsync(
        LifecycleResult result, DockerClient docker, ComposeFiles compose, CancellationToken cancellationToken)
    {
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        var runner = compose.ReplayRunnerContainerName is { } runnerName
            ? await docker.InspectContainerAsync(runnerName, cancellationToken).ConfigureAwait(false)
            : null;
        var expectedImage = result.Image!.ImageId;
        var drifted = runtime.Exists && runtime.ImageId != expectedImage ||
                      runner is { Exists: true } && runner.ImageId != expectedImage;
        var running = runtime.Running && (runner?.Running ?? true);
        var healthStatusCode = runtime.Running && !drifted
            ? await docker.ReadApplicationHealthAsync(runtime.ContainerId, cancellationToken).ConfigureAwait(false)
            : null;
        bool? alive = !runtime.Running ? false : runtime.LivenessStatus switch
        {
            "healthy" => true,
            "unhealthy" => false,
            _ => null
        };
        bool? healthy = !running || drifted || runner is { Healthy: false }
            ? false : healthStatusCode is { } code ? code == 200 : null;

        return result with
        {
            Outcome = drifted && result.Operation is null ? "drifted" : result.Outcome,
            Running = running,
            Alive = alive,
            Healthy = healthy,
            HealthStatusCode = healthStatusCode
        };
    }

    private static async Task<LifecycleResult> ObserveSettledRuntimeAsync(
        LifecycleResult result, DockerClient docker, ComposeFiles compose, CancellationToken cancellationToken)
    {
        // Admission can reopen after candidate verification, and optional quarantine acceptance can leave /health at 503.
        // Losing the current observation leaves readiness unknown. The operation's outcome is already settled.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await ObserveRuntimeAsync(result, docker, compose, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InstallerException or IOException or JsonException or
            KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or OperationCanceledException)
        {
            return result with { Healthy = null };
        }
    }
}
