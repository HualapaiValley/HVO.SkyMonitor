using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>How the conformance sink behaves for the next submission.</summary>
internal enum ConformanceSinkMode
{
    /// <summary>Accept and acknowledge every valid unit.</summary>
    Accept,

    /// <summary>Refuse the transport recoverably, as an outage or a 5xx would.</summary>
    Deny,

    /// <summary>Refuse the credential. Retrying without operator action cannot succeed.</summary>
    AuthenticationBlocked,

    /// <summary>Return nothing within the request budget.</summary>
    Timeout,

    /// <summary>Acknowledge only the first unit of the batch; the rest stay unsettled.</summary>
    PartiallyAcknowledge,

    /// <summary>Refuse the whole request terminally, as a 400 would.</summary>
    Reject,

    /// <summary>
    /// Acknowledge a payload hash the producer never sent. A conformant receiver never does this; the mode exists
    /// so the producer can be proven not to release retention on evidence nothing actually accepted.
    /// </summary>
    MisacknowledgeHash
}

/// <summary>
/// An in-process authenticated evidence sink that applies the contract's receiver rules exactly, through
/// <see cref="GraphExecutionEvidenceJson.CreateReceiverFacts"/>, plus a fault switch. It exists so the exporter can be
/// proven end to end without any LogicHost receiver: this milestone deliberately ships no central half.
/// </summary>
internal sealed class ExecutionEvidenceConformanceSink : IExecutionEvidenceTransport
{
    private readonly Dictionary<string, Dictionary<long, string>> _stored = new(StringComparer.Ordinal);
    private readonly List<long> _accepted = [];
    private readonly HashSet<long> _drop = [];
    private readonly TimeProvider _timeProvider;

    internal ExecutionEvidenceConformanceSink(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal ConformanceSinkMode Mode { get; set; } = ConformanceSinkMode.Accept;

    internal bool Configured { get; set; } = true;

    internal ExecutionEvidenceNegotiationDisposition NegotiationDisposition { get; set; } =
        ExecutionEvidenceNegotiationDisposition.Supported;

    /// <summary>Published limits, so the producer's minimum-of-published-and-local rule can be observed.</summary>
    internal ExecutionEvidenceLimitsV1 PublishedLimits { get; set; } = ExecutionEvidenceLimitsV1.Current;

    internal int SubmissionCount { get; private set; }

    internal int NegotiationCount { get; private set; }

    internal int MaximumConcurrentRequests { get; private set; }

    internal IReadOnlyList<long> AcceptedSequences => _accepted;

    internal IReadOnlyList<ExecutionEvidenceEnvelopeV1> AcceptedEnvelopes { get; } =
        new List<ExecutionEvidenceEnvelopeV1>();

    internal int LargestRequestUnits { get; private set; }

    internal int LargestRequestBytes { get; private set; }

    /// <summary>
    /// Emits the per-unit facts in reverse order and repeats one already-terminal fact from an earlier submission.
    /// A conformant producer settles by sequence, never by position, so neither may change the outcome.
    /// </summary>
    internal bool ReorderAndRepeatFacts { get; set; }

    private int _inFlight;

    /// <summary>Silently discards the named sequences so the next feedback reports a gap the producer must close.</summary>
    internal void DropSequences(params long[] sequences) => _drop.UnionWith(sequences);

    internal void StopDropping() => _drop.Clear();

    /// <summary>Pre-stores a different payload hash for a sequence so the next submission is an exact conflict.</summary>
    internal void SeedConflict(long sequence) => _conflicts.Add(sequence);

    private readonly HashSet<long> _conflicts = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Configured);

    public ValueTask<ExecutionEvidenceNegotiationTransportResult> NegotiateAsync(
        ExecutionEvidenceNegotiationRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NegotiationCount++;
        if (Mode == ConformanceSinkMode.Deny)
        {
            // A full outage refuses negotiation too, so the producer never starts sending during it.
            return ValueTask.FromResult(new ExecutionEvidenceNegotiationTransportResult(
                ExecutionEvidenceTransportDisposition.Retry, "http-503"));
        }
        var response = new ExecutionEvidenceNegotiationResponseV1(
            ExecutionEvidenceNegotiationResponseV1.CurrentSchemaVersion,
            NegotiationDisposition,
            NegotiationDisposition == ExecutionEvidenceNegotiationDisposition.Supported
                ? GraphExecutionEvidenceSchemaVersions.Supported
                : ["hvo-cameraagent-execution-evidence-v9"],
            PublishedLimits,
            _timeProvider.GetUtcNow(),
            NegotiationDisposition == ExecutionEvidenceNegotiationDisposition.Supported
                ? GraphExecutionEvidenceSchemaVersions.Current
                : null,
            NegotiationDisposition == ExecutionEvidenceNegotiationDisposition.Supported
                ? null
                : GraphExecutionEvidenceReasonCodes.UnsupportedSchema);
        // Round-tripping through the canonical serializer proves the sink only ever answers with a conformant body.
        var parsed = GraphExecutionEvidenceJson.ParseNegotiationResponse(
            GraphExecutionEvidenceJson.Serialize(response));
        return ValueTask.FromResult(new ExecutionEvidenceNegotiationTransportResult(
            ExecutionEvidenceTransportDisposition.Completed, "completed", parsed.Value));
    }

    public ValueTask<ExecutionEvidenceSubmitTransportResult> SubmitAsync(
        string originIdentitySha256,
        IReadOnlyList<byte[]> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, Interlocked.Increment(ref _inFlight));
        try
        {
            SubmissionCount++;
            LargestRequestUnits = Math.Max(LargestRequestUnits, envelopes.Count);
            LargestRequestBytes = Math.Max(
                LargestRequestBytes, envelopes.Sum(static envelope => envelope.Length));
            switch (Mode)
            {
                case ConformanceSinkMode.Deny:
                    return ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
                        ExecutionEvidenceTransportDisposition.Retry, "http-503"));
                case ConformanceSinkMode.AuthenticationBlocked:
                    return ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
                        ExecutionEvidenceTransportDisposition.AuthenticationBlocked,
                        ExecutionEvidenceExportReasonCodes.AuthenticationBlocked));
                case ConformanceSinkMode.Timeout:
                    return ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
                        ExecutionEvidenceTransportDisposition.Retry, "request-timeout"));
                case ConformanceSinkMode.Reject:
                    return ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
                        ExecutionEvidenceTransportDisposition.Rejected, "http-400"));
                default:
                    break;
            }

            // The framing must survive a round trip byte for byte, or every payload hash would change.
            var framed = ExecutionEvidenceBatchCodec.Decode(
                ExecutionEvidenceBatchCodec.Encode(envelopes), GraphExecutionEvidenceLimits.MaximumResyncUnits);
            if (!_stored.TryGetValue(originIdentitySha256, out var origin))
            {
                origin = [];
                _stored[originIdentitySha256] = origin;
            }
            var facts = ImmutableArray.CreateBuilder<ExecutionEvidenceFactV1>();
            var now = _timeProvider.GetUtcNow();
            var settledCount = 0;
            foreach (var payload in framed)
            {
                var parsed = GraphExecutionEvidenceJson.ParseEnvelope(payload);
                if (parsed.Value is not { } envelope)
                {
                    continue;
                }
                if (_drop.Contains(envelope.OriginSequence))
                {
                    continue;
                }
                if (Mode == ConformanceSinkMode.PartiallyAcknowledge && settledCount >= 1)
                {
                    // A truthful partial settlement: the receiver durably stored and validated the unit but has not
                    // committed or acknowledged it, so the producer must keep it.
                    facts.Add(new(
                        ExecutionEvidenceFactV1.CurrentSchemaVersion,
                        ExecutionEvidenceFactKind.Received,
                        envelope.EvidenceId,
                        envelope.OriginSequence,
                        envelope.PayloadSha256,
                        now));
                    continue;
                }
                var stored = origin.GetValueOrDefault(envelope.OriginSequence)
                    ?? (_conflicts.Contains(envelope.OriginSequence) ? new string('F', 64) : null);
                if (Mode == ConformanceSinkMode.MisacknowledgeHash)
                {
                    facts.Add(new(
                        ExecutionEvidenceFactV1.CurrentSchemaVersion,
                        ExecutionEvidenceFactKind.Acknowledged,
                        envelope.EvidenceId,
                        envelope.OriginSequence,
                        new string('E', 64),
                        now));
                    continue;
                }
                var unitFacts = GraphExecutionEvidenceJson.CreateReceiverFacts(envelope, stored, now);
                facts.AddRange(unitFacts);
                if (unitFacts.Any(static fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged))
                {
                    if (origin.TryAdd(envelope.OriginSequence, envelope.PayloadSha256))
                    {
                        _accepted.Add(envelope.OriginSequence);
                        ((List<ExecutionEvidenceEnvelopeV1>)AcceptedEnvelopes).Add(envelope);
                    }
                    settledCount++;
                }
            }

            if (ReorderAndRepeatFacts)
            {
                var reordered = facts.ToImmutable().Reverse().ToList();
                if (_accepted.Count > 0 && origin.ContainsKey(_accepted[0]))
                {
                    var stale = _accepted[0];
                    reordered.Insert(
                        0,
                        new(
                            ExecutionEvidenceFactV1.CurrentSchemaVersion,
                            ExecutionEvidenceFactKind.Acknowledged,
                            Guid.NewGuid(),
                            stale,
                            origin[stale],
                            now,
                            Duplicate: true));
                }
                facts.Clear();
                facts.AddRange(reordered);
            }

            var contiguous = 0L;
            while (origin.ContainsKey(contiguous + 1))
            {
                contiguous++;
            }
            var missing = GraphExecutionEvidenceJson.DetectGaps(contiguous, origin.Keys, out var truncated);
            var feedback = new ExecutionEvidenceFeedbackV1(
                ExecutionEvidenceFeedbackV1.CurrentSchemaVersion,
                originIdentitySha256,
                now,
                contiguous,
                missing,
                facts.Take(GraphExecutionEvidenceLimits.MaximumFactsPerFeedback).ToImmutableArray(),
                new(
                    ExecutionEvidenceRetentionV1.CurrentSchemaVersion,
                    contiguous,
                    now.AddDays(7),
                    1000),
                truncated);
            var round = GraphExecutionEvidenceJson.ParseFeedback(GraphExecutionEvidenceJson.Serialize(feedback));
            return ValueTask.FromResult(new ExecutionEvidenceSubmitTransportResult(
                ExecutionEvidenceTransportDisposition.Completed, "completed", round.Value));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A deterministic in-memory stand-in for the delivered durable processing store.</summary>
internal sealed class FakeExecutionEvidenceSource : IExecutionEvidenceSource
{
    private readonly List<ProcessingGraphExecutionDetail> _executions = [];

    internal int ReadTerminalCallCount { get; private set; }

    internal long? OldestTerminalKey { get; set; }

    internal ProcessingGraphRevisionSnapshot Snapshot { get; set; } = ExecutionEvidenceTestFactory.CreateSnapshot();

    /// <summary>When true the revision the executions name is no longer persisted.</summary>
    internal bool RevisionMissing { get; set; }

    internal ExecutionEvidenceAssignmentProvenanceV1? Assignment { get; set; }

    internal void Add(ProcessingGraphExecutionDetail detail)
    {
        _executions.Add(detail);
        OldestTerminalKey ??= TerminalKey(detail);
    }

    internal void RemoveThrough(long terminalUnixMs)
    {
        _executions.RemoveAll(detail => TerminalKey(detail) <= terminalUnixMs);
        OldestTerminalKey = _executions.Count == 0 ? null : _executions.Min(TerminalKey);
    }

    public ValueTask<IReadOnlyList<ProcessingGraphTerminalExecution>> ReadTerminalExecutionsAsync(
        long afterTerminalUnixMs,
        string afterExecutionId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ReadTerminalCallCount++;
        var rows = _executions
            .Select(detail => new ProcessingGraphTerminalExecution(detail.Execution, TerminalKey(detail)))
            .Where(row => row.TerminalUnixMs > afterTerminalUnixMs ||
                (row.TerminalUnixMs == afterTerminalUnixMs &&
                    string.CompareOrdinal(row.State.ExecutionId.ToString("N"), afterExecutionId) > 0))
            .OrderBy(static row => row.TerminalUnixMs)
            .ThenBy(static row => row.State.ExecutionId.ToString("N"), StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<ProcessingGraphTerminalExecution>>(rows);
    }

    public ValueTask<long?> ReadOldestTerminalExecutionKeyAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(OldestTerminalKey);

    public ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(_executions.FirstOrDefault(detail => detail.Execution.ExecutionId == executionId));

    public ValueTask<ProcessingGraphRevisionSnapshot> ReadRevisionSnapshotAsync(
        string revisionId,
        CancellationToken cancellationToken)
        => RevisionMissing
            ? throw new KeyNotFoundException(revisionId)
            : ValueTask.FromResult(Snapshot);

    public ValueTask<ExecutionEvidenceAssignmentProvenanceV1?> ReadAssignmentProvenanceAsync(
        string revisionId,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(Assignment);

    private static long TerminalKey(ProcessingGraphExecutionDetail detail)
        => (detail.Execution.CompletedUtc ?? detail.Execution.AcceptedUtc).ToUnixTimeMilliseconds();
}
