using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>The host-neutral health of CameraAgent-local orbital elements.</summary>
public enum OrbitalElementsHealthState
{
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// One health evaluation: a state, a fixed description, and bounded PascalCase data whose values are enum names,
/// stable reason codes, booleans and counts only. It never carries a path, a provider body or free text.
/// </summary>
public sealed record OrbitalElementsHealthSnapshot(
    OrbitalElementsHealthState State,
    string Description,
    IReadOnlyDictionary<string, object> Data);

/// <summary>
/// Evaluates orbital-elements health from the same read model the operator status uses. Disabled refresh is healthy;
/// an unusable store is unhealthy; a halt, a quarantine, an unsettled or storage-failed attempt, a stale snapshot, or no
/// snapshot once one was due is degraded, because the last-known-good snapshot (if any) remains readable.
/// </summary>
public sealed class OrbitalElementsHealthEvaluator
{
    public const string SnapshotStaleReasonCode = "orbital.snapshot-stale";
    public const string FirstSnapshotOverdueReasonCode = "orbital.first-snapshot-overdue";

    /// <summary>How long after a due time the first snapshot may still be pending before health degrades.</summary>
    public static readonly TimeSpan DueGrace = TimeSpan.FromMinutes(5);

    private readonly OrbitalElementsOperations _operations;
    private readonly IOptions<CameraAgentHostOptions> _options;
    private readonly TimeProvider _timeProvider;

    public OrbitalElementsHealthEvaluator(
        OrbitalElementsOperations operations,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _operations = operations;
        _options = options;
        _timeProvider = timeProvider;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Health returns a bounded classified state and never discloses persistence exceptions.")]
    public async ValueTask<OrbitalElementsHealthSnapshot> EvaluateAsync(CancellationToken cancellationToken)
    {
        var configured = _options.Value.OrbitalElements;
        if (!configured.Enabled)
        {
            return new(OrbitalElementsHealthState.Healthy, "Orbital elements refresh is disabled.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Availability"] = "Disabled",
                    ["Reason"] = OrbitalElementsOperations.DisabledReasonCode,
                    ["RecoveryAction"] = nameof(OrbitalRefreshRecoveryAction.Configure)
                });
        }
        OrbitalElementsObservation observation;
        try
        {
            observation = await _operations.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var (failure, reasonCode) = OrbitalElementsAvailability.Classify(exception);
            return Unhealthy(failure, reasonCode);
        }
        if (observation.State is not { } state)
        {
            return Unhealthy(observation.Availability, observation.Status.Reason);
        }
        var now = _timeProvider.GetUtcNow();
        var degraded = DegradedReason(now, configured, state, observation);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Availability"] = nameof(OrbitalElementsAvailabilityState.Available),
            ["Reason"] = degraded ?? observation.Status.Reason,
            ["RecoveryAction"] = observation.Status.RecoveryAction.ToString(),
            ["Halted"] = state.Halt is not null
        };
        if (state.Halt?.HttpStatusCode is { } haltStatus)
        {
            data["HaltHttpStatus"] = haltStatus;
        }
        if (state.CurrentRecordCount is { } records)
        {
            data["ActiveRecordCount"] = records;
        }
        if (state.LastActivationUtc is { } activated)
        {
            data["ActiveAgeSeconds"] = (long)Math.Max(0, (now - activated).TotalSeconds);
        }
        if (state.LastAttemptOutcome is { } outcome)
        {
            data["LastAttemptOutcome"] = outcome.ToString();
        }
        return degraded is null
            ? new(OrbitalElementsHealthState.Healthy, "Orbital elements are current.", data)
            : new(OrbitalElementsHealthState.Degraded,
                "Orbital elements need operator attention; the last-known-good snapshot remains readable.", data);
    }

    /// <summary>The first degrading condition in precedence order, or null when the snapshot is healthy.</summary>
    internal static string? DegradedReason(DateTimeOffset now, OrbitalElementsOptions configured, OrbitalProviderState state,
        OrbitalElementsObservation observation)
    {
        if (state.Halt is not null)
        {
            return OrbitalElementsOperations.ProviderHaltedReasonCode;
        }
        if (state.LastAttemptOutcome == OrbitalAttemptOutcome.Quarantined)
        {
            return state.LastAttemptReasonCode ?? OrbitalElementsOperations.CandidateQuarantinedReasonCode;
        }
        if (state.Quarantine is not null)
        {
            return OrbitalElementsOperations.CandidateQuarantinedReasonCode;
        }
        if (observation.Unsettled)
        {
            return OrbitalElementsOperations.AttemptUnsettledReasonCode;
        }
        if (state.LastAttemptOutcome == OrbitalAttemptOutcome.StorageFailed)
        {
            return state.LastAttemptReasonCode ?? OrbitalElementsOperations.StorageFailedReasonCode;
        }
        if (state.LastActivationUtc is { } activated && now - activated > TimeSpan.FromHours(configured.StaleAfterHours))
        {
            return SnapshotStaleReasonCode;
        }
        if (state.CurrentManifestSha256 is not null)
        {
            return null;
        }
        if (observation.Definition is not { Definition.Enabled: true } definition)
        {
            // Enabled with nothing scheduled can never produce a snapshot.
            return observation.Status.Reason;
        }
        return state.LastAttemptUtc is not null || (definition.NextRunUtc is { } due && now >= due + DueGrace)
            ? FirstSnapshotOverdueReasonCode
            : null;
    }

    private static OrbitalElementsHealthSnapshot Unhealthy(OrbitalElementsAvailabilityState availability, string reasonCode)
        => new(OrbitalElementsHealthState.Unhealthy, "Orbital elements storage is unavailable.",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Availability"] = availability.ToString(),
                ["Reason"] = reasonCode,
                ["RecoveryAction"] = nameof(OrbitalRefreshRecoveryAction.RepairStorage)
            });
}
