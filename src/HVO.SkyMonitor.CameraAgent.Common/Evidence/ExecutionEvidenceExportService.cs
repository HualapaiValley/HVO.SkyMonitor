using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>
/// Drains the durable graph-execution evidence outbox to an authenticated evidence sink.
/// </summary>
/// <remarks>
/// The lane is deliberately isolated from every path local correctness depends on. It never touches acquisition, raw
/// ingress, live graph execution, local publication, artifact upload, or replay: it reads terminal executions the
/// durable store already holds, seals canonical evidence bytes into its own SQLite database, and drains them on its
/// own schedule. Saturation refuses new enlistment rather than blocking a producer, so no bound in this file can
/// apply back pressure to a capture. It is also separate from the fleet heartbeat and from manifest-v2 artifact
/// upload: an acknowledgement here means the receiver stored evidence, never that it holds artifact bytes.
/// </remarks>
internal sealed partial class ExecutionEvidenceExportService(
    IExecutionEvidenceOutbox outbox,
    IExecutionEvidenceTransport transport,
    IExecutionEvidenceOriginProvider originProvider,
    IExecutionEvidenceSource operations,
    ICameraAgentConfigurationAccessor configurationAccessor,
    ExecutionEvidenceExportState state,
    ExecutionEvidenceExportTelemetry telemetry,
    ExecutionEvidenceExportWakeup wakeup,
    StoragePressureState storagePressure,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<ExecutionEvidenceExportService> logger) : BackgroundService
{
    private readonly Guid _bootSessionId = Guid.NewGuid();
    private ExecutionEvidenceOriginV1? _origin;
    private readonly Dictionary<string, ExecutionEvidenceResyncRequestV1> _pendingResync = new(StringComparer.Ordinal);
    private ExecutionEvidenceLimitsV1 _limits = ExecutionEvidenceLimitsV1.Current;
    private string? _negotiatedSchemaVersion;
    private bool _negotiationRefused;
    private long _resyncRequests;
    private long _rejectedUnits;
    private long _conflictUnits;
    private long _drainedUnits;
    private int _inFlight;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exportOptions = options.Value.ExecutionEvidenceExport;
        if (IsDisabled())
        {
            return;
        }

        var root = options.Value.RawIngressRoot;
        ExecutionEvidenceOriginV1 origin;
        try
        {
            origin = await PrepareAsync(root, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            state.Update(
                ExecutionEvidenceExportAvailability.Unhealthy,
                ExecutionEvidenceExportReasonCodes.DurableStateUnavailable,
                timeProvider.GetUtcNow());
            Log.Failed(logger, ExecutionEvidenceExportReasonCodes.DurableStateUnavailable, exception);
            return;
        }

        var pollInterval = TimeSpan.FromSeconds(exportOptions.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(root, origin, exportOptions, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                Log.Failed(logger, ExecutionEvidenceExportReasonCodes.CycleFailed, exception);
                state.Update(
                    ExecutionEvidenceExportAvailability.Degraded,
                    ExecutionEvidenceExportReasonCodes.CycleFailed,
                    timeProvider.GetUtcNow());
            }

            await wakeup.WaitAsync(pollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs exactly one export cycle. Tests drive the loop deterministically through this entry point.</summary>
    internal async ValueTask RunCycleOnceAsync(CancellationToken cancellationToken)
    {
        if (IsDisabled())
        {
            return;
        }
        var root = options.Value.RawIngressRoot;
        var origin = await PrepareAsync(root, cancellationToken).ConfigureAwait(false);
        await RunCycleAsync(root, origin, options.Value.ExecutionEvidenceExport, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A disabled lane performs no source sweep, opens no durable store, and therefore cannot grow local state or
    /// cost a scan. The decision is evaluated on every entry so a configuration reload takes effect immediately.
    /// </summary>
    private bool IsDisabled()
    {
        if (options.Value.ExecutionEvidenceExport.Enabled &&
            options.Value.CentralIntegration.Mode != CentralIntegrationMode.Disabled)
        {
            return false;
        }
        state.Update(
            ExecutionEvidenceExportAvailability.Disabled,
            ExecutionEvidenceExportReasonCodes.Disabled,
            timeProvider.GetUtcNow());
        return true;
    }

    private async ValueTask<ExecutionEvidenceOriginV1> PrepareAsync(string root, CancellationToken cancellationToken)
    {
        if (_origin is { } existing)
        {
            return existing;
        }
        await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await outbox.InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        return _origin = await CreateOriginAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RunCycleAsync(
        string root,
        ExecutionEvidenceOriginV1 origin,
        ExecutionEvidenceExportOptions exportOptions,
        CancellationToken cancellationToken)
    {
        var pressure = storagePressure.Get(root) is { IsUnderPressure: true };
        var configured = await transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        string? sweepReason = null;
        if (configured)
        {
            sweepReason = await SweepAsync(root, origin, exportOptions, pressure, cancellationToken)
                .ConfigureAwait(false);
            if (!_negotiationRefused && _negotiatedSchemaVersion is null)
            {
                await NegotiateAsync(origin, cancellationToken).ConfigureAwait(false);
            }
            if (_negotiatedSchemaVersion is not null)
            {
                await DrainAsync(root, exportOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        await outbox.RetainAsync(
            root,
            TimeSpan.FromHours(exportOptions.AcknowledgementRetentionHours),
            exportOptions.MaximumRetainedAcknowledgements,
            origin.IdentitySha256,
            cancellationToken).ConfigureAwait(false);

        var backlog = await outbox.ReadBacklogAsync(root, cancellationToken).ConfigureAwait(false);
        state.RecordCounters(_resyncRequests, _rejectedUnits, _conflictUnits, _drainedUnits);
        var (availability, reason) = Evaluate(backlog, exportOptions, pressure, sweepReason, configured);
        state.Update(
            availability,
            reason,
            timeProvider.GetUtcNow(),
            backlog,
            pressure,
            _negotiatedSchemaVersion);
    }

    private (ExecutionEvidenceExportAvailability Availability, string ReasonCode) Evaluate(
        ExecutionEvidenceBacklog backlog,
        ExecutionEvidenceExportOptions exportOptions,
        bool pressure,
        string? sweepReason,
        bool configured)
    {
        if (!configured && backlog.PendingCount + backlog.RetryCount + backlog.QuarantinedCount == 0)
        {
            // Standalone operation: no evidence is enlisted, nothing accumulates, and the lane stays healthy for as
            // long as the deployment runs without a central endpoint.
            return (ExecutionEvidenceExportAvailability.Healthy,
                ExecutionEvidenceExportReasonCodes.TransportUnconfigured);
        }
        if (_negotiationRefused)
        {
            return (ExecutionEvidenceExportAvailability.Degraded,
                ExecutionEvidenceExportReasonCodes.NegotiationRejected);
        }
        if (sweepReason is not null)
        {
            return (ExecutionEvidenceExportAvailability.Degraded, sweepReason);
        }
        if (pressure)
        {
            return (ExecutionEvidenceExportAvailability.Degraded, ExecutionEvidenceExportReasonCodes.StoragePressure);
        }
        if (backlog.QuarantinedCount > 0)
        {
            return (ExecutionEvidenceExportAvailability.Degraded, ExecutionEvidenceExportReasonCodes.Quarantined);
        }
        if (backlog.OldestPendingUtc is { } oldest &&
            timeProvider.GetUtcNow() - oldest > TimeSpan.FromHours(exportOptions.MaximumPendingAgeHours))
        {
            return (ExecutionEvidenceExportAvailability.Degraded,
                ExecutionEvidenceExportReasonCodes.BacklogSaturated);
        }
        if (backlog.PendingCount + backlog.RetryCount > 0)
        {
            return (ExecutionEvidenceExportAvailability.Degraded,
                ExecutionEvidenceExportReasonCodes.AcknowledgementPending);
        }
        return (ExecutionEvidenceExportAvailability.Healthy, ExecutionEvidenceExportReasonCodes.Drained);
    }

    /// <summary>
    /// Sweeps terminal executions forward from the durable cursor and seals every eligible unit. Returns a bounded
    /// degraded reason when the sweep could not enlist everything it saw, or null when it kept up.
    /// </summary>
    private async ValueTask<string?> SweepAsync(
        string root,
        ExecutionEvidenceOriginV1 origin,
        ExecutionEvidenceExportOptions exportOptions,
        bool pressure,
        CancellationToken cancellationToken)
    {
        var cursor = await outbox.ReadDiscoveryCursorAsync(root, cancellationToken).ConfigureAwait(false);
        if (cursor.DeferredTerminalUnixMs > 0)
        {
            var oldest = await operations.ReadOldestTerminalExecutionKeyAsync(cancellationToken).ConfigureAwait(false);
            if (oldest is null || oldest > cursor.DeferredTerminalUnixMs)
            {
                // The source retained nothing at or below the key this exporter deferred, so those immutable facts
                // are gone before they were sealed. That is recorded explicitly instead of being skipped silently.
                await outbox.RecordSourcePrunedAsync(root, cursor, cancellationToken).ConfigureAwait(false);
                Log.SourcePruned(logger, cursor.DeferredTerminalUnixMs);
                return ExecutionEvidenceExportReasonCodes.SourcePruned;
            }
        }

        if (pressure)
        {
            await DeferAsync(root, origin.IdentitySha256, cursor, cancellationToken).ConfigureAwait(false);
            return ExecutionEvidenceExportReasonCodes.StoragePressure;
        }

        var limits = new ExecutionEvidenceEnlistmentLimits(
            exportOptions.MaximumPendingUnits,
            exportOptions.MaximumPendingBytes,
            exportOptions.MaximumStorageBytes,
            Math.Min(exportOptions.MaximumUnitBytes, _limits.MaximumEnvelopeBytes));
        var redaction = exportOptions.RedactOperatorIdentity
            ? ExecutionEvidenceRedactionPolicyV1.OperatorIdentity
            : ExecutionEvidenceRedactionPolicyV1.None;
        var exportedRevisions = new HashSet<string>(StringComparer.Ordinal);
        string? rejectedReason = null;

        for (var batch = 0; batch < exportOptions.MaximumDiscoveryBatchesPerCycle; batch++)
        {
            var started = timeProvider.GetTimestamp();
            var rows = await operations.ReadTerminalExecutionsAsync(
                cursor.TerminalUnixMs,
                cursor.ExecutionId,
                exportOptions.DiscoveryBatchSize,
                cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                telemetry.Record("sweep", "drained", timeProvider.GetElapsedTime(started));
                if (cursor.DeferredTerminalUnixMs != 0)
                {
                    await DeferAsync(
                        root, origin.IdentitySha256, cursor with { DeferredTerminalUnixMs = 0 }, cancellationToken)
                        .ConfigureAwait(false);
                }
                return rejectedReason;
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var units = await CreateUnitsAsync(row, origin, redaction, exportedRevisions, cancellationToken)
                    .ConfigureAwait(false);
                var next = cursor with
                {
                    TerminalUnixMs = row.TerminalUnixMs,
                    ExecutionId = row.State.ExecutionId.ToString("N")
                };
                if (units.Count == 0)
                {
                    // Either the execution vanished between the sweep and the read, or a durable value could not be
                    // expressed in this contract version. Both are recorded and the cursor advances past the row, so
                    // one unexportable execution never stalls the lane; the reason stays visible in local status.
                    rejectedReason ??= ExecutionEvidenceExportReasonCodes.ProjectionRejected;
                    cursor = next;
                    await DeferAsync(root, origin.IdentitySha256, cursor, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var result = await outbox.EnlistAsync(
                    root, origin.IdentitySha256, units, next, limits, cancellationToken).ConfigureAwait(false);
                if (result.Disposition == ExecutionEvidenceEnlistmentDisposition.Saturated)
                {
                    await DeferAsync(
                        root,
                        origin.IdentitySha256,
                        cursor with { DeferredTerminalUnixMs = row.TerminalUnixMs },
                        cancellationToken).ConfigureAwait(false);
                    telemetry.Record("sweep", "saturated", timeProvider.GetElapsedTime(started));
                    Log.Saturated(logger, result.ReasonCode ?? ExecutionEvidenceExportReasonCodes.BacklogSaturated);
                    return result.ReasonCode ?? ExecutionEvidenceExportReasonCodes.BacklogSaturated;
                }

                cursor = next;
                if (result.EnlistedCount > 0)
                {
                    telemetry.Record("enlist", "enlisted", TimeSpan.Zero, result.EnlistedCount);
                    Log.Enlisted(logger, result.EnlistedCount, row.State.ExecutionId);
                    wakeup.Signal();
                }
            }
            telemetry.Record("sweep", "enlisted", timeProvider.GetElapsedTime(started), rows.Count);
        }
        return rejectedReason;
    }

    /// <summary>
    /// Persists the sweep cursor without enlisting anything, so a refused or unexportable row is remembered across a
    /// restart instead of being rediscovered and skipped.
    /// </summary>
    private async ValueTask DeferAsync(
        string root,
        string originIdentitySha256,
        ExecutionEvidenceDiscoveryCursor cursor,
        CancellationToken cancellationToken)
        => _ = await outbox.EnlistAsync(
            root,
            originIdentitySha256,
            [],
            cursor,
            new(long.MaxValue, long.MaxValue, long.MaxValue, GraphExecutionEvidenceLimits.MaximumEnvelopeBytes),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Projects one terminal execution into its sealed units. A revision is always offered before the executions that
    /// depend on it, so the receiver can resolve a plan identity from a lower origin sequence.
    /// </summary>
    private async ValueTask<IReadOnlyList<ExecutionEvidenceEnlistmentUnit>> CreateUnitsAsync(
        ProcessingGraphTerminalExecution row,
        ExecutionEvidenceOriginV1 origin,
        ExecutionEvidenceRedactionPolicyV1 redaction,
        HashSet<string> exportedRevisions,
        CancellationToken cancellationToken)
    {
        var detail = await operations.ReadExecutionDetailAsync(row.State.ExecutionId, cancellationToken)
            .ConfigureAwait(false);
        if (detail is null)
        {
            return [];
        }

        var units = new List<ExecutionEvidenceEnlistmentUnit>(3);
        var revisionId = row.State.GraphRevisionId;
        // The revision is marked exported only once its unit is actually built. Marking it earlier would let a later
        // execution of the same revision ship without the canonical body it depends on.
        if (!exportedRevisions.Contains(revisionId))
        {
            ProcessingGraphRevisionSnapshot snapshot;
            try
            {
                snapshot = await operations.ReadRevisionSnapshotAsync(revisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                // The revision this execution names is no longer persisted, so its canonical body can never be
                // exported. The execution is skipped rather than shipped without the body a receiver needs.
                Log.ProjectionRejected(
                    logger, GraphExecutionEvidenceReasonCodes.UnknownRevision, "graphRevision.revisionId");
                return [];
            }
            var assignment = await operations.ReadAssignmentProvenanceAsync(revisionId, cancellationToken)
                .ConfigureAwait(false);
            var revision = ProcessingGraphEvidenceProjection.CreateRevisionEvidence(snapshot, assignment);
            if (revision.Outcome == ProcessingGraphEvidenceProjectionOutcome.Rejected)
            {
                Log.ProjectionRejected(logger, revision.ReasonCode!, revision.FieldPath!);
                return [];
            }
            var revisionProducedUtc = timeProvider.GetUtcNow();
            units.Add(new(
                ExecutionEvidenceBodyKind.GraphRevision,
                $"revision:{revisionId}",
                sequence => Seal(ProcessingGraphEvidenceProjection.CreateEnvelope(
                    origin, sequence, Guid.NewGuid(), revisionProducedUtc, revision.Value!, redaction))));
            exportedRevisions.Add(revisionId);
        }

        var execution = ProcessingGraphEvidenceProjection.CreateExecutionEvidence(detail);
        if (execution.Outcome == ProcessingGraphEvidenceProjectionOutcome.Rejected)
        {
            Log.ProjectionRejected(logger, execution.ReasonCode!, execution.FieldPath!);
            return [];
        }
        var executionProducedUtc = timeProvider.GetUtcNow();
        units.Add(new(
            ExecutionEvidenceBodyKind.GraphExecution,
            $"execution:{row.State.ExecutionId:N}",
            sequence => Seal(ProcessingGraphEvidenceProjection.CreateEnvelope(
                origin, sequence, Guid.NewGuid(), executionProducedUtc, execution.Value!, redaction))));

        var observedAtUtc = timeProvider.GetUtcNow();
        var availability = ProcessingGraphEvidenceProjection.CreateAvailabilityReport(detail, observedAtUtc);
        if (availability.Outcome == ProcessingGraphEvidenceProjectionOutcome.Rejected)
        {
            Log.ProjectionRejected(logger, availability.ReasonCode!, availability.FieldPath!);
            return units;
        }
        if (availability.Outcome == ProcessingGraphEvidenceProjectionOutcome.Projected)
        {
            units.Add(new(
                ExecutionEvidenceBodyKind.ArtifactAvailability,
                $"availability:{row.State.ExecutionId:N}",
                sequence => Seal(ProcessingGraphEvidenceProjection.CreateEnvelope(
                    origin, sequence, Guid.NewGuid(), observedAtUtc, availability.Value!, redaction))));
        }
        return units;
    }

    /// <summary>Serializes one sealed envelope and keeps its canonical payload hash and evidence identity together.</summary>
    private static ExecutionEvidenceSealedUnit Seal(ExecutionEvidenceEnvelopeV1 envelope)
        => new(envelope.EvidenceId, GraphExecutionEvidenceJson.Serialize(envelope), envelope.PayloadSha256);

    private async ValueTask NegotiateAsync(
        ExecutionEvidenceOriginV1 origin,
        CancellationToken cancellationToken)
    {
        var request = new ExecutionEvidenceNegotiationRequestV1(
            ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
            origin,
            GraphExecutionEvidenceSchemaVersions.Supported);
        var started = timeProvider.GetTimestamp();
        ExecutionEvidenceNegotiationTransportResult result;
        using (var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.negotiate"))
        {
            result = await InvokeAsync(() => transport.NegotiateAsync(request, cancellationToken)).ConfigureAwait(false);
            activity?.SetTag("evidence_export.outcome", result.Disposition.ToString());
            activity?.SetStatus(
                result.Disposition == ExecutionEvidenceTransportDisposition.Completed
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error,
                result.ReasonCode);
        }
        telemetry.Record("negotiate", result.Disposition.ToString(), timeProvider.GetElapsedTime(started));
        if (result.Disposition != ExecutionEvidenceTransportDisposition.Completed || result.Response is not { } response)
        {
            return;
        }
        if (GraphExecutionEvidenceJson.Validate(response) is { IsValid: false })
        {
            Log.NegotiationRejected(logger, GraphExecutionEvidenceReasonCodes.InvalidNegotiation);
            return;
        }
        if (response.Disposition == ExecutionEvidenceNegotiationDisposition.Unsupported ||
            !GraphExecutionEvidenceSchemaVersions.IsSupported(response.SelectedSchemaVersion))
        {
            _negotiationRefused = true;
            Log.NegotiationRejected(logger, response.ReasonCode ?? GraphExecutionEvidenceReasonCodes.UnsupportedSchema);
            return;
        }
        // A producer applies the minimum of the published and its own local value for every negotiated limit, so a
        // stricter receiver is honoured and a more permissive one never widens this producer's own bounds.
        _limits = Minimum(ExecutionEvidenceLimitsV1.Current, response.Limits);
        _negotiatedSchemaVersion = response.SelectedSchemaVersion;
    }

    internal static ExecutionEvidenceLimitsV1 Minimum(ExecutionEvidenceLimitsV1 local, ExecutionEvidenceLimitsV1 published)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(published);
        return new(
            local.SchemaVersion,
            Math.Min(local.MaximumEnvelopeBytes, published.MaximumEnvelopeBytes),
            Math.Min(local.MaximumExecutionEnvelopeBytes, published.MaximumExecutionEnvelopeBytes),
            Math.Min(local.MaximumAvailabilityEnvelopeBytes, published.MaximumAvailabilityEnvelopeBytes),
            Math.Min(local.MaximumDefinitionBytes, published.MaximumDefinitionBytes),
            Math.Min(local.MaximumFrozenPlanBytes, published.MaximumFrozenPlanBytes),
            Math.Min(local.MaximumNodeCount, published.MaximumNodeCount),
            Math.Min(local.MaximumAttemptsPerNode, published.MaximumAttemptsPerNode),
            Math.Min(local.MaximumInputsPerNode, published.MaximumInputsPerNode),
            Math.Min(local.MaximumOutputsPerNode, published.MaximumOutputsPerNode),
            Math.Min(local.MaximumInputsPerExecution, published.MaximumInputsPerExecution),
            Math.Min(local.MaximumOutputsPerExecution, published.MaximumOutputsPerExecution),
            Math.Min(local.MaximumAvailabilityObservations, published.MaximumAvailabilityObservations),
            Math.Min(local.MaximumFactsPerFeedback, published.MaximumFactsPerFeedback),
            Math.Min(local.MaximumMissingRanges, published.MaximumMissingRanges),
            Math.Min(local.MaximumResyncRanges, published.MaximumResyncRanges),
            Math.Min(local.MaximumResyncUnits, published.MaximumResyncUnits));
    }

    private async ValueTask DrainAsync(
        string root,
        ExecutionEvidenceExportOptions exportOptions,
        CancellationToken cancellationToken)
    {
        var origins = await outbox.ReadOriginsWithWorkAsync(root, cancellationToken).ConfigureAwait(false);
        foreach (var originRecord in origins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = originRecord.IdentitySha256;
            var units = _pendingResync.Remove(identity, out var resync)
                ? await outbox.ReadRangeAsync(
                    root, identity, resync.Ranges, Math.Min(resync.MaximumUnits, exportOptions.MaximumRequestUnits),
                    cancellationToken).ConfigureAwait(false)
                // The contract publishes no per-submission unit cap of its own, so the negotiated bounded-replay cap
                // is applied as the batch bound: it is the largest unit count the receiver ever asks this producer
                // to send at once, and honouring the minimum of it and the local value keeps a stricter receiver's
                // bound in force.
                : await outbox.ReadPendingAsync(
                    root,
                    identity,
                    Math.Min(exportOptions.MaximumRequestUnits, _limits.MaximumResyncUnits),
                    exportOptions.MaximumRequestBytes,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            if (units.Count == 0)
            {
                continue;
            }

            var started = timeProvider.GetTimestamp();
            var payloads = units.Select(static unit => unit.Payload.ToArray()).ToArray();
            ExecutionEvidenceSubmitTransportResult result;
            using (var activity = ExecutionEvidenceExportTelemetry.ActivitySource.StartActivity("evidence-export.submit"))
            {
                result = await InvokeAsync(() => transport.SubmitAsync(identity, payloads, cancellationToken))
                    .ConfigureAwait(false);
                activity?.SetTag("evidence_export.outcome", result.Disposition.ToString());
                activity?.SetStatus(
                    result.Disposition == ExecutionEvidenceTransportDisposition.Completed
                        ? ActivityStatusCode.Ok
                        : ActivityStatusCode.Error,
                    result.ReasonCode);
            }
            telemetry.Record(
                "submit",
                result.Disposition.ToString(),
                timeProvider.GetElapsedTime(started),
                units.Count,
                payloads.Sum(static payload => (long)payload.Length));

            if (result.Disposition == ExecutionEvidenceTransportDisposition.Completed &&
                result.Feedback is { } feedback &&
                GraphExecutionEvidenceJson.Validate(feedback).IsValid &&
                string.Equals(feedback.OriginIdentitySha256, identity, StringComparison.Ordinal))
            {
                await ApplyFeedbackAsync(root, identity, units, feedback, exportOptions, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var reason = result.Disposition == ExecutionEvidenceTransportDisposition.AuthenticationBlocked
                ? ExecutionEvidenceExportReasonCodes.AuthenticationBlocked
                : ExecutionEvidenceExportReasonCodes.Bound(result.ReasonCode);
            var delay = BoundDelay(result.RetryAfter, exportOptions);
            foreach (var unit in units)
            {
                await DeferUnitAsync(root, identity, unit, delay, reason, exportOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            Log.Retrying(logger, reason, (long)delay.TotalMilliseconds, units.Count);
        }
    }

    private async ValueTask ApplyFeedbackAsync(
        string root,
        string identity,
        IReadOnlyList<ExecutionEvidenceUnit> units,
        ExecutionEvidenceFeedbackV1 feedback,
        ExecutionEvidenceExportOptions exportOptions,
        CancellationToken cancellationToken)
    {
        var settled = new HashSet<long>();
        foreach (var group in feedback.Facts.GroupBy(static fact => fact.OriginSequence))
        {
            var sequence = group.Key;
            var unit = units.FirstOrDefault(candidate => candidate.OriginSequence == sequence);
            if (unit is null)
            {
                continue;
            }
            var acknowledged = group.FirstOrDefault(
                static fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged);
            var rejected = group.FirstOrDefault(static fact => fact.Kind == ExecutionEvidenceFactKind.Rejected);
            if (rejected is not null)
            {
                if (string.Equals(
                        rejected.ReasonCode,
                        GraphExecutionEvidenceReasonCodes.SequenceConflict,
                        StringComparison.Ordinal))
                {
                    await outbox.RecordConflictAsync(
                        root, identity, sequence, unit.PayloadSha256, rejected.StoredPayloadSha256,
                        GraphExecutionEvidenceReasonCodes.SequenceConflict, cancellationToken).ConfigureAwait(false);
                    _conflictUnits++;
                }
                await outbox.QuarantineAsync(
                    root,
                    identity,
                    sequence,
                    ExecutionEvidenceExportReasonCodes.Bound(rejected.ReasonCode),
                    cancellationToken).ConfigureAwait(false);
                _rejectedUnits++;
                Log.Quarantined(logger, sequence, ExecutionEvidenceExportReasonCodes.Bound(rejected.ReasonCode));
                settled.Add(sequence);
                continue;
            }
            if (acknowledged is not null)
            {
                try
                {
                    await outbox.AcknowledgeAsync(
                        root, identity, sequence, acknowledged.PayloadSha256, feedback.ServerTimeUtc,
                        cancellationToken).ConfigureAwait(false);
                    _drainedUnits++;
                }
                catch (InvalidDataException)
                {
                    // The receiver acknowledged a payload hash this origin sequence does not carry. Releasing the
                    // local retention on that word would discard evidence nothing has actually accepted, so the unit
                    // is quarantined for an operator instead, and one bad acknowledgement never stalls the lane.
                    await outbox.RecordConflictAsync(
                        root, identity, sequence, unit.PayloadSha256, acknowledged.PayloadSha256,
                        GraphExecutionEvidenceReasonCodes.InvalidHash, cancellationToken).ConfigureAwait(false);
                    await outbox.QuarantineAsync(
                        root, identity, sequence, GraphExecutionEvidenceReasonCodes.InvalidHash, cancellationToken)
                        .ConfigureAwait(false);
                    _conflictUnits++;
                    _rejectedUnits++;
                    Log.Quarantined(logger, sequence, GraphExecutionEvidenceReasonCodes.InvalidHash);
                }
                settled.Add(sequence);
            }
        }

        // Every unit without a terminal fact is only partially settled. It stays durable and is offered again.
        var delay = BoundDelay(null, exportOptions);
        foreach (var unit in units.Where(unit => !settled.Contains(unit.OriginSequence)))
        {
            await DeferUnitAsync(
                root, identity, unit, delay, ExecutionEvidenceExportReasonCodes.AcknowledgementPending,
                exportOptions, cancellationToken).ConfigureAwait(false);
        }

        await outbox.RecordAcknowledgedThroughAsync(
            root, identity, Math.Max(0, feedback.ContiguousThroughSequence), cancellationToken).ConfigureAwait(false);
        if (GraphExecutionEvidenceJson.CreateResyncRequest(feedback) is { } resync)
        {
            _pendingResync[identity] = resync;
            _resyncRequests++;
            Log.Resync(logger, resync.Ranges.Length, resync.MaximumUnits);
        }
    }

    /// <summary>
    /// Defers one unit, or quarantines it once the bounded attempt budget is spent so a permanently failing unit
    /// stops consuming request capacity while remaining durable and operator-visible.
    /// </summary>
    private ValueTask DeferUnitAsync(
        string root,
        string identity,
        ExecutionEvidenceUnit unit,
        TimeSpan delay,
        string reasonCode,
        ExecutionEvidenceExportOptions exportOptions,
        CancellationToken cancellationToken)
        => unit.AttemptCount >= exportOptions.MaximumAttempts
            ? outbox.QuarantineAsync(root, identity, unit.OriginSequence, reasonCode, cancellationToken)
            : outbox.RetryAsync(
                root, identity, unit.OriginSequence, timeProvider.GetUtcNow() + delay, reasonCode, cancellationToken);

    private async ValueTask<ExecutionEvidenceOriginV1> CreateOriginAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var descriptor = await originProvider.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        var origin = GraphExecutionEvidenceJson.BindIdentity(new(
            ExecutionEvidenceOriginV1.CurrentSchemaVersion,
            descriptor.OriginInstallationId,
            descriptor.AgentInstanceId,
            _bootSessionId,
            descriptor.SoftwareVersion,
            GraphExecutionEvidenceJson.UnhashedPayloadSha256,
            descriptor.ObservatoryId,
            descriptor.LogicalCameraInstallationId,
            descriptor.InstallationPublicId));
        await outbox.EnsureOriginAsync(root, origin, cancellationToken).ConfigureAwait(false);
        return origin;
    }

    /// <summary>Runs one transport call while the single-in-flight-request bound is observable in telemetry.</summary>
    private async ValueTask<T> InvokeAsync<T>(Func<ValueTask<T>> operation)
    {
        state.RecordInFlight(Interlocked.Increment(ref _inFlight));
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            state.RecordInFlight(Interlocked.Decrement(ref _inFlight));
        }
    }

    private static TimeSpan BoundDelay(TimeSpan? value, ExecutionEvidenceExportOptions exportOptions)
    {
        var minimum = TimeSpan.FromSeconds(exportOptions.RetryInitialDelaySeconds);
        var maximum = TimeSpan.FromSeconds(exportOptions.RetryMaximumDelaySeconds);
        return value is null
            ? minimum
            : TimeSpan.FromTicks(Math.Clamp(value.Value.Ticks, minimum.Ticks, maximum.Ticks));
    }

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or
            KeyNotFoundException;

    private static partial class Log
    {
        [LoggerMessage(2600, LogLevel.Debug,
            "Execution evidence export enlisted {Count} unit(s) for execution {ExecutionId}")]
        internal static partial void Enlisted(ILogger logger, int count, Guid executionId);

        [LoggerMessage(2601, LogLevel.Warning,
            "Execution evidence export retry scheduled for {Count} unit(s) because {ReasonCode} after {DelayMilliseconds} ms")]
        internal static partial void Retrying(ILogger logger, string reasonCode, long delayMilliseconds, int count);

        [LoggerMessage(2602, LogLevel.Warning,
            "Execution evidence export quarantined origin sequence {OriginSequence} because {ReasonCode}")]
        internal static partial void Quarantined(ILogger logger, long originSequence, string reasonCode);

        [LoggerMessage(2603, LogLevel.Error, "Execution evidence export cycle failed because {ReasonCode}")]
        internal static partial void Failed(ILogger logger, string reasonCode, Exception exception);

        [LoggerMessage(2604, LogLevel.Warning,
            "Execution evidence export refused new enlistment because {ReasonCode}; nothing already durable was dropped")]
        internal static partial void Saturated(ILogger logger, string reasonCode);

        [LoggerMessage(2605, LogLevel.Error,
            "Execution evidence export detected source retention removed unenlisted evidence at or below key {TerminalUnixMs}")]
        internal static partial void SourcePruned(ILogger logger, long terminalUnixMs);

        [LoggerMessage(2606, LogLevel.Warning,
            "Execution evidence export version negotiation refused because {ReasonCode}")]
        internal static partial void NegotiationRejected(ILogger logger, string reasonCode);

        [LoggerMessage(2607, LogLevel.Information,
            "Execution evidence export scheduled a bounded resynchronization of {RangeCount} range(s) up to {MaximumUnits} unit(s)")]
        internal static partial void Resync(ILogger logger, int rangeCount, int maximumUnits);

        [LoggerMessage(2608, LogLevel.Warning,
            "Execution evidence projection rejected a durable row because {ReasonCode} at {FieldPath}")]
        internal static partial void ProjectionRejected(ILogger logger, string reasonCode, string fieldPath);
    }
}
