using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class OperatorUiTelemetry : IDisposable
{
    internal const string MeterName = "HVO.SkyMonitor.LogicHost.OperatorUi";
    internal const string ActivitySourceName = MeterName;
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> reads;
    private readonly Histogram<double> readDuration;
    private readonly Histogram<long> readRows;
    private readonly Histogram<long> responseBytes;
    private readonly Counter<long> mutations;
    private readonly Histogram<double> mutationDuration;

    public OperatorUiTelemetry()
    {
        reads = meter.CreateCounter<long>("hvo.operator_ui.reads", "{read}");
        readDuration = meter.CreateHistogram<double>("hvo.operator_ui.read.duration", "ms");
        readRows = meter.CreateHistogram<long>("hvo.operator_ui.read.rows", "{row}");
        responseBytes = meter.CreateHistogram<long>("hvo.operator_ui.response.bytes", "By");
        mutations = meter.CreateCounter<long>("hvo.operator_ui.mutations", "{mutation}");
        mutationDuration = meter.CreateHistogram<double>("hvo.operator_ui.mutation.duration", "ms");
    }

    public static Activity? StartRead(string operation)
    {
        var activity = ActivitySource.StartActivity("operator-ui.read", ActivityKind.Internal);
        activity?.SetTag("operator_ui.operation", operation);
        return activity;
    }

    public static Activity? StartMutation(string operation)
    {
        var activity = ActivitySource.StartActivity("operator-ui.mutate", ActivityKind.Internal);
        activity?.SetTag("operator_ui.operation", operation);
        return activity;
    }

    public static Activity? StartRawDownload()
        => ActivitySource.StartActivity("operator-ui.raw-download", ActivityKind.Internal);

    public void RecordRead(string operation, string audience, string outcome, long rows, long bytes, TimeSpan elapsed)
    {
        TagList tags = new() { { "operation", operation }, { "audience", audience }, { "outcome", outcome } };
        reads.Add(1, tags);
        readDuration.Record(elapsed.TotalMilliseconds, tags);
        readRows.Record(rows, tags);
        if (bytes > 0) responseBytes.Record(bytes, tags);
    }

    public void RecordMutation(string operation, string outcome, string role, TimeSpan elapsed)
    {
        TagList tags = new() { { "operation", operation }, { "outcome", outcome }, { "role", role } };
        mutations.Add(1, tags);
        mutationDuration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordResponseBytes(string operation, string audience, string outcome, long bytes)
    {
        TagList tags = new() { { "operation", operation }, { "audience", audience }, { "outcome", outcome } };
        responseBytes.Record(bytes, tags);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        meter.Dispose();
    }
}
