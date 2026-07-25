using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal static class CameraAgentScheduleOperatorProjection
{
    internal static LocalCaptureProfileDefinition Sanitize(LocalCaptureProfileDefinition profile)
        => profile with
        {
            Module = profile.Module with { Options = null },
            ProcessingSteps = profile.ProcessingSteps
                .Select(static step => step with { Options = null })
                .ToArray()
        };

    internal static CaptureScheduleRevisionSnapshot Sanitize(CaptureScheduleRevisionSnapshot revision)
        => revision with { Profile = Sanitize(revision.Profile) };

    internal static CaptureScheduleStoreSnapshot Sanitize(CaptureScheduleStoreSnapshot snapshot)
        => snapshot with
        {
            ActiveRevision = Sanitize(snapshot.ActiveRevision),
            PendingRevision = snapshot.PendingRevision is null ? null : Sanitize(snapshot.PendingRevision)
        };

    internal static CaptureScheduleOperatorState Sanitize(CaptureScheduleOperatorState state)
        => state with
        {
            ActiveRevision = Sanitize(state.ActiveRevision),
            PendingRevision = state.PendingRevision is null ? null : Sanitize(state.PendingRevision),
            History = state.History.Select(Sanitize).ToArray()
        };

    internal static LocalCaptureProfileDefinition RestoreOpaqueOptions(
        LocalCaptureProfileDefinition candidate,
        LocalCaptureProfileDefinition basis)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(basis);
        var validation = LocalCaptureProfileContract.Validate(candidate);
        if (!validation.IsValid || candidate.ProcessingSteps.Any(static step => step is null))
        {
            throw new ArgumentException("The local capture profile structure is invalid.", nameof(candidate));
        }
        var module = string.Equals(candidate.Module.Type, basis.Module.Type, StringComparison.OrdinalIgnoreCase)
            ? candidate.Module with { Options = basis.Module.Options }
            : candidate.Module;
        var steps = candidate.ProcessingSteps.Select((step, index) =>
        {
            var matching = basis.ProcessingSteps.FirstOrDefault(item =>
                step.Id is not null &&
                string.Equals(item.Id, step.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Type, step.Type, StringComparison.OrdinalIgnoreCase));
            if (matching is null && step.Id is null && index < basis.ProcessingSteps.Count &&
                basis.ProcessingSteps[index].Id is null &&
                string.Equals(basis.ProcessingSteps[index].Type, step.Type, StringComparison.OrdinalIgnoreCase))
            {
                matching = basis.ProcessingSteps[index];
            }
            return matching is null ? step : step with { Options = matching.Options };
        }).ToArray();
        return candidate with { Module = module, ProcessingSteps = steps };
    }
}
