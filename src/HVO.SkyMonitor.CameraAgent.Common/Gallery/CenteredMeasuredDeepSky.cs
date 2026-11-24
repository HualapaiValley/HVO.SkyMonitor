using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>The work item captured by a centered publication scheduler after the base is committed.</summary>
public sealed record CenteredMeasuredDeepSkyWork(string BaseId, string RegisteredRevisionId, long EvidenceOrder)
{
    /// <summary>Creates an optional science work item from the committed initial stack revision.</summary>
    public static CenteredMeasuredDeepSkyWork? FromRegisteredStack(CenteredView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Base.Definition is { Kind: CenteredViewKind.Stack, Window: not null, RecomputedFromBaseId: null } &&
            view.Revision.BaseId == view.Base.BaseId && view.Revision.Order > 0
            ? new(view.Base.BaseId, view.Revision.RevisionId, view.Revision.Order)
            : null;
    }
}

public interface ICenteredMeasuredDeepSky
{
    /// <summary>Runs on the optional processing lane. Retry the same frozen work item on an I/O failure.</summary>
    ValueTask<CenteredViewRevision?> ProcessAsync(CenteredMeasuredDeepSkyWork work, CancellationToken cancellationToken);

    /// <summary>Returns the assessment referenced by this exact publication, if it remains readable.</summary>
    ValueTask<MeasuredDeepSkyStackV1?> ReadAsync(string baseId, string revisionId, CancellationToken cancellationToken);
}

internal sealed class CenteredMeasuredDeepSky(ICenteredViewReadIndex index, ICenteredViewPublisher publisher,
    ICenteredWindowRegistrationSource registration, CaptureProcessingPersistence persistence,
    ICelestialCatalog catalog, RawCaptureIngress ingress) : ICenteredMeasuredDeepSky, IDisposable
{
    internal const string LayerKind = "measured-deep-sky";
    private static readonly JsonSerializerOptions GeometryJson = new(JsonSerializerDefaults.Web);
    private const string LayerVersion = "measured-deep-sky-preview-v1";
    private const int MaximumInputsBytes = 72 * 1024 * 1024;
    private static readonly CenteredViewLayerKey LayerKey = new(LayerKind, CenteredViewLayerScope.Stack, null);
    private readonly SemaphoreSlim _worker = new(1, 1);

    public void Dispose() => _worker.Dispose();

    public async ValueTask<CenteredViewRevision?> ProcessAsync(CenteredMeasuredDeepSkyWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(work.EvidenceOrder);
        var view = await index.ReadRevisionAsync(work.BaseId, work.RegisteredRevisionId, cancellationToken).ConfigureAwait(false);
        if (view is null || CenteredMeasuredDeepSkyWork.FromRegisteredStack(view) != work ||
            (await index.ReadRevisionsAsync(work.BaseId, 0, 1, cancellationToken).ConfigureAwait(false))
                .SingleOrDefault()?.RevisionId != work.RegisteredRevisionId)
        {
            return null;
        }

        var definition = view.Base.Definition;
        var raw = await ingress.ReadCenteredRawAsync(definition.Reference.Input.Key, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            if (definition.Reference.Input.Raw.CenteredAcquisition is not null)
            {
                throw new InvalidDataException("The published stack's admitted reference is no longer retained.");
            }
            return null;
        }
        var run = raw.Run;
        if (run.Camera != definition.Stream.Camera || run.RunId != definition.Stream.RunId ||
            run.GraphRevisionId != definition.Stream.GenerationId || definition.Reference.RawArtifact().IdentitySha256 !=
            CaptureContractJson.ComputeDescriptorSha256(raw.Manifest.Descriptor))
        {
            throw new InvalidDataException("The published stack differs from the admitted graph and run.");
        }
        if (CaptureProjectedSceneStager.ResolveOptions(run.Configuration) is not { AssessMeasuredDeepSky: true })
        {
            return null;
        }
        if (!raw.IsCommitted)
        {
            return await PublishIncompatibleAsync(work, "reference-raw-unavailable", cancellationToken).ConfigureAwait(false);
        }
        if (run.SceneResources.DeepSky is not { } snapshot)
        {
            return await PublishIncompatibleAsync(work, "no-openngc-snapshot-at-admission", cancellationToken).ConfigureAwait(false);
        }

        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProcessStackAsync(view, work, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInvalidEvidence(exception))
        {
            return await PublishIncompatibleAsync(work, "retained-stack-evidence-incompatible", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _worker.Release();
        }
    }

    internal static bool IsInvalidEvidence(Exception exception) => exception is InvalidDataException or ArgumentException or
        JsonException or InvalidOperationException or KeyNotFoundException;

    private async ValueTask<CenteredViewRevision?> PublishIncompatibleAsync(CenteredMeasuredDeepSkyWork work, string reason,
        CancellationToken cancellationToken) =>
        (await publisher.PublishLayerAsync(work.BaseId, new(LayerKey, work.EvidenceOrder, null, null,
            CenteredViewReadiness.Incompatible, reason, null), cancellationToken).ConfigureAwait(false))?.Revision;

    private async ValueTask<CenteredViewRevision?> ProcessStackAsync(CenteredView view, CenteredMeasuredDeepSkyWork work,
        CatalogSnapshotIdentity snapshot, CancellationToken cancellationToken)
    {
        var definition = view.Base.Definition;
        var captureId = definition.Reference.Capture.CaptureId;
        var remainingBytes = MaximumInputsBytes;
        var science = definition.Science;
        var evidence = await persistence.ReadCenteredViewProductAsync(captureId, science, cancellationToken).ConfigureAwait(false);
        var stack = MeasuredDeepSkyStackRecipe.ReadStackEvidence(evidence);
        var contributors = stack.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed).ToArray();
        var reference = stack.Plan.Slots[2].Exposure;
        if (stack.Disposition != CenteredStackDisposition.Registered || definition.ClosedWindow is not { } closedWindow ||
            definition.Window is not { } window || window.RegistrationPlanIdentitySha256 != stack.PlanSha256 ||
            window.ClosureIdentitySha256 != stack.Plan.ClosureIdentity ||
            stack.Plan.Camera != definition.Stream.Camera || stack.Plan.RunId != definition.Stream.RunId ||
            stack.Plan.ReferenceCaptureId != captureId || reference is null ||
            !MatchesExposure(reference, definition.Reference) ||
            contributors.Length != definition.Contributors.Count ||
            !contributors.Zip(definition.Contributors).All(pair =>
                stack.Plan.Slots[pair.First.Offset + 2].Exposure is { } source &&
                MatchesExposure(source, pair.Second.Exposure) && pair.First.Method?.ToString() == pair.Second.RegistrationMethod &&
                CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(pair.First, GeometryJson)) ==
                CaptureContractJson.ComputeCanonicalJsonSha256(pair.Second.SourceToReference)))
        {
            throw new InvalidDataException("Published membership differs from the selected registered stack.");
        }

        var closure = await persistence.ReadCenteredViewProductAsync(captureId, closedWindow, cancellationToken)
            .ConfigureAwait(false);
        if (closure.ContentIdentitySha256 != window.ClosureIdentitySha256 ||
            closure.SourceArtifactIds is not [{ } rawId, { } scienceId] ||
            rawId != reference.Raw.ArtifactId || scienceId != science.ArtifactId)
        {
            throw new InvalidDataException("The retained closure does not bind the selected reference and registered pixels.");
        }

        var inputs = new List<ProcessingArtifact>();
        foreach (var plane in stack.Outputs)
        {
            var retainedPlane = await persistence.ReadCenteredViewProductAsync(captureId,
                new(plane.ArtifactId, plane.OutputIdentitySha256, plane.PayloadSha256), cancellationToken).ConfigureAwait(false);
            remainingBytes -= retainedPlane.Payload.Length;
            if (remainingBytes < 0)
            {
                throw new InvalidDataException("The frozen stack exceeds the assessment input budget.");
            }
            inputs.Add(retainedPlane);
        }
        if (reference.Plate is { } plate)
        {
            foreach (var pin in new[] { reference.Raw, plate.Associations, plate.Scene, plate.Context, plate.Solution })
            {
                var input = await registration.ReadArtifactAsync(reference.Key, pin, cancellationToken).ConfigureAwait(false);
                if (input is not null)
                {
                    remainingBytes -= input.Payload.Length;
                    if (remainingBytes < 0)
                    {
                        throw new InvalidDataException("The frozen stack and mapping exceed the assessment input budget.");
                    }
                    inputs.Add(input);
                }
            }
        }
        var request = MeasuredDeepSkyStackRecipe.CreateRequest(evidence, inputs, snapshot);
        var recipe = new MeasuredDeepSkyStackRecipe((catalog as IDeepSkyCatalogSource)?.DeepSky);
        var outputIdentity = recipe.ExpectedOutputIdentity(request);
        var retained = await persistence.TryReadMeasuredDeepSkyAsync(captureId, outputIdentity, cancellationToken).ConfigureAwait(false);
        var committed = retained?.Pin;
        var source = retained?.Artifact;
        if (source is null)
        {
            var executor = new ProcessingRecipeExecutor([recipe]);
            var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Status != ProcessingOutcomeStatus.Produced || result.Products is not [{ } product])
            {
                throw new InvalidDataException($"Registered assessment failed ({result.ReasonCode}).");
            }
            await persistence.PersistCenteredViewProductAsync(definition.Reference.Input.Raw, product, cancellationToken).ConfigureAwait(false);
            committed = Pin(product);
            source = new ProcessingArtifact(committed.ArtifactId, product.Role, product.Variant, product.Recipe.IdentitySha256,
                product.MediaType, null, product.Payload, definition.Reference.ExposureEndedUtc, product.TotalIntegration,
                product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
            {
                ProductKind = product.Kind,
                SchemaVersion = product.SchemaVersion,
                ContentIdentitySha256 = product.ContentIdentitySha256,
                CaptureId = captureId,
                RecipeDescriptor = product.Recipe.Descriptor
            };
        }
        var assessment = ReadAssessment(source, science, captureId);
        var evidencePin = committed!;
        var (layer, payload) = CreateLayer(view.Base, assessment, evidencePin, work.EvidenceOrder);
        if (payload is not null)
        {
            var drawing = PresentationProcessingProducts.CreateLayerProduct(payload, LayerKind, [source], LayerVersion);
            await persistence.PersistCenteredViewProductAsync(definition.Reference.Input.Raw, drawing, cancellationToken).ConfigureAwait(false);
            var committedDrawing = Pin(drawing);
            var preview = definition.Preview.Product;
            var drawable = LayeredPresentationJson.CreateLayer(LayerKind,
                new(committedDrawing.ArtifactId, drawing.ContentIdentitySha256!, drawing.MediaType, preview.Compatibility),
                null, PresentationCoordinateSpace.ScenePixels,
                PresentationLayerCompositor.AlgorithmVersion, LayerVersion, 65, PresentationBlendMode.Normal, 1_000_000,
                enabledByDefault: false, JsonSerializer.SerializeToElement(new { }));
            layer = layer with { Drawable = drawable };
        }

        return (await publisher.PublishLayerAsync(view.Base.BaseId, layer, cancellationToken).ConfigureAwait(false))?.Revision;
    }

    public async ValueTask<MeasuredDeepSkyStackV1?> ReadAsync(string baseId, string revisionId, CancellationToken cancellationToken)
    {
        var view = await index.ReadRevisionAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        if (view is null || view.Base.Definition.Kind != CenteredViewKind.Stack ||
            view.Revision.Layers.SingleOrDefault(layer => layer.Key == LayerKey)?.Evidence is not { } evidence)
        {
            return null;
        }

        var science = view.Base.Definition.Science;
        var captureId = view.Base.Definition.Reference.Capture.CaptureId;
        var artifact = await persistence.ReadCenteredViewProductAsync(captureId, evidence, cancellationToken).ConfigureAwait(false);
        return ReadAssessment(artifact, science, captureId);
    }

    private static MeasuredDeepSkyStackV1 ReadAssessment(ProcessingArtifact artifact, CenteredViewArtifact science, Guid captureId)
    {
        var assessment = MeasuredDeepSkyStackV1.Read(artifact.Payload);
        if (artifact.MediaType != MeasuredDeepSkyStackV1.MediaType ||
            artifact.SchemaVersion != MeasuredDeepSkyStackV1.CurrentSchemaVersion ||
            assessment.AssessmentRecipeSha256 != artifact.RecipeIdentitySha256 ||
            artifact.ContentIdentitySha256 != ProcessingIdentity.ComputePayloadSha256(artifact.Payload) ||
            artifact.RecipeDescriptor is not
            {
                Name: MeasuredDeepSkyStackRecipe.RecipeName,
                ImplementationVersion: MeasuredDeepSkyStackRecipe.AlgorithmVersion
            } ||
            artifact.SourceArtifactIds is not [{ } stackId, ..] || stackId != science.ArtifactId ||
            assessment.ReferenceCaptureId != captureId ||
            assessment.Stack.ArtifactId != science.ArtifactId || assessment.Stack.OutputIdentitySha256 != science.IdentitySha256 ||
            assessment.Stack.PayloadSha256 != science.PayloadSha256)
        {
            throw new InvalidDataException("The retained assessment does not belong to this exact base.");
        }
        return assessment;
    }

    internal static (CenteredViewLayer Layer, PresentationLayerPayloadV1? Payload) CreateLayer(
        CenteredViewBase view, MeasuredDeepSkyStackV1 assessment, CenteredViewArtifact evidence, long order)
    {
        var observedUtc = assessment.ReferenceMidpointUtc;
        var transform = view.Definition.Preview.Transform;
        if (transform.ReferenceWidth != assessment.Width || transform.ReferenceHeight != assessment.Height ||
            transform.CropX < 0 || transform.CropY < 0 || transform.CropWidth <= 0 || transform.CropHeight <= 0 ||
            (long)transform.CropX + transform.CropWidth > assessment.Width ||
            (long)transform.CropY + transform.CropHeight > assessment.Height ||
            transform.PreviewWidth != view.Definition.Preview.Product.Compatibility.WidthPixels ||
            transform.PreviewHeight != view.Definition.Preview.Product.Compatibility.HeightPixels)
        {
            return (new(LayerKey, order, evidence, observedUtc, CenteredViewReadiness.Incompatible,
                "preview-transform-mismatch", null), null);
        }
        if (assessment.Status != DeepSkyStackStatus.Qualified)
        {
            return (new(LayerKey, order, evidence, observedUtc,
                assessment.Status == DeepSkyStackStatus.AssessedEmpty ? CenteredViewReadiness.AssessedEmpty : CenteredViewReadiness.Incompatible,
                assessment.Reason.ToString(), null), null);
        }

        var byId = assessment.Extraction!.Detections.ToDictionary(detection => detection.Id);
        var points = assessment.Identifications.Where(association => association.Status == DeepSkyIdentificationStatus.Qualified)
            .Select(association => (Point: transform.ToPreview(byId[association.DetectionId].Centroid), Label: association.Designation!))
            .Where(visible => visible.Point.X >= 0 && visible.Point.Y >= 0 &&
                visible.Point.X < transform.PreviewWidth && visible.Point.Y < transform.PreviewHeight).ToArray();
        if (points.Length == 0)
        {
            return (new(LayerKey, order, evidence, observedUtc, CenteredViewReadiness.Incompatible,
                "qualified-sources-outside-preview-crop", null), null);
        }

        var color = new PresentationColor(76, 226, 219);
        var drawable = PresentationLayerPayloadJson.Create(evidence.PayloadSha256, transform.PreviewWidth, transform.PreviewHeight,
            markers: points.Select(visible => new PresentationMarkerV1(visible.Point, 5, color, Crosshair: true)),
            textBlocks: points.Take(PresentationLayerPayloadV1.MaximumTextBlocks).Select(visible => new PresentationTextBlockV1(
                PresentationTextAnchor.Point, new(visible.Point.X + 7, visible.Point.Y), [visible.Label], 1, 0, 0, color)));
        return (new(LayerKey, order, evidence, observedUtc, CenteredViewReadiness.Ready, null, null), drawable);
    }

    private static CenteredViewArtifact Pin(ProcessingProduct product) =>
        new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.OutputIdentitySha256, product.ChecksumSha256);

    private static bool MatchesExposure(CenteredStackExposurePin pin, CenteredViewExposure exposure) =>
        pin.Key == exposure.Input.Key && exposure.Linear is { } linear && linear.ArtifactId == pin.Linear.ArtifactId &&
        linear.PayloadSha256 == pin.Linear.PayloadSha256 &&
        pin.Raw.ArtifactId == exposure.Input.Raw.Artifact.ArtifactId &&
        pin.Raw.DescriptorSha256 == CaptureContractJson.ComputeDescriptorSha256(exposure.Input.Raw) &&
        pin.Raw.PayloadSha256 == exposure.Input.Raw.Artifact.ChecksumSha256 &&
        pin.GeometrySha256 == MeasuredDeepSkyStackRecipe.ComputeGeometryIdentity(exposure.Input.Geometry);

}
