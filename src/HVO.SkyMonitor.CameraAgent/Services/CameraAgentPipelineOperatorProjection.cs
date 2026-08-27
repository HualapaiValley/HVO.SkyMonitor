using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CameraAgentPipelineRevisionPlan(
    string RevisionId,
    long RevisionNumber,
    string ProfileSha256,
    bool CanToggle,
    CaptureProcessingPlanPreview Plan);

internal sealed record CameraAgentPipelineOperatorState(
    CameraAgentPipelineRevisionPlan Active,
    CameraAgentPipelineRevisionPlan? Pending);

internal sealed record CameraAgentPipelineProfilePreview(
    string BasisRevisionId,
    string ProfileJson,
    CaptureProcessingPlanPreview Plan);

internal static class CameraAgentPipelineOperatorProjection
{
    internal static CameraAgentPipelineOperatorState CreateState(
        CaptureScheduleOperatorState state,
        CameraModuleConfig currentConfiguration,
        ICaptureProcessingPipelineFactory pipelineFactory)
        => new(
            CreateRevision(state.ActiveRevision, currentConfiguration, pipelineFactory),
            state.PendingRevision is null
                ? null
                : CreateRevision(state.PendingRevision, currentConfiguration, pipelineFactory));

    internal static CaptureProcessingPlanPreview Preview(
        LocalCaptureProfileDefinition profile,
        CameraModuleConfig currentConfiguration,
        ICaptureProcessingPipelineFactory pipelineFactory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        var validation = LocalCaptureProfileContract.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The local capture profile is invalid ({validation.FieldPath}).",
                nameof(profile));
        }
        return pipelineFactory.PreviewPlan(profile.ApplyTo(currentConfiguration));
    }

    internal static LocalCaptureProfileDefinition Toggle(
        LocalCaptureProfileDefinition profile,
        string nodeId,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!string.Equals(
                profile.SchemaVersion,
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Graph toggles require a cameraagent-local-profile-v2 revision.");
        }
        if (nodeId.Length > 128)
        {
            throw new InvalidOperationException("The processing node identifier is invalid.");
        }
        var matches = profile.ProcessingSteps
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => string.Equals(
                string.IsNullOrWhiteSpace(item.Step.Id) ? item.Step.Type : item.Step.Id.Trim(),
                nodeId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("The processing node was not found in the desired graph.");
        }
        var steps = profile.ProcessingSteps.ToArray();
        steps[matches[0].Index] = matches[0].Step with { Enabled = enabled };
        return profile with { ProcessingSteps = steps };
    }

    internal static string SanitizeValidationFailure(InvalidOperationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Message.Contains("depends on disabled step", StringComparison.Ordinal))
        {
            return "An enabled node still depends on the disabled node. Disable its dependents and preview again.";
        }
        if (exception.Message.Contains("cameraagent-local-profile-v2", StringComparison.Ordinal))
        {
            return "Graph toggles require a cameraagent-local-profile-v2 revision.";
        }
        if (exception.Message.Contains("processing node", StringComparison.Ordinal))
        {
            return "The processing node was not found in the desired graph.";
        }
        return exception.Message.Contains("dependency cycle", StringComparison.Ordinal)
            ? "The desired graph contains a dependency cycle."
            : "The desired graph is invalid for this CameraAgent.";
    }

    private static CameraAgentPipelineRevisionPlan CreateRevision(
        CaptureScheduleRevisionSnapshot revision,
        CameraModuleConfig currentConfiguration,
        ICaptureProcessingPipelineFactory pipelineFactory)
        => new(
            revision.RevisionId,
            revision.RevisionNumber,
            revision.ProfileSha256,
            LocalCaptureProfileContract.Validate(revision.Profile).IsValid,
            pipelineFactory.PreviewPlan(revision.Profile.ApplyTo(currentConfiguration)));
}
