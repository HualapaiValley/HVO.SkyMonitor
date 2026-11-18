using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Common.Deployment;

/// <summary>Delegates the immutable occurrence to the independent installer; it never stops its own host.</summary>
public sealed class CameraAgentRestartAutomationAdapter(CameraAgentControlStore controls,
    CaptureAdmissionCoordinator capture, TimeProvider timeProvider) : ILocalAutomationPeriodicTaskAdapter
{
    public const string DefinitionId = "cameraagent-restart";
    public const string Target = "installed-cameraagent";
    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.ScheduledCameraAgentRestart;
    public bool SupportsRunNow => false;
    public LocalAutomationTaskDescriptor Describe() => new(TaskKind,
        "Installer-owned drain, supervised restart and independent verification, outside capture windows.",
        [LocalAutomationTriggerKind.Periodic], [Target], controls.Available,
        controls.Available ? null : "The installer lifecycle worker is not registered or its heartbeat expired.");

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.DefinitionId != DefinitionId ? new("restart.reserved-definition-id", "definition.definitionId") :
        definition.TriggerInterval < 3600 ? new("restart.interval-too-short", "definition.triggerInterval") : null;
    }

    public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var request = new CameraAgentControlRequest(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.RunKey)).AsSpan(0, 16)),
            CameraAgentControlKind.Restart, $"automation:{DefinitionId}", "Scheduled CameraAgent restart",
            capture.Snapshot.Version, timeProvider.GetUtcNow(), occurrence.RunKey);
        await controls.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        // Running is deliberately retained across process lifetimes for this task alone. Only a matched installer
        // terminal receipt can settle it; acknowledging the request is not successful restart verification.
        return new(LocalAutomationRunOutcome.Running, "Awaiting the installer's durable lifecycle receipt.");
    }
}
