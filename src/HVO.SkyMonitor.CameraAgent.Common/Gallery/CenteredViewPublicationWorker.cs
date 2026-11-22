using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// Durable work is read off the acquisition and processing paths. In-process wakes
// are only hints, including on startup and when a profile changes.
internal sealed partial class CenteredViewPublicationWorker(
    CaptureScheduleRuntimeCoordinator schedule, RawCaptureIngress ingress,
    ICenteredExposureRuntimeReader runtime, IExposureProductReader products, CenteredExposureProductChanges changes,
    SqliteCenteredViews views, ICenteredViewProducer producer, CenteredViewSourceLayers layers,
    TimeProvider clock, ILogger<CenteredViewPublicationWorker> logger) : BackgroundService
{
    private const int BatchSize = 24;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly CenteredViewPreviewOptions PreviewOptions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var version = changes.Version;
            var context = schedule.Snapshot is null ? (CaptureScheduleCaptureContext?)null : schedule.CaptureContext;
            var more = false;
            try
            {
                if (context is { } current && ingress.CenteredPublicationReady)
                {
                    var camera = CenteredViewLookup.ResolveCamera(current.Snapshot);
                    var active = camera is not null && current.Snapshot.CurrentDecision is not { Admitted: false }
                        && await runtime.ReadCurrentAsync(camera, stoppingToken).ConfigureAwait(false) is
                        { Basis: CenteredRuntimeEvidenceBasis.ActiveAcquisition } acquisition
                        ? CenteredViewLookup.ResolveStream(acquisition)
                        : null;
                    foreach (var known in await views.ReadPublicationCamerasAsync(stoppingToken).ConfigureAwait(false))
                        more |= await DrainCameraAsync(known, active, current.RevisionChanged, stoppingToken).ConfigureAwait(false);
                    if (active is not null && !current.RevisionChanged.IsCancellationRequested &&
                        await views.ReadLiveCandidateAsync(active, stoppingToken).ConfigureAwait(false) is { } candidate)
                        await views.SelectLiveAsync(active, candidate, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
            {
                ReadFailed(logger, exception);
            }

            if (!more)
            {
                using var interval = new CancellationTokenSource(PollInterval, clock);
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, interval.Token,
                    context?.RevisionChanged ?? CancellationToken.None);
                try
                {
                    await changes.WaitForChangeAsync(version, wake.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && wake.IsCancellationRequested)
                {
                    // A deadline or revised profile is also a wake.
                }
            }
        }
    }

    internal async ValueTask<bool> DrainCameraAsync(CameraKey camera, CenteredViewStream? active,
        CancellationToken revisionChanged, CancellationToken token)
    {
        var cursor = await views.ReadProductCursorAsync(camera, token).ConfigureAwait(false);
        var batch = await products.ReadProductsAfterAsync(camera, cursor, BatchSize, token).ConfigureAwait(false);
        foreach (var item in batch)
            await HandleAndCheckpointAsync(camera, item, active, advanceCursor: true, revisionChanged, token).ConfigureAwait(false);

        foreach (var revision in await views.ReadDueProductRetriesAsync(camera, BatchSize, token).ConfigureAwait(false))
        {
            var item = (await products.ReadProductsAfterAsync(camera, revision - 1, 1, token).ConfigureAwait(false)).SingleOrDefault();
            if (item?.Revision != revision)
            {
                Unavailable(logger, revision, "centered.product-not-retained");
                await views.CheckpointProductAsync(camera, revision, null, advanceCursor: false, token).ConfigureAwait(false);
            }
            else
            {
                await HandleAndCheckpointAsync(camera, item, active, advanceCursor: false, revisionChanged, token).ConfigureAwait(false);
            }
        }
        return batch.Count == BatchSize;
    }

    private async ValueTask HandleAndCheckpointAsync(CameraKey camera, ExposureProductLookup item, CenteredViewStream? active,
        bool advanceCursor, CancellationToken revisionChanged, CancellationToken token)
    {
        if (item.Exposure.Camera != camera || item.Revision is not > 0)
            throw new InvalidDataException("The centered product cursor returned an invalid camera or revision.");
        var revision = item.Revision.Value;
        TimeSpan? retry = null;
        try
        {
            await HandleProductAsync(item, active, revisionChanged, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            ProductRetry(logger, revision, exception);
            retry = RetryDelay;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            ProductRejected(logger, revision, exception);
        }
        // Publication is idempotent if cancellation or a crash happens after commit but before acknowledgement.
        await views.CheckpointProductAsync(camera, revision, retry, advanceCursor, token).ConfigureAwait(false);
    }

    private async ValueTask HandleProductAsync(ExposureProductLookup item, CenteredViewStream? active,
        CancellationToken revisionChanged, CancellationToken token)
    {
        var isLinear = item.Slot.SchemaVersion == CenteredExposureProductReader.LinearSchemaVersion;
        if (!isLinear && !CenteredViewLayerBindings.SupportsSchema(CenteredViewLayerBindings.SourceSchema(item.Slot.SchemaVersion)))
            return;
        var raw = await ingress.ReadCenteredRawAsync(item.Exposure, token).ConfigureAwait(false);
        if (raw?.Manifest.CenteredExposure is not { } binding)
        {
            Unavailable(logger, item.Revision!.Value, "centered.admission-not-retained");
            return;
        }
        var executionId = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(item.Exposure.CaptureId, raw.Run.GraphRevisionId);
        if (item.Slot.ExecutionId != executionId)
            return;
        var stream = new CenteredViewStream(item.Exposure.Camera, item.Exposure.RunId, raw.Run.GraphRevisionId);
        if (!isLinear)
        {
            await layers.RebuildForProductAsync(item, raw.Run.GraphRevisionId,
                raw.Manifest.Descriptor.Timing.ExposureEndedUtc, token).ConfigureAwait(false);
            return;
        }

        var linear = await products.ReadLinearAsync(item.Exposure, token).ConfigureAwait(false);
        if (linear?.Revision != item.Revision)
            return;
        var single = await producer.PublishSingleAsync(stream, executionId, raw.Manifest.Descriptor.Capture.CaptureSequence,
            linear!, PreviewOptions, token).ConfigureAwait(false);
        if (single is null)
            return;

        // The first three attempts may display singles; later ones may be read in
        // explicit single mode but must not jump ahead of the centered reference.
        if (stream == active && !revisionChanged.IsCancellationRequested && binding.Source.Ordinal <= 3)
            await views.SelectLiveAsync(stream, single.Base.BaseId, token).ConfigureAwait(false);
        await layers.RebuildForBaseAsync(single.Base, token).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The centered publication worker could not read durable work.")]
    private static partial void ReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Centered product revision {Revision} could not publish and will retry.")]
    private static partial void ProductRetry(ILogger logger, long revision, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Centered product revision {Revision} was rejected by publication.")]
    private static partial void ProductRejected(ILogger logger, long revision, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Centered product revision {Revision} could not publish: {Reason}.")]
    private static partial void Unavailable(ILogger logger, long revision, string reason);
}
