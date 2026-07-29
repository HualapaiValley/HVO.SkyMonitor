using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed partial class CameraAgentOperatorTelemetry : IDisposable
{
    internal const string InstrumentationName = "HVO.SkyMonitor.CameraAgent.Operator";
    private readonly Meter _meter = new(InstrumentationName);
    private readonly ActivitySource _activities = new(InstrumentationName);
    private readonly Counter<long> _planPreviews;
    private readonly Counter<long> _planMutations;
    private readonly Counter<long> _comparisonRequests;
    private readonly Histogram<double> _planPreviewDuration;
    private readonly Histogram<double> _comparisonDuration;
    private readonly Histogram<long> _comparisonOutputBytes;
    private readonly Counter<long> _transientQueries;
    private readonly Histogram<double> _transientQueryDuration;
    private readonly Histogram<long> _transientResponseBytes;
    private readonly ILogger _logger;

    public CameraAgentOperatorTelemetry(ILogger<CameraAgentOperatorTelemetry>? logger = null)
    {
        _logger = logger ?? NullLogger<CameraAgentOperatorTelemetry>.Instance;
        _planPreviews = _meter.CreateCounter<long>("camera_agent.processing.plan.previews", "{preview}");
        _planMutations = _meter.CreateCounter<long>("camera_agent.processing.plan.mutations", "{mutation}");
        _comparisonRequests = _meter.CreateCounter<long>("camera_agent.processing.comparison.requests", "{request}");
        _planPreviewDuration = _meter.CreateHistogram<double>(
            "camera_agent.processing.plan.preview.duration", "s");
        _comparisonDuration = _meter.CreateHistogram<double>("camera_agent.processing.comparison.duration", "s");
        _comparisonOutputBytes = _meter.CreateHistogram<long>("camera_agent.processing.comparison.output.bytes", "By");
        _transientQueries = _meter.CreateCounter<long>("hvo.transient.operator.queries");
        _transientQueryDuration = _meter.CreateHistogram<double>("hvo.transient.operator.query.duration", "ms");
        _transientResponseBytes = _meter.CreateHistogram<long>("hvo.transient.operator.response.bytes", "By");
    }

    internal Activity? StartPlanPreview() => _activities.StartActivity("processing-plan.preview");

    internal Activity? StartPlanMutation(string action) => _activities.StartActivity($"processing-plan.{action}");

    internal Activity? StartArtifactComparison() => _activities.StartActivity("artifact.compare");

    internal Activity? StartTransientQuery() => _activities.StartActivity("transient.operator.query");

    internal void RecordPlanPreview(TimeSpan duration, string outcome)
    {
        var tags = new TagList { { "outcome", outcome } };
        _planPreviews.Add(1, tags);
        _planPreviewDuration.Record(duration.TotalSeconds, tags);
        PlanPreviewCompleted(_logger, outcome);
    }

    internal void RecordPlanMutation(string action, string outcome)
    {
        _planMutations.Add(1, new TagList { { "action", action }, { "outcome", outcome } });
        PlanMutationCompleted(_logger, action, outcome);
    }

    internal void RecordComparison(TimeSpan duration, long outputBytes, string outcome)
    {
        var tags = new TagList { { "outcome", outcome } };
        _comparisonRequests.Add(1, tags);
        _comparisonDuration.Record(duration.TotalSeconds, tags);
        _comparisonOutputBytes.Record(outputBytes, tags);
        ComparisonCompleted(_logger, outcome);
    }

    internal void RecordTransientQuery(TimeSpan duration, long responseBytes, string outcome)
    {
        outcome = outcome switch
        {
            "success" or "unauthorized" or "not_found" or "invalid" or "unavailable" or "cancelled" => outcome,
            _ => "unavailable"
        };
        var tags = new TagList { { "outcome", outcome } };
        _transientQueries.Add(1, tags);
        _transientQueryDuration.Record(duration.TotalMilliseconds, tags);
        _transientResponseBytes.Record(responseBytes, tags);
        TransientQueryCompleted(_logger, outcome);
    }

    public void Dispose()
    {
        _meter.Dispose();
        _activities.Dispose();
    }

    [LoggerMessage(2530, LogLevel.Information, "Processing plan preview completed with outcome {Outcome}")]
    private static partial void PlanPreviewCompleted(ILogger logger, string outcome);

    [LoggerMessage(2531, LogLevel.Information,
        "Processing plan mutation {Action} completed with outcome {Outcome}")]
    private static partial void PlanMutationCompleted(ILogger logger, string action, string outcome);

    [LoggerMessage(2532, LogLevel.Information, "Artifact comparison completed with outcome {Outcome}")]
    private static partial void ComparisonCompleted(ILogger logger, string outcome);

    [LoggerMessage(2533, LogLevel.Information, "Transient operator query completed with outcome {Outcome}")]
    private static partial void TransientQueryCompleted(ILogger logger, string outcome);
}
