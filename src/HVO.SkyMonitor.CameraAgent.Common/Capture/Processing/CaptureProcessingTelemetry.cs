using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed class CaptureProcessingTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.ProcessingGraph";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.ProcessingGraph";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _graphs;
    private readonly Counter<long> _nodes;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _outputs;
    private readonly Counter<long> _outputBytes;
    private readonly Counter<long> _recovered;
    private readonly Histogram<double> _validationDuration;
    private readonly Histogram<double> _dependencyWaitDuration;
    private readonly Histogram<double> _recipeDuration;
    private readonly Histogram<double> _persistenceDuration;
    private readonly Histogram<double> _graphDuration;
    private readonly CaptureProcessingState _state;
    private readonly FleetRuntimeState? _fleetRuntimeState;

    public CaptureProcessingTelemetry(CaptureProcessingState? state = null, FleetRuntimeState? fleetRuntimeState = null)
    {
        _state = state ?? new CaptureProcessingState();
        _fleetRuntimeState = fleetRuntimeState;
        _graphs = _meter.CreateCounter<long>("camera_agent.processing.graphs", "{graph}");
        _nodes = _meter.CreateCounter<long>("camera_agent.processing.nodes", "{node}");
        _outcomes = _meter.CreateCounter<long>("camera_agent.processing.outcomes", "{outcome}");
        _outputs = _meter.CreateCounter<long>("camera_agent.processing.outputs", "{output}");
        _outputBytes = _meter.CreateCounter<long>("camera_agent.processing.output.bytes", "By");
        _recovered = _meter.CreateCounter<long>("camera_agent.processing.recovered", "{output}");
        _validationDuration = _meter.CreateHistogram<double>("camera_agent.processing.validation.duration", "s");
        _dependencyWaitDuration = _meter.CreateHistogram<double>("camera_agent.processing.dependency_wait.duration", "s");
        _recipeDuration = _meter.CreateHistogram<double>("camera_agent.processing.recipe.duration", "s");
        _persistenceDuration = _meter.CreateHistogram<double>("camera_agent.processing.persistence.duration", "s");
        _graphDuration = _meter.CreateHistogram<double>("camera_agent.processing.graph.duration", "s");
        _meter.CreateObservableGauge("camera_agent.processing.pending", () => _state.Snapshot.PendingCount, "{graph}");
        _meter.CreateObservableGauge("camera_agent.processing.retry", () => _state.Snapshot.RetryCount, "{node}");
        _meter.CreateObservableGauge("camera_agent.processing.oldest.age", ObserveOldestAge, "s");
    }

    internal void RecordValidation(int nodeCount, TimeSpan duration)
    {
        _validationDuration.Record(duration.TotalSeconds);
        _graphs.Add(1, new KeyValuePair<string, object?>("outcome", "validated"));
        _nodes.Add(nodeCount, new KeyValuePair<string, object?>("outcome", "validated"));
    }

    internal void GraphStarted()
    {
        _state.GraphStarted();
    }

    internal void RecordGraph(string outcome, TimeSpan duration)
    {
        _state.GraphCompleted(outcome);
        _fleetRuntimeState?.ProcessingCompleted(duration);
        _graphs.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _graphDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
    }

    internal void RecordDependencyWait(CaptureProcessingGraphNode node, TimeSpan duration)
        => _dependencyWaitDuration.Record(duration.TotalSeconds, NodeTags(node, "ready"));

    internal void RecordNode(
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        TimeSpan duration)
    {
        var outcome = Outcome(status);
        var tags = NodeTags(node, outcome, reason);
        _nodes.Add(1, tags);
        _outcomes.Add(1, tags);
        if (node.RecipeName is not null)
        {
            _recipeDuration.Record(duration.TotalSeconds, tags);
        }
    }

    internal void RecordPersistence(CaptureProcessingGraphNode node, ProcessingProduct product, TimeSpan duration)
    {
        var tags = NodeTags(node, "produced");
        tags.Add("role", product.Role.ToString());
        tags.Add("variant", product.Variant);
        _persistenceDuration.Record(duration.TotalSeconds, tags);
        _outputs.Add(1, tags);
        _outputBytes.Add(product.Payload.Length, tags);
    }

    internal void RecordRecovered(FrameArtifactRole role, string variant)
        => _recovered.Add(1, new TagList { { "role", role.ToString() }, { "variant", variant } });

    private Measurement<double> ObserveOldestAge()
    {
        var started = _state.Snapshot.OldestPendingUtc;
        return new(started is null ? 0 : Math.Max(
            0,
            (DateTimeOffset.UtcNow - started.Value).TotalSeconds));
    }

    private static TagList NodeTags(
        CaptureProcessingGraphNode node,
        string outcome,
        string? reason = null)
    {
        var tags = new TagList
        {
            { "step", node.Id },
            { "recipe", node.RecipeName ?? "infrastructure" },
            { "required", node.Required },
            { "outcome", outcome }
        };
        if (reason is not null)
        {
            tags.Add("reason", reason);
        }
        return tags;
    }

    private static string Outcome(DurableProcessingNodeStatus status) => status switch
    {
        DurableProcessingNodeStatus.Completed => "completed",
        DurableProcessingNodeStatus.Skipped => "skipped",
        DurableProcessingNodeStatus.RetryableFailure => "retry",
        DurableProcessingNodeStatus.TerminalFailure => "terminal",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public void Dispose() => _meter.Dispose();
}
