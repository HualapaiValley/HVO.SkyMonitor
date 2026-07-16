using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum DerivedFleetState
{
    Online,
    Degraded,
    Offline
}

internal sealed record FleetClassification(DerivedFleetState State, string Reason);

internal sealed class FleetStatusClassifier(IOptions<FleetStatusOptions> options)
{
    public FleetClassification Classify(DeviceFleetState? state, DateTimeOffset now, DateTimeOffset? activatedAtUtc = null)
    {
        if (state is null)
        {
            return activatedAtUtc is { } activated && now - activated < TimeSpan.FromSeconds(options.Value.OfflineSeconds)
                ? new FleetClassification(DerivedFleetState.Degraded, "awaiting-first-heartbeat")
                : new FleetClassification(DerivedFleetState.Offline, "never-received");
        }
        var age = now - state.ReceivedAtUtc;
        if (age >= TimeSpan.FromSeconds(options.Value.OfflineSeconds))
        {
            return new FleetClassification(DerivedFleetState.Offline, "stale-receipt");
        }
        if (age >= TimeSpan.FromSeconds(options.Value.FreshSeconds))
        {
            return new FleetClassification(DerivedFleetState.Degraded, "stale-receipt");
        }
        if (state.ReportedHealth != FleetHealth.Healthy || state.HasStoragePressure ||
            state.HasRequiredLaneFailure || state.HasQuarantine)
        {
            return new FleetClassification(DerivedFleetState.Degraded, "reported-health");
        }
        if (state.ClockDiagnostic != FleetClockDiagnostic.WithinTolerance)
        {
            return new FleetClassification(DerivedFleetState.Degraded, "apparent-clock-skew");
        }
        if (Recent(state.LastSequenceGapUtc) || Recent(state.LastBootSessionChangeUtc))
        {
            return new FleetClassification(DerivedFleetState.Degraded, "continuity-warning");
        }
        return new FleetClassification(DerivedFleetState.Online, "current");

        bool Recent(DateTimeOffset? value) => value is { } occurred && now - occurred < TimeSpan.FromSeconds(options.Value.OfflineSeconds);
    }
}
