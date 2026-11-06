using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

/// <summary>Stable reasons a calibration-family activation or deactivation is refused (#1475).</summary>
internal static class CalibrationFamilyActivationReasonCodes
{
    public const string Activated = "calibration.family.activation.activated";
    public const string Deactivated = "calibration.family.activation.deactivated";
    public const string UnknownFamily = "calibration.family.activation.unknown-family";
    public const string IdentityMismatch = "calibration.family.activation.identity-mismatch";
    public const string AcquisitionModelMismatch = "calibration.family.activation.acquisition-model-mismatch";
    public const string IdempotencyConflict = "calibration.family.activation.idempotency-conflict";
}

/// <summary>A stored calibration family: its verified canonical contract and the identity it is keyed by.</summary>
internal sealed record CalibrationFamilyRecord(
    string IdentitySha256,
    CalibrationFamilyV1 Family,
    DateTimeOffset CreatedUtc);

/// <summary>The family active for one agent and rig, and when it was activated.</summary>
internal sealed record CalibrationFamilyActiveRecord(
    string AgentId,
    string RigId,
    CalibrationFamilyRecord Family,
    DateTimeOffset ActivatedUtc);

/// <summary>
/// The agent and rig a family is activated for: their identities and the calibration model their lights are captured
/// under. A family fits only when it names the same agent, rig, rig profile, sensor profile and acquisition model.
/// </summary>
internal sealed record CalibrationFamilyActivationTarget(
    string AgentId,
    string RigId,
    string RigProfileSha256,
    string SensorProfileSha256,
    ProfileIdentityDescriptor AcquisitionModel);

/// <summary>An explicit, idempotent request to make a stored family the active one for its target.</summary>
internal sealed record CalibrationFamilyActivationRequest(
    string IdempotencyKey,
    CalibrationFamilyActivationTarget Target,
    string FamilyIdentitySha256,
    string Actor,
    string? Reason);

/// <summary>An explicit, idempotent request to leave an agent and rig with no active family.</summary>
internal sealed record CalibrationFamilyDeactivationRequest(
    string IdempotencyKey,
    string AgentId,
    string RigId,
    string Actor,
    string? Reason);

/// <summary>One recorded activation or deactivation, as the activation log keeps it.</summary>
internal sealed record CalibrationFamilyActivationRecord(
    string IdempotencyKey,
    string CommandKind,
    string PayloadSha256,
    string AgentId,
    string RigId,
    string? FromIdentitySha256,
    string? ToIdentitySha256,
    string Actor,
    string? Reason,
    DateTimeOffset RecordedUtc);

/// <summary>
/// The outcome of an activation or deactivation: the recorded change, which a replay of the same request returns
/// unchanged, or the stable reason it was refused, with nothing written.
/// </summary>
internal sealed record CalibrationFamilyActivationResult(
    CalibrationFamilyActivationRecord? Record,
    string ReasonCode,
    bool Replayed)
{
    public bool IsAccepted => Record is not null;

    internal static CalibrationFamilyActivationResult Refused(string reasonCode) => new(null, reasonCode, false);
}
