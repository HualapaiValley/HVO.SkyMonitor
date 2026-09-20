using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class ObjectStoreTelemetry
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.ObjectStorage";
    public const string ActivitySourceName = MeterName;

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Counter<long> _operations = Meter.CreateCounter<long>(
        "skymonitor.central.object_storage.operations", "{operation}");
    private readonly Histogram<double> _duration = Meter.CreateHistogram<double>(
        "skymonitor.central.object_storage.duration", "ms");
    private readonly Counter<long> _bytes = Meter.CreateCounter<long>(
        "skymonitor.central.object_storage.bytes", "By");
    private readonly Counter<long> _retries = Meter.CreateCounter<long>(
        "skymonitor.central.object_storage.retries", "{retry}");
    private readonly UpDownCounter<long> _activeStreams = Meter.CreateUpDownCounter<long>(
        "skymonitor.central.object_storage.active_streams", "{stream}");
    private readonly CentralObjectStorageOptions _options;

    public ObjectStoreTelemetry(IOptions<CentralObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public Activity? StartOperation(string operation, string bucket)
    {
        var activity = ActivitySource.StartActivity("object-store.request", ActivityKind.Client);
        activity?.SetTag("object_store.operation", operation);
        activity?.SetTag("object_store.bucket_role", GetBucketRole(bucket));
        activity?.SetTag("object_store.provider", GetProvider());
        activity?.SetTag("object_store.addressing_style", GetAddressingStyle());
        return activity;
    }

    public void RecordOperation(string operation, string outcome, string bucket, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", outcome },
            { "bucket_role", GetBucketRole(bucket) }
        };
        _operations.Add(1, tags);
        _duration.Record(elapsed.TotalMilliseconds, tags);
        if (Activity.Current?.Source.Name == ActivitySourceName)
        {
            Activity.Current.SetTag("object_store.outcome", outcome);
        }
    }

    public void RecordBytes(string operation, string direction, string bucket, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }
        _bytes.Add(bytes,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("direction", direction),
            new KeyValuePair<string, object?>("bucket_role", GetBucketRole(bucket)));
    }

    public void RecordRetry(string operation, string reason, string bucket)
        => _retries.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("bucket_role", GetBucketRole(bucket)));

    public void ChangeActiveStreams(string direction, long delta)
        => _activeStreams.Add(delta, new KeyValuePair<string, object?>("direction", direction));

    private string GetBucketRole(string bucket)
        => string.Equals(bucket, _options.DiagnosticsBucket, StringComparison.Ordinal)
            ? "diagnostics"
            : "artifact";

    // Provider-neutral tag: every adapter reports which provider served the operation.
    private string GetProvider()
        => _options.Provider switch
        {
            ObjectStorageProvider.Filesystem => "filesystem",
            ObjectStorageProvider.S3 => "s3",
            _ => "unknown"
        };

    // Transport-specific tag; meaningful only for S3 and reported as such for other providers.
    private string GetAddressingStyle()
        => _options.Provider != ObjectStorageProvider.S3
            ? "n/a"
            : _options.S3.AddressingStyle == ObjectStorageAddressingStyle.Path ? "path" : "virtual-host";
}
