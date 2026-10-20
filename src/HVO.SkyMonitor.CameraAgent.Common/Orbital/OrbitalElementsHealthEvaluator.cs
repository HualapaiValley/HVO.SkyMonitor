using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
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
/// an unusable store is unhealthy; a halt, a quarantine, an unsettled or storage-failed attempt, stale elements, an
/// overdue refresh, or no snapshot once one was due is degraded, because the last-known-good snapshot (if any) remains
/// readable.
/// </summary>
/// <remarks>
/// Element freshness and acquisition are separate facts. Freshness derives from the newest element epoch in the active
/// snapshot against the <see cref="OrbitalSelectionPolicy.StaleAbsoluteAgeHours"/> envelope, so re-downloading an
/// unchanged old payload never makes it look fresh. Refresh is overdue when no valid provider retrieval succeeded within
/// <see cref="OrbitalElementsOptions.StaleAfterHours"/>. Activation age is reported separately and only moves when the
/// active payload changes.
/// </remarks>
public sealed class OrbitalElementsHealthEvaluator
{
    /// <summary>The newest element epoch in the active snapshot is older than <see cref="ElementStaleAge"/>.</summary>
    public const string SnapshotStaleReasonCode = "orbital.snapshot-stale";

    /// <summary>No valid provider retrieval succeeded within <see cref="OrbitalElementsOptions.StaleAfterHours"/>.</summary>
    public const string RefreshOverdueReasonCode = "orbital.refresh-overdue";

    public const string FirstSnapshotOverdueReasonCode = "orbital.first-snapshot-overdue";

    /// <summary>
    /// The element-age envelope beyond which the #527 selection policy treats an element set as stale (48 hours). It is
    /// read from <see cref="OrbitalSelectionPolicy"/> rather than restated, so the two cannot drift.
    /// </summary>
    public static readonly TimeSpan ElementStaleAge = TimeSpan.FromHours(
        new OrbitalSelectionPolicy([new OrbitalSourcePriority(CelesTrakGpSource.DatasetKey, 0)]).StaleAbsoluteAgeHours);

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
        if (state.CurrentObjectCount is { } objects)
        {
            data["ActiveObjectCount"] = objects;
        }
        if (state.CurrentMaximumEpochUtc is { } newest)
        {
            data["NewestElementAgeSeconds"] = AgeSeconds(now, newest);
        }
        if (state.CurrentMinimumEpochUtc is { } oldest)
        {
            data["OldestElementAgeSeconds"] = AgeSeconds(now, oldest);
        }
        if (state.LastActivationUtc is { } activated)
        {
            data["ActivationAgeSeconds"] = AgeSeconds(now, activated);
        }
        if (state.LastRetrievalSuccessUtc is { } retrieved)
        {
            data["RetrievalAgeSeconds"] = AgeSeconds(now, retrieved);
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
        if (state.CurrentManifestSha256 is not null)
        {
            // Fail safe: an active snapshot without a newest epoch has no element that could be fresh.
            if (state.CurrentMaximumEpochUtc is not { } newest || now - newest > ElementStaleAge)
            {
                return SnapshotStaleReasonCode;
            }
            return state.LastRetrievalSuccessUtc is not { } retrieved ||
                now - retrieved > TimeSpan.FromHours(configured.StaleAfterHours)
                ? RefreshOverdueReasonCode
                : null;
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

    /// <summary>Whole seconds since <paramref name="then"/>, never negative (an epoch ahead of the clock reads as 0).</summary>
    private static long AgeSeconds(DateTimeOffset now, DateTimeOffset then) => (long)Math.Max(0, (now - then).TotalSeconds);

    private static OrbitalElementsHealthSnapshot Unhealthy(OrbitalElementsAvailabilityState availability, string reasonCode)
        => new(OrbitalElementsHealthState.Unhealthy, "Orbital elements storage is unavailable.",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Availability"] = availability.ToString(),
                ["Reason"] = reasonCode,
                ["RecoveryAction"] = nameof(OrbitalRefreshRecoveryAction.RepairStorage)
            });
}
