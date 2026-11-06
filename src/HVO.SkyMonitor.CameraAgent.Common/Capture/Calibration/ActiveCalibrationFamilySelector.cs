using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.OpticalCalibration;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

/// <summary>
/// Selects the calibration family and gain response a mixed-gain window is normalized under (#1475 slice F2). The
/// family is the one explicitly activated for the capture's agent and rig; nothing about the capture's gain or other
/// conditions takes part. A gain step therefore selects a different member of the one active family when the plan is
/// prepared, never a different family, and automatic gain control can never change which family is in force.
/// </summary>
internal sealed class ActiveCalibrationFamilySelector(SqliteCalibrationFamilyStore families)
{
    /// <summary>No calibration family is active for the capture's agent and rig.</summary>
    public const string NoActiveFamily = "normalization.plan.no-active-family";

    /// <summary>
    /// Returns the active family's canonical bytes and the response the capture's frames were produced with, or the
    /// stable reason there is none: no active family, a family that does not fit the capture's agent, rig, profiles
    /// and acquisition model, or a response the host cannot declare.
    /// </summary>
    public async ValueTask<ActiveCalibrationFamilySelection> SelectAsync(
        CameraModuleConfig config,
        ReconstructionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(descriptor);
        var capture = descriptor.Capture;
        if (await families.GetActiveAsync(capture.AgentId, capture.RigId, cancellationToken).ConfigureAwait(false)
            is not { } active)
        {
            return ActiveCalibrationFamilySelection.Unavailable(NoActiveFamily);
        }
        var family = active.Family.Family;
        var target = new CalibrationFamilyActivationTarget(
            capture.AgentId,
            capture.RigId,
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Sensor.Sha256,
            descriptor.Profiles.Calibration);
        if (CalibrationFamilyActivationService.Fit(family, target) is { } fitReason)
        {
            return ActiveCalibrationFamilySelection.Unavailable(fitReason);
        }
        var resolution = GainResponseResolver.Resolve(ResolveSource(config), config.Rig.Sensor, config.Rig.Readout);
        return resolution.Response is { } response
            ? new ActiveCalibrationFamilySelection(active.Family.IdentitySha256, CalibrationFamilyContractJson.Serialize(family), response, null)
            : ActiveCalibrationFamilySelection.Unavailable(resolution.ReasonCode!);
    }

    /// <summary>
    /// The response source the host declares for its module. VirtualSky renders with the sensor's pinned simulation
    /// response when it has one and with the built-in ASI174MM model otherwise; any other module is a physical camera.
    /// </summary>
    internal static GainResponseSourceKind ResolveSource(CameraModuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(config.Module?.Type, VirtualOpticalCalibrationReviewService.VirtualModuleType, StringComparison.OrdinalIgnoreCase))
        {
            return GainResponseSourceKind.PhysicalCamera;
        }
        return config.Rig.Sensor.SimulationResponse is null
            ? GainResponseSourceKind.VirtualAsi174MmModel
            : GainResponseSourceKind.VirtualConfiguredResponse;
    }
}

/// <summary>The active family and response a window is planned under, or the stable reason there is none.</summary>
internal sealed record ActiveCalibrationFamilySelection(
    string? FamilyIdentitySha256,
    ReadOnlyMemory<byte> FamilyJson,
    GainResponse? Response,
    string? ReasonCode)
{
    public static ActiveCalibrationFamilySelection Unavailable(string reasonCode) => new(null, default, null, reasonCode);
}
