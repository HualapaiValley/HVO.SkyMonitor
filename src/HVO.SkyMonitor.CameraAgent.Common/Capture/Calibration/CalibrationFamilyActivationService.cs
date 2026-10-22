using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

/// <summary>
/// Explicitly activates and deactivates calibration families (#1475 slice C1). Every change is durable, recorded with
/// its idempotency key, actor and reason, and idempotent: the same key with the same request returns the recorded
/// result, and the same key with a different request is refused. A family is activated only for the agent and rig it
/// names, under the acquisition model it names.
/// </summary>
/// <remarks>
/// Activation never follows automatic gain control. A request carries no gain or capture condition, and nothing in the
/// capture or processing path calls this service; a gain step selects a different member of the one active family
/// (slice F), never a different family.
/// </remarks>
internal sealed partial class CalibrationFamilyActivationService(
    SqliteCalibrationFamilyStore store,
    ILogger<CalibrationFamilyActivationService> logger)
{
    private const int MaximumTokenLength = 128;
    private const int MaximumReasonLength = 512;

    public async ValueTask<CalibrationFamilyActivationResult> ActivateAsync(
        CalibrationFamilyActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target ?? throw new ArgumentException("An activation names its agent and rig.", nameof(request));
        ValidateCommand(request.IdempotencyKey, target.AgentId, target.RigId, request.Actor, request.Reason);
        var identity = SqliteCalibrationFamilyStore.NormalizeIdentity(request.FamilyIdentitySha256, nameof(request));
        var rigProfile = Sha256(target.RigProfileSha256, nameof(request));
        var sensorProfile = Sha256(target.SensorProfileSha256, nameof(request));
        var model = target.AcquisitionModel;
        if (model is null || !Token(model.Name) || !Token(model.Version))
        {
            throw new ArgumentException("An activation names the acquisition model its lights are captured under.", nameof(request));
        }
        var modelSha256 = Sha256(model.Sha256, nameof(request));
        var payload = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            command = SqliteCalibrationFamilyStore.ActivateCommand,
            agentId = target.AgentId,
            rigId = target.RigId,
            familyIdentitySha256 = identity,
            rigProfileSha256 = rigProfile,
            sensorProfileSha256 = sensorProfile,
            acquisitionModel = new { name = model.Name, version = model.Version, sha256 = modelSha256 },
            actor = request.Actor,
            reason = request.Reason
        });
        var result = await store.ChangeActivationAsync(
            SqliteCalibrationFamilyStore.ActivateCommand, request.IdempotencyKey, payload, target.AgentId, target.RigId,
            identity, request.Actor, request.Reason, family => Fit(family, target), cancellationToken).ConfigureAwait(false);
        Report(result, request.IdempotencyKey, target.AgentId, target.RigId);
        return result;
    }

    public async ValueTask<CalibrationFamilyActivationResult> DeactivateAsync(
        CalibrationFamilyDeactivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommand(request.IdempotencyKey, request.AgentId, request.RigId, request.Actor, request.Reason);
        var payload = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            command = SqliteCalibrationFamilyStore.DeactivateCommand,
            agentId = request.AgentId,
            rigId = request.RigId,
            actor = request.Actor,
            reason = request.Reason
        });
        var result = await store.ChangeActivationAsync(
            SqliteCalibrationFamilyStore.DeactivateCommand, request.IdempotencyKey, payload, request.AgentId, request.RigId,
            null, request.Actor, request.Reason, static _ => null, cancellationToken).ConfigureAwait(false);
        Report(result, request.IdempotencyKey, request.AgentId, request.RigId);
        return result;
    }

    /// <summary>
    /// Whether a family fits its target: the same agent, rig, rig profile and sensor profile, and masters acquired
    /// under the calibration model the target's lights are captured under. Returns the refusal reason, or null.
    /// </summary>
    internal static string? Fit(CalibrationFamilyV1 family, CalibrationFamilyActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(family.AgentId, target.AgentId, StringComparison.Ordinal) ||
            !string.Equals(family.RigId, target.RigId, StringComparison.Ordinal) ||
            !string.Equals(family.RigProfileSha256, target.RigProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(family.SensorProfileSha256, target.SensorProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            return CalibrationFamilyActivationReasonCodes.IdentityMismatch;
        }
        return string.Equals(family.AcquisitionModel.Name, target.AcquisitionModel.Name, StringComparison.Ordinal) &&
               string.Equals(family.AcquisitionModel.Version, target.AcquisitionModel.Version, StringComparison.Ordinal) &&
               string.Equals(family.AcquisitionModel.Sha256, target.AcquisitionModel.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch;
    }

    private void Report(CalibrationFamilyActivationResult result, string idempotencyKey, string agentId, string rigId)
    {
        if (result.Record is not { } record)
        {
            ActivationRefused(logger, idempotencyKey, agentId, rigId, result.ReasonCode);
        }
        else if (result.Replayed)
        {
            ActivationReplayed(logger, idempotencyKey, record.CommandKind, agentId, rigId);
        }
        else
        {
            ActivationRecorded(
                logger, idempotencyKey, record.CommandKind, agentId, rigId,
                record.FromIdentitySha256 ?? "none", record.ToIdentitySha256 ?? "none");
        }
    }

    private static void ValidateCommand(string idempotencyKey, string agentId, string rigId, string actor, string? reason)
    {
        if (!Token(idempotencyKey) || idempotencyKey.Any(char.IsControl))
        {
            throw new ArgumentException("A valid idempotency key is required.", nameof(idempotencyKey));
        }
        if (!Token(agentId))
        {
            throw new ArgumentException("A valid agent identifier is required.", nameof(agentId));
        }
        if (!Token(rigId))
        {
            throw new ArgumentException("A valid rig identifier is required.", nameof(rigId));
        }
        if (!Token(actor))
        {
            throw new ArgumentException("A valid actor is required.", nameof(actor));
        }
        if (reason?.Length > MaximumReasonLength)
        {
            throw new ArgumentException("The reason is too long.", nameof(reason));
        }
    }

    private static bool Token(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTokenLength;

    private static string Sha256(string? value, string parameterName)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : throw new ArgumentException("A 64-character hexadecimal SHA-256 is required.", parameterName);

    [LoggerMessage(EventId = 7902, Level = LogLevel.Information,
        Message = "Calibration family {CommandKind} {IdempotencyKey} recorded for agent {AgentId} rig {RigId}: {FromIdentitySha256} to {ToIdentitySha256}.")]
    private static partial void ActivationRecorded(
        ILogger logger, string idempotencyKey, string commandKind, string agentId, string rigId,
        string fromIdentitySha256, string toIdentitySha256);

    [LoggerMessage(EventId = 7903, Level = LogLevel.Information,
        Message = "Calibration family {CommandKind} {IdempotencyKey} for agent {AgentId} rig {RigId} replayed its recorded result.")]
    private static partial void ActivationReplayed(
        ILogger logger, string idempotencyKey, string commandKind, string agentId, string rigId);

    [LoggerMessage(EventId = 7904, Level = LogLevel.Warning,
        Message = "Calibration family command {IdempotencyKey} for agent {AgentId} rig {RigId} was refused: {ReasonCode}.")]
    private static partial void ActivationRefused(
        ILogger logger, string idempotencyKey, string agentId, string rigId, string reasonCode);
}
