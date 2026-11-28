using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Diagnostics;

/// <summary>Measurements for the opt-in standalone CameraAgent profiling profile.</summary>
public static class CameraAgentWorkloadTelemetry
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Workload";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("camera_agent.workload.duration", "s");
    private static readonly Histogram<long> MetadataBytes = Meter.CreateHistogram<long>("camera_agent.workload.scene.metadata", "By",
        advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1024, 8192, 65536, 262144, 1048576, 4194304, 16777216] });
    private static readonly Histogram<long> ThreadAllocatedBytes = Meter.CreateHistogram<long>("camera_agent.workload.thread_allocated", "By",
        advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1024, 8192, 65536, 262144, 1048576, 4194304, 16777216] });
    private static readonly UpDownCounter<long> MaterializationPending = Meter.CreateUpDownCounter<long>(
        "camera_agent.workload.materialization.pending", "{request}");
    private static readonly Counter<long> SvgCache = Meter.CreateCounter<long>("camera_agent.workload.svg.cache", "{event}");

    internal enum Operation
    {
        SqliteWrite,
        SqliteCheckpoint,
        IngressFilePublication,
        SiloPublish,
        SiloReadiness,
        ProjectedSceneSerialization,
        SvgRender,
        CompositorEncoder,
        CurrentSkyRefresh
    }

    internal enum Outcome
    {
        Success,
        Failure,
        Cancelled,
        Busy,
        Locked,
        Incomplete,
        Observed
    }

    internal enum CacheEvent
    {
        Hit,
        Miss,
        Eviction
    }

    private static readonly string[] OperationNames =
    [
        "sqlite_write", "sqlite_checkpoint", "ingress_file_publication", "silo_publish", "silo_readiness",
        "projected_scene_serialization", "svg_render", "compositor_encoder", "current_sky_refresh"
    ];
    private static readonly string[] OutcomeNames = ["success", "failure", "cancelled", "busy", "locked", "incomplete", "observed"];
    private static readonly string[] CacheEventNames = ["hit", "miss", "eviction"];

    internal static long Start() => Duration.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Record(long started, Operation operation, Outcome outcome = Outcome.Success)
    {
        if (started != 0)
        {
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("operation", OperationNames[(int)operation]),
                new KeyValuePair<string, object?>("outcome", OutcomeNames[(int)outcome]));
        }
    }

    internal static void RecordSceneBytes(int bytes) => MetadataBytes.Record(bytes);

    internal static long StartAllocation() => ThreadAllocatedBytes.Enabled ? GC.GetAllocatedBytesForCurrentThread() : -1;

    // Only use around synchronous work on the same thread.
    internal static void RecordAllocation(long started, Operation operation)
    {
        if (started >= 0)
        {
            ThreadAllocatedBytes.Record(GC.GetAllocatedBytesForCurrentThread() - started,
                new KeyValuePair<string, object?>("operation", OperationNames[(int)operation]));
        }
    }

    internal static void RecordMaterializationPending(int change) => MaterializationPending.Add(change);

    internal static ProcessingProduct MaterializePacked(
        ProcessingArtifact baseArtifact,
        ProcessingArtifact manifestArtifact,
        OverlayManifestV1 manifest,
        IReadOnlyList<PresentationLayerProductInput> layerProducts,
        IEnumerable<string> enabledLayers,
        string outputVariant,
        CancellationToken cancellationToken)
    {
        var started = Start();
        var outcome = Outcome.Failure;
        try
        {
            var product = PresentationMaterializationExecutor.MaterializePacked(
                baseArtifact, manifestArtifact, manifest, layerProducts, enabledLayers, outputVariant,
                cancellationToken: cancellationToken);
            outcome = Outcome.Success;
            return product;
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.Cancelled;
            throw;
        }
        finally
        {
            Record(started, Operation.CompositorEncoder, outcome);
        }
    }

    internal static void RecordCache(CacheEvent cacheEvent, int count = 1)
        => SvgCache.Add(count, new KeyValuePair<string, object?>("event", CacheEventNames[(int)cacheEvent]));
}
