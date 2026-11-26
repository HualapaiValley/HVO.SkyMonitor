using System.Runtime.ExceptionServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public sealed record CenteredViewLayerStyle(
    string Kind, int ZOrder, bool EnabledByDefault,
    PresentationBlendMode BlendMode = PresentationBlendMode.Normal, int OpacityMillionths = 1_000_000);

/// <summary>Projects a committed source-pixel payload onto exact retained bases.</summary>
public interface ICenteredViewLayerProducer
{
    ValueTask<CenteredView?> PublishAsync(string baseId, CenteredViewLayerScope scope,
        ExposureProductLookup source, CenteredViewLayerStyle style, DateTimeOffset observedUtc,
        bool isPartial = false, string? reason = null, CancellationToken cancellationToken = default);

    ValueTask PublishToContributorsAsync(ExposureProductLookup source,
        CenteredViewLayerStyle style, DateTimeOffset observedUtc, bool isPartial = false, string? reason = null,
        CancellationToken cancellationToken = default);
}

internal sealed class CenteredViewLayerProducer(
    SqliteCenteredViews views, CaptureProcessingPersistence persistence,
    ICameraAgentRetainedAstrometryService? astrometry = null,
    ICenteredWindowRegistrationSource? registration = null) : ICenteredViewLayerProducer
{
    private const string GeometryVersion = "centered-view-layer-mapping-v1";

    public async ValueTask<CenteredView?> PublishAsync(string baseId, CenteredViewLayerScope scope,
        ExposureProductLookup source, CenteredViewLayerStyle style, DateTimeOffset observedUtc,
        bool isPartial = false, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        var viewBase = await views.ReadBaseAsync(baseId, cancellationToken).ConfigureAwait(false);
        if (viewBase is null)
            return null;
        var evidence = await ReadEvidenceAsync(source, style, cancellationToken).ConfigureAwait(false);
        return await PublishAsync(viewBase, scope, evidence, style, observedUtc, isPartial, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask PublishToContributorsAsync(ExposureProductLookup source,
        CenteredViewLayerStyle style, DateTimeOffset observedUtc, bool isPartial = false, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        var evidence = await ReadEvidenceAsync(source, style, cancellationToken).ConfigureAwait(false);
        const int batchSize = 32;
        long afterOrder = 0;
        IOException? retryableFailure = null;
        while (true)
        {
            var bases = await views.ReadContributorBasesAsync(source.Exposure, afterOrder, batchSize, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (viewBase, order) in bases)
            {
                try
                {
                    await PublishAsync(viewBase, CenteredViewLayerScope.Contributor, evidence, style, observedUtc,
                        isPartial, reason, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    retryableFailure ??= exception;
                }
                afterOrder = order;
            }
            if (bases.Count < batchSize)
                break;
        }
        // The durable worker will retry the source. Committed layer revisions are idempotent.
        if (retryableFailure is not null)
            ExceptionDispatchInfo.Throw(retryableFailure);
    }

    private async ValueTask<CenteredView?> PublishAsync(CenteredViewBase viewBase, CenteredViewLayerScope scope,
        SourceLayer evidence, CenteredViewLayerStyle style, DateTimeOffset observedUtc, bool partial, string? reason,
        CancellationToken token)
    {
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope));
        var definition = viewBase.Definition;
        var source = evidence.Lookup.Exposure;
        CenteredViewContributor? contributor = null;
        if (scope == CenteredViewLayerScope.Contributor)
        {
            contributor = definition.Contributors.SingleOrDefault(member => member.Exposure.Input.Key == source);
            if (contributor is null)
                return null;
        }
        else if (source != definition.Reference.Input.Key || scope == CenteredViewLayerScope.Stack &&
            (definition.Kind != CenteredViewKind.Stack || evidence.Artifact.SourceArtifactIds?.Contains(definition.Science.ArtifactId) != true))
        {
            throw new InvalidDataException("Presentation evidence does not bind the reference or the registered stack.");
        }

        var key = new CenteredViewLayerKey(style.Kind, scope, scope == CenteredViewLayerScope.Stack ? null : source.CaptureId);
        if (evidence.Astrometry is { } fit)
        {
            if (scope != CenteredViewLayerScope.Reference || fit.Solution?.Assessment.Frame.SourceDescriptorSha256 !=
                CaptureContractJson.ComputeDescriptorSha256(definition.Reference.Input.Raw))
                throw new InvalidDataException("The astrometric diagnostic has no exact reference geometry.");
            if (fit.Diagnostics is null)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible, fit.Reason ?? fit.DiagnosticsReason, null), token).ConfigureAwait(false);
        }
        var sourceExposure = contributor?.Exposure ?? definition.Reference;
        if (evidence.Satellite is { } satellite)
        {
            if (contributor is null || satellite.Exposure.StartUtc != sourceExposure.ExposureStartedUtc ||
                satellite.Exposure.EndUtc != sourceExposure.ExposureEndedUtc)
                throw new InvalidDataException("The satellite status does not match the contributing exposure.");
            reason ??= satellite.Reason;
            partial |= satellite.Status == "Partial";
            if (!satellite.Renderable)
            {
                var readiness = satellite is { Status: "Complete", TrackCount: 0 }
                    ? CenteredViewReadiness.AssessedEmpty : CenteredViewReadiness.Incompatible;
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, readiness, reason, null), token).ConfigureAwait(false);
            }
        }
        IImageRegistrationMap? geometry;
        try
        {
            geometry = contributor is null ? null : await ResolveDirectMapAsync(definition, contributor, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (contributor is not null && exception is InvalidDataException or JsonException)
        {
            // A retained base can lose or reject its own registration parents.
            // Settle that binding; do not suppress other bases for this source.
            return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible, "centered.registration-map-unavailable", null), token)
                .ConfigureAwait(false);
        }
        if (evidence.Payload.WidthPixels != sourceExposure.Input.Geometry.Layout.Width ||
            evidence.Payload.HeightPixels != sourceExposure.Input.Geometry.Layout.Height)
            throw new InvalidDataException("Presentation evidence does not have the source's delivered-image geometry.");

        if (evidence.Measurement is { } measurement)
        {
            _ = ExposureMeasurementProducts.VerifyRawBinding(measurement, sourceExposure.Input.Raw);
            if (scope != CenteredViewLayerScope.Reference || measurement.Input.Exposure != sourceExposure.Input.Key ||
                measurement.Input.Linear?.ArtifactId != sourceExposure.Linear?.ArtifactId ||
                measurement.Input.Linear?.OutputIdentitySha256 != sourceExposure.Linear?.IdentitySha256 ||
                measurement.Input.Linear?.PayloadSha256 != sourceExposure.Linear?.PayloadSha256)
                throw new InvalidDataException("Measured markers do not match the exact admitted reference and linear product.");
            if (measurement.Sources.Status is ExposureMeasurementStatus.Unsuitable or ExposureMeasurementStatus.Unavailable)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible, measurement.Sources.Reason, null), token)
                    .ConfigureAwait(false);
            partial |= measurement.Sources.Detections.Count > evidence.Payload.Markers.Count;
        }
        if (evidence.Associations is { } associations)
        {
            if (scope != CenteredViewLayerScope.Reference ||
                associations.Source.CaptureId != sourceExposure.Capture.CaptureId ||
                associations.Source.ArtifactId != sourceExposure.Input.Raw.Artifact.ArtifactId ||
                !string.Equals(associations.Source.DescriptorIdentitySha256,
                    CaptureContractJson.ComputeDescriptorSha256(sourceExposure.Input.Raw), StringComparison.OrdinalIgnoreCase) ||
                associations.Source.WidthPixels != sourceExposure.Input.Geometry.Layout.Width ||
                associations.Source.HeightPixels != sourceExposure.Input.Geometry.Layout.Height)
                throw new InvalidDataException("Measured associations do not match the exact admitted reference exposure.");
            if (style.Kind == MeasuredAssociationPresentation.MeasuredLayerKind &&
                associations.Measurement.Status != StellarMeasurementStatus.Completed)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible,
                    MeasuredStellarAssociationReasonCodes.MeasurementIncomplete, null), token).ConfigureAwait(false);
            var totalMeasured = associations.Associations.Count +
                (associations.PointSourcePlanets?.Associations.Count ?? 0) +
                associations.UnassociatedDetections.Count;
            if (style.Kind == MeasuredAssociationPresentation.MeasuredLayerKind && totalMeasured > evidence.Payload.Markers.Count)
                partial = true;
        }
        if (evidence.RegionalVisibility is { } visibility)
        {
            if (scope != CenteredViewLayerScope.Reference ||
                visibility.Sources.CaptureId != sourceExposure.Capture.CaptureId ||
                visibility.Sources.RawArtifactId != sourceExposure.Input.Raw.Artifact.ArtifactId ||
                visibility.Sources.RawDescriptorSha256 != CaptureContractJson.ComputeDescriptorSha256(sourceExposure.Input.Raw))
                throw new InvalidDataException("Regional stellar visibility does not match the exact admitted reference exposure.");
            if (visibility.Status == RegionalVisibilityAssessmentStatus.Withheld)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible,
                    visibility.ReasonCodes.Count > 0 ? visibility.ReasonCodes[0] : RegionalVisibilityReasonCodes.PointingUncertain, null), token)
                    .ConfigureAwait(false);
            if (visibility.Status == RegionalVisibilityAssessmentStatus.InsufficientEvidence)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.AssessedEmpty,
                    visibility.ReasonCodes.Count > 0 ? visibility.ReasonCodes[0] : RegionalVisibilityReasonCodes.InsufficientSupport, null), token)
                    .ConfigureAwait(false);
        }

        PresentationLayerPayloadV1 mapped;
        try
        {
            mapped = MapPayload(evidence, definition.Preview.Transform, geometry, contributor is not null,
                source.CaptureId == definition.Reference.Capture.CaptureId);
        }
        catch (ArgumentException) when (evidence.Payload.RichText is not null)
        {
            // The source already passed delivered-geometry validation. Settle the unrepresentable
            // preview before the worker acknowledges the available source revision.
            return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible, "centered.rich-text-preview-too-small", null), token)
                .ConfigureAwait(false);
        }
        if (contributor is not null && evidence.Payload.Segments.Count > mapped.Segments.Count)
        {
            if (mapped.Segments.Count == 0)
                return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                    evidence.Pin, observedUtc, CenteredViewReadiness.Incompatible, "centered.segments-outside-registration", null), token)
                    .ConfigureAwait(false);
            partial = true;
        }
        if (mapped.Markers.Count == 0 && mapped.Segments.Count == 0 && mapped.Ellipses.Count == 0 &&
            mapped.TextBlocks.Count == 0 && mapped.TileMask is null && mapped.RichText is null)
        {
            var emptyReason = reason ?? (evidence.Associations is null
                ? null
                : style.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind
                    ? "expected.all-predictions-matched"
                    : "measured.no-detected-sources");
            return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value,
                evidence.Pin, observedUtc, CenteredViewReadiness.AssessedEmpty, emptyReason, null), token).ConfigureAwait(false);
        }

        var options = evidence.Associations is not null
            ? JsonSerializer.SerializeToElement(new
            {
                viewBase.BaseId,
                GeometryVersion,
                layerKind = style.Kind,
                sourceCaptureId = source.CaptureId,
                sourceExposure.ExposureStartedUtc,
                sourceExposure.ExposureEndedUtc,
                evidence.Pin,
                evidence.Lookup.Revision
            })
            : JsonSerializer.SerializeToElement(new
            {
                viewBase.BaseId,
                GeometryVersion,
                sourceCaptureId = source.CaptureId,
                sourceExposure.ExposureStartedUtc,
                sourceExposure.ExposureEndedUtc,
                evidence.Pin,
                evidence.Lookup.Revision
            });
        var eligibility = CaptureContractJson.ComputeCanonicalJsonSha256(options);
        var variant = evidence.Associations is not null ? $"{GeometryVersion}:{style.Kind}" : GeometryVersion;
        var product = PresentationProcessingProducts.CreateLayerProduct(mapped, variant, [evidence.Artifact],
            GeometryVersion, eligibility);
        await persistence.PersistCenteredViewProductAsync(definition.Reference.Input.Raw, product, token).ConfigureAwait(false);
        var sourceProduct = new PresentationProductReference(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            mapped.ContentIdentitySha256, product.MediaType, definition.Preview.Product.Compatibility);
        var renderer = mapped.SchemaVersion switch
        {
            PresentationLayerPayloadV1.SceneStyleSchemaVersion => PresentationLayerCompositor.SceneStyleAlgorithmVersion,
            PresentationLayerPayloadV1.RichTextSchemaVersion => PresentationLayerCompositor.RichTextAlgorithmVersion,
            _ => PresentationLayerCompositor.AlgorithmVersion
        };
        var layer = LayeredPresentationJson.CreateLayer(style.Kind, sourceProduct, null, PresentationCoordinateSpace.ScenePixels,
            renderer, mapped.SchemaVersion == PresentationLayerPayloadV1.SceneStyleSchemaVersion
                ? SceneStylePresentation.ProducerVersion : GeometryVersion,
            style.ZOrder, style.BlendMode, style.OpacityMillionths, style.EnabledByDefault, options);
        return await views.PublishLayerAsync(viewBase.BaseId, new(key, evidence.Lookup.Revision!.Value, evidence.Pin,
            observedUtc, partial ? CenteredViewReadiness.Partial : CenteredViewReadiness.Ready, reason, layer), token)
            .ConfigureAwait(false);
    }

    private async ValueTask<SourceLayer> ReadEvidenceAsync(ExposureProductLookup source, CenteredViewLayerStyle style, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not { Availability: ExposureProductAvailability.Available, Revision: > 0, Product: { } product } ||
            product.Exposure != source.Exposure || product.Slot != source.Slot ||
            (!PresentationLayerPayloadV1.SupportsSchema(product.Slot.SchemaVersion) &&
                product.Slot.SchemaVersion is not (ExposureMeasurementProducts.SchemaVersion or MeasuredStellarAssociationsV1.CurrentSchemaVersion or RegionalStellarVisibilityProducts.SchemaVersion or MeasuredAstrometrySolutionV1.CurrentSchemaVersion)))
            throw new InvalidDataException("A drawable layer requires a committed source-pixel payload.");
        var pin = new CenteredViewArtifact(product.ArtifactId, product.OutputIdentitySha256, product.PayloadSha256);
        if (product.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)
        {
            var (measurementArtifact, measurement) = await persistence.ReadCenteredViewMeasurementAsync(source.Exposure.CaptureId, pin, token)
                .ConfigureAwait(false);
            if (measurementArtifact.ContentIdentitySha256 is not { } identity || identity != product.ContentIdentitySha256)
                throw new InvalidDataException("The exposure measurement content identity is unbound.");
            var layout = measurement.Input.Raw.Layout;
            var markers = measurement.Sources.Detections.Take(512).Select(detection =>
                new PresentationMarkerV1(detection.Pixel, 3, new(200, 240, 255), Crosshair: true));
            return new(source, pin, measurementArtifact, PresentationLayerPayloadJson.Create(
                identity, layout.Width, layout.Height, markers: markers), measurement);
        }
        if (product.Slot.SchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion)
        {
            var associationsArtifact = await persistence.ReadCenteredViewProductAsync(source.Exposure.CaptureId, pin, token)
                .ConfigureAwait(false);
            var associations = MeasuredStellarAssociationJson.Parse(associationsArtifact.Payload).Associations;
            if (associations is null || associationsArtifact.ContentIdentitySha256 is not { } identity ||
                !string.Equals(identity, product.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity, associations.AssociationIdentitySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The measured associations content identity is unbound.");
            ProjectedSceneV1? scene = null;
            if (associationsArtifact.SourceArtifactIds is [_, _, var sceneArtifactId])
            {
                var sceneArtifact = await persistence.ReadCenteredViewParentAsync(source.Exposure.CaptureId, sceneArtifactId, token)
                    .ConfigureAwait(false);
                if (!ProjectedSceneV1.IsSupportedSchemaVersion(sceneArtifact.SchemaVersion) ||
                    ProjectedSceneJson.Parse(sceneArtifact.Payload).Scene is not { } parsedScene ||
                    !string.Equals(parsedScene.SceneIdentitySha256, associations.Scene.SceneIdentitySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The measured associations scene lineage is invalid.");
                scene = parsedScene;
            }
            var associationPayload = style.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind
                ? MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(associations, scene)
                : associations.Measurement.Status == StellarMeasurementStatus.Completed
                    ? MeasuredAssociationPresentation.CreateMeasuredPayload(associations, scene)
                    : PresentationLayerPayloadJson.Create(identity, associations.Source.WidthPixels, associations.Source.HeightPixels);
            return new(source, pin, associationsArtifact, associationPayload, Associations: associations);
        }
        if (product.Slot.SchemaVersion == RegionalStellarVisibilityProducts.SchemaVersion)
        {
            var (visibilityArtifact, visibility) = await persistence.ReadCenteredViewRegionalVisibilityAsync(source.Exposure.CaptureId, pin, token)
                .ConfigureAwait(false);
            if (visibilityArtifact.ContentIdentitySha256 is not { } identity ||
                identity != product.ContentIdentitySha256 ||
                identity != visibility.AssessmentIdentitySha256)
                throw new InvalidDataException("The regional stellar visibility content identity is unbound.");
            var visibilityPayload = PresentationLayerProducers.FromRegionalStellarVisibility(
                visibility, visibility.Conditions.WidthPixels, visibility.Conditions.HeightPixels);
            return new(source, pin, visibilityArtifact, visibilityPayload, RegionalVisibility: visibility);
        }
        var artifact = await persistence.ReadCenteredViewProductAsync(source.Exposure.CaptureId, pin, token).ConfigureAwait(false);
        if (product.Slot.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion)
        {
            var fit = astrometry is null ? null : await astrometry.GetExactAsync(source.Exposure.CaptureId, pin, token).ConfigureAwait(false);
            if (fit?.Reason is "read-capacity" or CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable)
                throw new IOException("The retained reference fit is temporarily unavailable.");
            if (fit is not { Solution: not null, SourceDescriptor: { } descriptor, SolutionIdentitySha256: { } identity } ||
                artifact.ContentIdentitySha256 != identity || artifact.ContentIdentitySha256 != product.ContentIdentitySha256)
                throw new InvalidDataException("The retained reference fit could not be authenticated.");
            var fitPayload = fit.Diagnostics is { } diagnostics
                ? AstrometricDiagnosticPresentation.CreatePayload(identity, diagnostics, descriptor.Layout.Width, descriptor.Layout.Height, residuals: true)
                : PresentationLayerPayloadJson.Create(identity, descriptor.Layout.Width, descriptor.Layout.Height);
            return new(source, pin, artifact, fitPayload, Astrometry: fit);
        }
        var payload = PresentationLayerPayloadJson.Parse(artifact.Payload).Payload;
        if (payload is null || artifact.MediaType != PresentationLayerPayloadJson.MediaType ||
            payload.SchemaVersion != product.Slot.SchemaVersion || payload.ContentIdentitySha256 != product.ContentIdentitySha256 ||
            payload.ContentIdentitySha256 != artifact.ContentIdentitySha256)
            throw new InvalidDataException("A retained layer payload does not match the recorded evidence.");
        var satellite = style.Kind == "predicted-satellites"
            ? await ReadSatelliteStatusAsync(source.Exposure.CaptureId, artifact, token).ConfigureAwait(false) : null;
        return new(source, pin, artifact, payload, Satellite: satellite);
    }

    private async ValueTask<CaptureSatelliteStatusV1> ReadSatelliteStatusAsync(Guid captureId, ProcessingArtifact layer,
        CancellationToken token)
    {
        if (layer.SourceArtifactIds is not [{ } parentId])
            throw new InvalidDataException("The satellite layer lacks its committed source evidence.");
        var parent = await persistence.ReadCenteredViewParentAsync(captureId, parentId, token).ConfigureAwait(false);
        if (parent.SchemaVersion == CaptureSatelliteProducts.SourceSchema && parent.SourceArtifactIds is [_, { } statusId])
            parent = await persistence.ReadCenteredViewParentAsync(captureId, statusId, token).ConfigureAwait(false);
        if (parent.SchemaVersion != CaptureSatelliteProducts.StatusSchema)
            throw new InvalidDataException("The satellite layer lacks its committed prediction status.");
        return CaptureSatelliteProducts.ParseStatus(parent.Payload.Span);
    }

    private async ValueTask<IImageRegistrationMap?> ResolveDirectMapAsync(CenteredViewBaseDefinition definition,
        CenteredViewContributor member, CancellationToken token)
    {
        if (member.Exposure.Input.Key == definition.Reference.Input.Key &&
            member.RegistrationMethod == nameof(CenteredRegistrationMethod.ReferenceIdentity))
            return null;
        var mapping = member.SourceToReference.Deserialize<CenteredStackSourceEvidence>(GeometryJson);
        if (mapping is null || mapping.CaptureId != member.Exposure.Capture.CaptureId || mapping.Reason != CenteredRegistrationReason.Contributed ||
            mapping.Method?.ToString() != member.RegistrationMethod)
            throw new InvalidDataException("The base lacks the exact applied registration for the layer source.");
        if (mapping is { Method: CenteredRegistrationMethod.VerifiedCentroidSimilarity, Similarity: { } similarity })
            return similarity;
        if (mapping.Method != CenteredRegistrationMethod.VerifiedPlatePair || registration is null)
            throw new InvalidDataException("The base lacks a restorable direct registration map.");

        var artifact = await persistence.ReadCenteredViewRetainedProductAsync(definition.Reference.Capture.CaptureId, definition.Science, token)
            .ConfigureAwait(false);
        var evidence = CenteredRegisteredStackEvidence.Read(artifact);
        var exact = evidence.Sources.SingleOrDefault(source => source.CaptureId == mapping.CaptureId);
        if (exact is null || !JsonElement.DeepEquals(member.SourceToReference, JsonSerializer.SerializeToElement(exact, GeometryJson)) ||
            evidence.Plan.ReferenceCaptureId != definition.Reference.Capture.CaptureId)
            throw new InvalidDataException("The retained registration does not contain the issued plate pair.");

        var plateInputs = new Dictionary<Guid, ProcessingArtifact>();
        foreach (var offset in new[] { mapping.Offset, 0 })
        {
            var endpoint = evidence.Plan.Slots[offset + CenteredWindowScheduler.CenterIndex].Exposure;
            if (endpoint?.Plate is not { } plate)
                throw new InvalidDataException("The registration plan has no pinned plate request.");
            foreach (var pin in new[] { endpoint.Raw, plate.Associations, plate.Scene, plate.Context, plate.Solution })
            {
                plateInputs[pin.ArtifactId] =
                    await registration.ReadArtifactAsync(endpoint.Key, pin, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A required registration plate input is no longer retained.");
            }
        }
        return CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, mapping.Offset, plateInputs, token);
    }

    private static PresentationLayerPayloadV1 MapPayload(SourceLayer evidence, CenteredViewPreviewTransform transform,
        IImageRegistrationMap? registration, bool contributor, bool isReference)
    {
        var source = evidence.Payload;
        var identity = evidence.Artifact.ContentIdentitySha256
            ?? throw new InvalidDataException("The source layer has no retained content identity.");
        if (source.RichText is { } rich)
        {
            if (contributor)
                throw new InvalidDataException("Corner facts require reference or stack scope.");
            var scale = Math.Min(1, Math.Min((double)transform.PreviewWidth / source.WidthPixels,
                (double)transform.PreviewHeight / source.HeightPixels));
            var previewText = scale == 1 ? rich : rich with
            {
                Corners = rich.Corners.Select(corner => FitPreviewCorner(corner, scale, transform.PreviewWidth, transform.PreviewHeight)).ToArray()
            };
            return PresentationLayerPayloadJson.CreateRichText(identity, transform.PreviewWidth, transform.PreviewHeight, previewText);
        }
        if (registration is not null && (source.Ellipses.Count != 0 || source.TileMask is not null))
            throw new InvalidDataException("Ellipses and tile masks require the reference coordinate domain.");

        PixelPoint? Map(PixelPoint point) =>
            (registration is null ? point : registration.SourceToReference(point)) is { } reference
                ? transform.ToPreview(reference) : null;

        var segments = new List<PresentationSegmentV1>();
        foreach (var segment in source.Segments)
        {
            if (Map(segment.From) is { } from && Map(segment.To) is { } to)
                segments.Add(segment with
                {
                    From = from,
                    To = to,
                    Thickness = contributor && isReference ? Math.Min(segment.Thickness + 1, 8) : segment.Thickness
                });
        }
        var tileMask = source.TileMask;
        if (tileMask is not null && (transform.PreviewWidth != transform.ReferenceWidth || transform.PreviewHeight != transform.ReferenceHeight ||
            transform.CropWidth != transform.ReferenceWidth || transform.CropHeight != transform.ReferenceHeight))
        {
            AddTileSegments(tileMask, transform, segments);
            tileMask = null;
        }
        return PresentationLayerPayloadJson.Create(identity, transform.PreviewWidth, transform.PreviewHeight,
            markers: source.Markers.Select(marker => (Marker: marker, Center: Map(marker.Center)))
                .Where(mapped => mapped.Center.HasValue).Select(mapped => mapped.Marker with { Center = mapped.Center.GetValueOrDefault() }),
            segments: segments,
            ellipses: source.Ellipses.Select(ellipse => ellipse with
            {
                Center = transform.ToPreview(ellipse.Center),
                RadiusX = ellipse.RadiusX * transform.PreviewWidth / transform.CropWidth,
                RadiusY = ellipse.RadiusY * transform.PreviewHeight / transform.CropHeight
            }),
            textBlocks: source.TextBlocks.Select(block => (Block: block, Point: block.Anchor == PresentationTextAnchor.Point
                    ? Map(block.Point) : block.Point))
                .Where(mapped => mapped.Point.HasValue).Select(mapped => mapped.Block with { Point = mapped.Point.GetValueOrDefault() }),
            tileMask: tileMask, sceneStyleIdentitySha256: source.SceneStyleIdentitySha256);
    }

    private static PresentationRichCornerV1 FitPreviewCorner(PresentationRichCornerV1 corner, double scale, int width, int height)
    {
        var preferred = Scale(scale);
        if (Fits(preferred))
            return preferred;

        // The text scales, but the compositor's panel padding remains fixed in preview pixels.
        // If necessary, fit against the actual packaged glyphs down to the supported font floor.
        var best = Scale(0);
        if (!Fits(best))
            throw new ArgumentException("The Frame Facts corner cannot fit the preview.", nameof(corner));

        var lower = 0d;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var mid = (lower + scale) / 2;
            var candidate = Scale(mid);
            if (Fits(candidate))
            {
                lower = mid;
                best = candidate;
            }
            else
                scale = mid;
        }
        return best;

        PresentationRichCornerV1 Scale(double factor) => corner with
        {
            SizeMilliPixels = Math.Max(4_000, (int)Math.Floor(corner.SizeMilliPixels * factor)),
            Inset = (int)Math.Floor(corner.Inset * factor),
            LineSpacing = (int)Math.Floor(corner.LineSpacing * factor)
        };

        bool Fits(PresentationRichCornerV1 scaled)
        {
            try
            {
                using var layout = PresentationRichText.Layout(scaled, width, height);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private static void AddTileSegments(PresentationTileMaskV1 mask, CenteredViewPreviewTransform transform,
        List<PresentationSegmentV1> segments)
    {
        var width = transform.ReferenceWidth;
        var height = transform.ReferenceHeight;
        for (var row = 0; row < mask.Rows; row++)
        {
            for (var column = 0; column < mask.Columns; column++)
            {
                var index = row * mask.Columns + column;
                if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0)
                    continue;
                if (segments.Count > GroupedSvgPresentationRenderer.MaximumSvgElements - 4)
                    throw new InvalidDataException("The transformed tile grid exceeds the retained layer's drawing bound.");
                var left = (long)column * width / mask.Columns;
                var right = (long)(column + 1) * width / mask.Columns;
                var top = (long)row * height / mask.Rows;
                var bottom = (long)(row + 1) * height / mask.Rows;
                var nw = transform.ToPreview(new(left, top));
                var ne = transform.ToPreview(new(right, top));
                var se = transform.ToPreview(new(right, bottom));
                var sw = transform.ToPreview(new(left, bottom));
                segments.AddRange([
                    new(nw, ne, mask.LineThickness, mask.Color), new(ne, se, mask.LineThickness, mask.Color),
                    new(se, sw, mask.LineThickness, mask.Color), new(sw, nw, mask.LineThickness, mask.Color)
                ]);
            }
        }
    }

    private sealed record SourceLayer(ExposureProductLookup Lookup, CenteredViewArtifact Pin,
        ProcessingArtifact Artifact, PresentationLayerPayloadV1 Payload, ExposureMeasurementV1? Measurement = null,
        CameraAgentRetainedAstrometry? Astrometry = null, CaptureSatelliteStatusV1? Satellite = null,
        RegionalStellarVisibilityV1? RegionalVisibility = null, MeasuredStellarAssociationsV1? Associations = null);
    private static readonly JsonSerializerOptions GeometryJson = new(JsonSerializerDefaults.Web);
}
