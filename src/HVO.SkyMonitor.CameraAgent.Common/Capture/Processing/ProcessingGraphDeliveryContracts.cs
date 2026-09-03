using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public enum ProcessingGraphDeliveryTransportDisposition
{
    Acknowledged,
    Retry,
    AuthenticationBlocked,
    Rejected
}

public sealed record ProcessingGraphProposalTransportResult(
    ProcessingGraphDeliveryTransportDisposition Disposition,
    string ReasonCode,
    ProcessingGraphProposalPollResponseV1? Response = null,
    TimeSpan? RetryAfter = null);

public sealed record ProcessingGraphFactTransportResult(
    ProcessingGraphDeliveryTransportDisposition Disposition,
    string ReasonCode,
    ProcessingGraphFactAcknowledgementV1? Acknowledgement = null,
    TimeSpan? RetryAfter = null);

public sealed record ProcessingGraphDeliveryBacklog(
    long PendingProposalCount,
    DateTimeOffset? OldestPendingProposalUtc,
    long PendingFactCount,
    DateTimeOffset? OldestPendingFactUtc);

public interface IProcessingGraphDeliveryTransport
{
    ValueTask<ProcessingGraphProposalTransportResult> PullAsync(
        ProcessingGraphProposalPollRequestV1 request,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphFactTransportResult> SendFactAsync(
        ProcessingGraphDeliveryFactV1 fact,
        CancellationToken cancellationToken);
}

public interface IProcessingGraphDeliveryInbox
{
    ProcessingGraphAgentCapabilities Capabilities { get; }

    ValueTask StageAsync(ProcessingGraphDeliveryProposalV1 proposal, CancellationToken cancellationToken);

    ValueTask ObserveActiveRevisionAsync(CancellationToken cancellationToken);

    ValueTask<ProcessingGraphDeliveryFactV1?> ReadPendingFactAsync(CancellationToken cancellationToken);

    ValueTask AcknowledgeFactAsync(Guid factId, CancellationToken cancellationToken);

    ValueTask SupersedeProposalAsync(Guid proposalId, CancellationToken cancellationToken);

    ValueTask RetryFactAsync(
        Guid factId,
        DateTimeOffset nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphDeliveryBacklog> ReadBacklogAsync(CancellationToken cancellationToken);
}

internal sealed class NullProcessingGraphDeliveryTransport : IProcessingGraphDeliveryTransport
{
    internal static NullProcessingGraphDeliveryTransport Instance { get; } = new();

    public ValueTask<ProcessingGraphProposalTransportResult> PullAsync(
        ProcessingGraphProposalPollRequestV1 request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new ProcessingGraphProposalTransportResult(
            ProcessingGraphDeliveryTransportDisposition.Retry,
            "transport-unavailable"));

    public ValueTask<ProcessingGraphFactTransportResult> SendFactAsync(
        ProcessingGraphDeliveryFactV1 fact,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new ProcessingGraphFactTransportResult(
            ProcessingGraphDeliveryTransportDisposition.Retry,
            "transport-unavailable"));
}

public enum ProcessingGraphDeliveryAvailability
{
    Initializing,
    Healthy,
    Degraded,
    Unhealthy,
    Disabled
}

public sealed record ProcessingGraphDeliveryStateSnapshot(
    ProcessingGraphDeliveryAvailability Availability,
    string ReasonCode,
    long PendingFactCount,
    DateTimeOffset? LastCentralContactUtc,
    long PendingProposalCount = 0,
    DateTimeOffset? OldestPendingProposalUtc = null,
    DateTimeOffset? OldestPendingFactUtc = null);

public sealed class ProcessingGraphDeliveryState
{
    private ProcessingGraphDeliveryStateSnapshot _snapshot = new(
        ProcessingGraphDeliveryAvailability.Initializing, "initializing", 0, null);

    public ProcessingGraphDeliveryStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void Update(
        ProcessingGraphDeliveryAvailability availability,
        string reasonCode,
        long pendingFactCount,
        DateTimeOffset? lastCentralContactUtc = null,
        ProcessingGraphDeliveryBacklog? backlog = null)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, new(
            availability,
            reasonCode.Length <= 128 ? reasonCode : reasonCode[..128],
            pendingFactCount,
            lastCentralContactUtc ?? current.LastCentralContactUtc,
            backlog?.PendingProposalCount ?? 0,
            backlog?.OldestPendingProposalUtc,
            backlog?.OldestPendingFactUtc));
    }

    internal void UpdateBacklog(ProcessingGraphDeliveryBacklog backlog)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with
        {
            PendingProposalCount = backlog.PendingProposalCount,
            PendingFactCount = backlog.PendingFactCount,
            OldestPendingProposalUtc = backlog.OldestPendingProposalUtc,
            OldestPendingFactUtc = backlog.OldestPendingFactUtc
        });
    }
}

public sealed class ProcessingGraphDeliveryTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.ProcessingGraphDelivery";
    public const string ActivitySourceName = MeterName;

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _duration;

    public ProcessingGraphDeliveryTelemetry(ProcessingGraphDeliveryState state, TimeProvider timeProvider)
    {
        _operations = _meter.CreateCounter<long>("hvo.processing_graph_delivery.operations");
        _duration = _meter.CreateHistogram<double>("hvo.processing_graph_delivery.operation.duration", "ms");
        _meter.CreateObservableGauge(
            "hvo.processing_graph_delivery.pending_proposals",
            () => state.Snapshot.PendingProposalCount,
            "proposal");
        _meter.CreateObservableGauge(
            "hvo.processing_graph_delivery.pending_facts",
            () => state.Snapshot.PendingFactCount,
            "fact");
        _meter.CreateObservableGauge(
            "hvo.processing_graph_delivery.oldest_pending_proposal_age",
            () => state.Snapshot.OldestPendingProposalUtc is { } proposal
                ? Math.Max(0, (timeProvider.GetUtcNow() - proposal).TotalSeconds)
                : 0,
            "s");
        _meter.CreateObservableGauge(
            "hvo.processing_graph_delivery.oldest_pending_fact_age",
            () => state.Snapshot.OldestPendingFactUtc is { } fact
                ? Math.Max(0, (timeProvider.GetUtcNow() - fact).TotalSeconds)
                : 0,
            "s");
    }

    internal void Record(string operation, string outcome, TimeSpan duration)
    {
        TagList tags = default;
        tags.Add("operation", operation);
        tags.Add("outcome", outcome);
        _operations.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    public void Dispose() => _meter.Dispose();
}
