using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Endpoints;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentControlRestartWindow
{
    Task<bool> IsSafeAsync(CancellationToken cancellationToken, bool beforeRestart = false);
}

internal sealed class CameraAgentControlRestartWindow(CaptureScheduleRuntimeCoordinator schedule,
    TimeProvider clock) : ICameraAgentControlRestartWindow
{
    public async Task<bool> IsSafeAsync(CancellationToken cancellationToken, bool beforeRestart = false)
    {
        if (schedule.Snapshot is null) return false;
        var state = await schedule.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var decision = CaptureScheduleEvaluator.Evaluate(state.ActiveRevision.Definition, state.Preview,
            new(now, CaptureScheduleSafetyState.Available, ManualPaused: false, state.Overrides));
        // A permanently closed schedule may have no transition; its checked preview still bounds the evidence.
        return CameraAgentControlLifecycleEndpoints.IsRestartWindowSafe(decision with
        { NextTransitionUtc = decision.NextTransitionUtc ?? state.Preview.PreviewEndUtc }, now, beforeRestart);
    }
}
