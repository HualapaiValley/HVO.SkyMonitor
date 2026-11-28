using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class CenteredViewTrailPublication(
    RawCaptureIngress ingress, SqliteCenteredViews views, CaptureProcessingPersistence persistence,
    ICameraAgentTransientOperatorProjection trails, ICenteredViewLayerProducer layers,
    IOptions<CameraAgentHostOptions> options, TimeProvider clock, ILogger<CenteredViewTrailPublication> logger)
    : BackgroundService
{
    private const string SnapshotSchema = "cameraagent-centered-trail-evidence-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CenteredViewLayerStyle Style = new(TransientTrailPresentation.LayerKind, 500, false);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, clock);
        do
        {
            try
            {
                if (ingress.CenteredPublicationReady)
                    await PublishReadyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
            {
                ReadFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async ValueTask PublishReadyAsync(CancellationToken token)
    {
        if (!options.Value.ArtifactRead.MeasuredTransientTrailsEnabled)
            return;
        foreach (var work in await views.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false))
        {
            TimeSpan? retry = null;
            try
            {
                if (await ReadEvidenceAsync(work, token).ConfigureAwait(false) is { } evidence)
                {
                    if (evidence.Source is { } source)
                    {
                        await layers.PublishToContributorsAsync(source, Style, work.ObservedUtc, evidence.Incomplete,
                            evidence.Reason, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await PublishUnavailableAsync(work, evidence, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                PublishFailed(logger, work.Exposure.CaptureId, exception);
                retry = RetryDelay;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException)
            {
                PublishFailed(logger, work.Exposure.CaptureId, exception);
            }
            await views.CheckpointTrailWorkAsync(work, retry, token).ConfigureAwait(false);
        }
    }

    internal async ValueTask RebuildForBaseAsync(CenteredViewBase viewBase, ExposureKey exposure, CancellationToken token)
    {
        if (!options.Value.ArtifactRead.MeasuredTransientTrailsEnabled ||
            await views.ReadLatestTrailWorkAsync(exposure, token).ConfigureAwait(false) is not { } work ||
            await ReadEvidenceAsync(work, token).ConfigureAwait(false) is not { } evidence)
            return;

        if (evidence.Source is { } source)
        {
            await layers.PublishAsync(viewBase.BaseId, CenteredViewLayerScope.Contributor, source,
                Style, work.ObservedUtc, evidence.Incomplete, evidence.Reason, token).ConfigureAwait(false);
        }
        else
        {
            await views.PublishLayerAsync(viewBase.BaseId, UnavailableLayer(work, evidence), token).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishUnavailableAsync(CenteredViewTrailWork work, CenteredViewTrailEvidence evidence, CancellationToken token)
    {
        long after = 0;
        while (true)
        {
            var bases = await views.ReadContributorBasesAsync(work.Exposure, after, 24, token, includePending: true).ConfigureAwait(false);
            foreach (var (viewBase, order) in bases)
            {
                await views.PublishLayerAsync(viewBase.BaseId, UnavailableLayer(work, evidence), token).ConfigureAwait(false);
                after = order;
            }
            if (bases.Count < 24)
                return;
        }
    }

    private static CenteredViewLayer UnavailableLayer(CenteredViewTrailWork work, CenteredViewTrailEvidence evidence) =>
        new(new(Style.Kind, CenteredViewLayerScope.Contributor, work.Exposure.CaptureId), work.Revision, evidence.Pin,
            work.ObservedUtc, CenteredViewReadiness.Incompatible, evidence.Reason ?? "centered.trail-geometry-unavailable", null);

    private async ValueTask<CenteredViewTrailEvidence?> ReadEvidenceAsync(CenteredViewTrailWork work, CancellationToken token)
    {
        if (await views.ReadTrailEvidenceAsync(work, token).ConfigureAwait(false) is { } pinned)
            return pinned;

        var raw = await ingress.ReadCenteredRawAsync(work.Exposure, token).ConfigureAwait(false);
        if (raw?.Manifest.CenteredExposure is not { } binding ||
            binding.Source.Camera != work.Exposure.Camera || binding.Source.RunId != work.Exposure.RunId)
            return null;

        var descriptor = raw.Manifest.Descriptor;
        var evidence = await trails.GetCaptureTrailsAsync(work.Exposure.CaptureId, token).ConfigureAwait(false);
        if (await views.ReadLatestTrailWorkAsync(work.Exposure, token).ConfigureAwait(false) != work)
            return null; // A later durable event will project its own snapshot.
        var descriptorHash = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        if (evidence.CaptureId != work.Exposure.CaptureId || evidence.Trails.Any(trail =>
            CaptureContractJson.ComputeDescriptorSha256(trail.SourceManifest.Descriptor) != descriptorHash))
            throw new InvalidDataException("Measured trail evidence differs from its centered source admission.");

        var snapshot = new
        {
            schemaVersion = SnapshotSchema,
            work.Exposure,
            work.Revision,
            work.Stage,
            StageState = work.State,
            evidence.State,
            evidence.Incomplete,
            evidence.Reason,
            descriptorHash,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Timing.ExposureEndedUtc,
            trails = evidence.Trails.Select(trail => new
            {
                trail.CandidateId,
                trail.EventId,
                trail.State,
                trail.CandidateIdentitySha256,
                trail.ExtractionIdentitySha256,
                trail.AssessmentIdentitySha256,
                trail.FinalizationIdentitySha256,
                trail.DetectorInput,
                trail.Geometry
            }).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var checksum = ProcessingIdentity.ComputePayloadSha256(bytes);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            SnapshotSchema, "1.0.0", SnapshotSchema, JsonSerializer.SerializeToElement(new { checksum })));
        Guid[] lineage = [descriptor.Artifact.ArtifactId];
        var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, SnapshotSchema, recipe.IdentitySha256, lineage);
        var snapshotProduct = new ProcessingProduct(FrameArtifactRole.Metadata, SnapshotSchema, identity,
            "application/vnd.hvo.centered-trail-evidence+json", null, bytes, checksum, recipe, [new(SnapshotSchema, "1.0.0")],
            lineage, TimeSpan.Zero, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = SnapshotSchema,
            ContentIdentitySha256 = checksum
        };
        await persistence.PersistCenteredViewProductAsync(descriptor, snapshotProduct, token).ConfigureAwait(false);
        var snapshotId = ProcessingIdentity.CreateArtifactId(identity);
        var pin = new CenteredViewArtifact(snapshotId, identity, checksum);
        var canAssess = evidence.Trails.Count != 0 ||
            !evidence.Incomplete && work is { Stage: "causal-scan", State: "succeeded" } && evidence.State == "NoRecordedCandidate";
        if (!canAssess)
            return await views.PinTrailEvidenceAsync(work, new(null, pin, evidence.Incomplete, evidence.Reason), token)
                .ConfigureAwait(false);

        var snapshotArtifact = new ProcessingArtifact(snapshotId, FrameArtifactRole.Metadata, SnapshotSchema, recipe.IdentitySha256,
            snapshotProduct.MediaType, null, bytes, descriptor.Timing.ExposureEndedUtc, TimeSpan.Zero, snapshotProduct.Compatibility)
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = SnapshotSchema,
            ContentIdentitySha256 = checksum
        };
        var payload = evidence.Trails.Count == 0
            ? PresentationLayerPayloadJson.Create(checksum, descriptor.Layout.Width, descriptor.Layout.Height)
            : TransientTrailPresentation.CreatePayload(checksum, descriptor.Layout.Width, descriptor.Layout.Height,
                evidence.Trails.Select(trail => new TransientMeasuredTrail(trail.Geometry, trail.DetectorInput,
                    trail.State == "ConfirmedMeteor")).ToArray());
        var layer = PresentationProcessingProducts.CreateLayerProduct(payload, "centered-measured-trail-v1", [snapshotArtifact],
            TransientTrailPresentation.ProducerVersion);
        await persistence.PersistCenteredViewProductAsync(descriptor, layer, token).ConfigureAwait(false);
        var slot = new ExposureProductSlot(ProcessingGraphOperationsCoordinator.GetLiveExecutionId(work.Exposure.CaptureId,
            raw.Run.GraphRevisionId), SnapshotSchema, payload.SchemaVersion, layer.Variant);
        var product = new ExposureProductRef(work.Exposure, slot, ProcessingIdentity.CreateArtifactId(layer.OutputIdentitySha256),
            layer.OutputIdentitySha256, layer.ChecksumSha256, layer.ContentIdentitySha256);
        return await views.PinTrailEvidenceAsync(work,
            new(new(work.Exposure, slot, ExposureProductAvailability.Available, work.Revision, product, evidence.Reason),
                pin, evidence.Incomplete, evidence.Reason), token).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The centered trail publisher could not read its durable queue.")]
    private static partial void ReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The measured trails for centered exposure {CaptureId} could not publish.")]
    private static partial void PublishFailed(ILogger logger, Guid captureId, Exception exception);
}
