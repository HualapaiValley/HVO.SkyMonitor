using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class CenteredViewProducer(
    CaptureProcessingPersistence persistence,
    ICameraAgentArtifactService artifacts,
    SqliteCenteredViews views,
    IOptions<CameraAgentHostOptions> hostOptions,
    SqliteCaptureProcessingStore store) : ICenteredViewProducer
{
    public async ValueTask<CenteredView?> PublishSingleAsync(
        CenteredViewStream stream, Guid executionId, long referenceOrder, LinearExposureLookup result,
        CenteredViewPreviewOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        var input = result.Exposure;
        if (referenceOrder != input.Raw.Capture.CaptureSequence ||
            input.Key != new ExposureKey(stream.Camera, stream.RunId, input.Raw.Capture.CaptureId) ||
            !input.Raw.Validate().IsValid || input.Geometry.Layout != input.Raw.Layout)
            throw new InvalidDataException("The single preview source does not belong to the admitted camera run.");

        var retained = await views.ReadRegisteredBaseAsync(stream, input.Key.CaptureId, CenteredViewKind.Single, cancellationToken, includePending: true)
            .ConfigureAwait(false);
        if (retained is not null)
        {
            var reference = retained.Base.Definition.Reference;
            if (reference.ExecutionId != executionId ||
                CaptureContractJson.ComputeDescriptorSha256(reference.Input.Raw) != CaptureContractJson.ComputeDescriptorSha256(input.Raw))
                throw new InvalidDataException("The registered single belongs to different admitted evidence.");
            return retained;
        }
        if (result.Disposition == LinearExposureDisposition.Pending || result.Revision is not > 0)
            return null;

        CenteredViewExposure exposure;
        CenteredViewArtifact science;
        ProcessingArtifact pixels;
        CenteredViewImageSource source;
        if (result.Disposition is LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly)
        {
            exposure = BindQualifiedExposure(result, executionId);
            science = exposure.Linear!;
            pixels = await persistence.ReadCenteredViewProductAsync(input.Key.CaptureId, science, cancellationToken).ConfigureAwait(false);
            if (pixels.Role != FrameArtifactRole.Calibrated || pixels.Layout is null ||
                pixels.RecipeIdentitySha256 != result.Provenance!.Recipe.IdentitySha256 ||
                pixels.SourceArtifactIds is not { Count: > 0 } lineage || lineage[0] != input.Raw.Artifact.ArtifactId)
                throw new InvalidDataException("The retained preview input is not the qualified linear artifact.");
            source = CenteredViewImageSource.QualifiedLinear;
        }
        else
        {
            if (!options.AllowRawFallback)
                return null;
            exposure = new(input, executionId, null);
            science = exposure.RawArtifact();
            var raw = await ReadRawFallbackAsync(exposure, cancellationToken).ConfigureAwait(false);
            if (raw is null)
                return null;
            pixels = raw;
            source = CenteredViewImageSource.RawFallback;
        }

        var contributor = new CenteredViewContributor(exposure, CenteredRegistrationMethod.ReferenceIdentity.ToString(),
            JsonSerializer.SerializeToElement(new { method = "reference-identity-v1" }));
        var encoded = CenteredViewPreviewEncoder.Encode([pixels], pixels.Layout!, source, result.Disposition,
            result.Reason, BoundOptions(options), cancellationToken);
        var definition = new CenteredViewBaseDefinition(stream, exposure, referenceOrder, CenteredViewKind.Single,
            science, encoded.Preview, [contributor], null);
        return await PersistAndRegisterAsync(definition, encoded.Product, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CenteredView> PersistAndRegisterAsync(
        CenteredViewBaseDefinition definition, ProcessingProduct preview, CancellationToken token)
    {
        if (preview.Payload.Length > hostOptions.Value.ArtifactRead.MaximumPreviewEncodedBytes)
            throw new InvalidDataException("The centered preview exceeds the configured encoded byte limit.");
        var initialLayers = await CreateInitialLayersAsync(definition, token).ConfigureAwait(false);
        await persistence.PersistCenteredViewProductAsync(definition.Reference.Input.Raw, preview, token).ConfigureAwait(false);
        return await views.RegisterBaseAsync(definition, initialLayers, token).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<CenteredViewLayer>> CreateInitialLayersAsync(
        CenteredViewBaseDefinition definition, CancellationToken token)
    {
        var configured = await store.ReadCenteredLayerBindingsAsync(definition.Stream.GenerationId, token).ConfigureAwait(false);
        if (configured is null)
            return [];

        var present = configured.Values.ToHashSet();
        var reference = definition.Reference.Capture.CaptureId;
        var layers = new List<CenteredViewLayer>();
        foreach (var binding in CenteredViewLayerBindings.Known)
        {
            var isConfigured = present.Contains(binding);
            if (binding.Scope == CenteredViewLayerScope.Reference)
                layers.Add(Initial(binding.Key(reference), isConfigured));
            else
                layers.AddRange(definition.Contributors.Select(member => Initial(binding.Key(member.Exposure.Capture.CaptureId), isConfigured)));
        }

        var trailsEnabled = hostOptions.Value.ArtifactRead.MeasuredTransientTrailsEnabled &&
            hostOptions.Value.TransientDetection.Mode is TransientOperatingMode.Edge or TransientOperatingMode.Hybrid;
        layers.AddRange(definition.Contributors.Select(member => Initial(
            new(TransientTrailPresentation.LayerKind, CenteredViewLayerScope.Contributor, member.Exposure.Capture.CaptureId), trailsEnabled)));
        return layers;

        static CenteredViewLayer Initial(CenteredViewLayerKey key, bool configured) => new(key, 0, null, null,
            configured ? CenteredViewReadiness.Waiting : CenteredViewReadiness.NotConfigured,
            configured ? "centered.awaiting-product" : "centered.layer-not-configured", null);
    }

    private async ValueTask<ProcessingArtifact?> ReadRawFallbackAsync(CenteredViewExposure exposure, CancellationToken token)
    {
        var raw = exposure.Input.Raw;
        var eligibility = CameraAgentPreviewEligibilityPolicy.Evaluate(FrameArtifactRole.Raw, raw.Artifact.MediaType,
            raw.Layout.ByteLength, raw.Layout.PixelFormat, CameraAgentPreviewEligibilityPolicy.IsSupportedLayout(raw.Layout),
            null, null, hostOptions.Value.ArtifactRead);
        if (eligibility != CameraAgentPreviewEligibility.Available)
            return null;
        var opened = await artifacts.OpenContentAsync(raw.Artifact.ArtifactId, token).ConfigureAwait(false);
        if (opened.Status == CameraAgentArtifactReadStatus.Unavailable)
            throw new IOException("The admitted raw preview source is temporarily unavailable.");
        if (opened.Content is not { } content)
            return null;
        await using var lease = content.ConfigureAwait(false);
        if (opened.Status != CameraAgentArtifactReadStatus.Found || content.CaptureId != raw.Capture.CaptureId ||
            content.ChecksumSha256 != raw.Artifact.ChecksumSha256 || content.ByteLength != raw.Layout.ByteLength ||
            content.Descriptor is not { } descriptor || CaptureContractJson.ComputeDescriptorSha256(descriptor) != exposure.RawArtifact().IdentitySha256)
            throw new InvalidDataException("The retained raw fallback does not match the admitted exposure.");
        var bytes = new byte[checked((int)content.ByteLength)];
        await content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (ProcessingIdentity.ComputePayloadSha256(bytes) != raw.Artifact.ChecksumSha256)
            throw new InvalidDataException("The retained raw fallback payload changed during readback.");
        return new ProcessingArtifact(raw.Artifact.ArtifactId, FrameArtifactRole.Raw, raw.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256, raw.Artifact.MediaType,
            raw.Layout, bytes, raw.Artifact.CreatedUtc, raw.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(raw));
    }

    private static CenteredViewExposure BindQualifiedExposure(LinearExposureLookup result, Guid executionId)
    {
        var exposure = result.Exposure;
        if (result.Disposition is not (LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly) ||
            result.Revision is not > 0 || result.Product is not { } product || result.Provenance is not { } provenance ||
            product.Exposure != exposure.Key || product.Slot.ExecutionId != executionId ||
            provenance.Disposition != result.Disposition || provenance.RawArtifactId != exposure.Raw.Artifact.ArtifactId ||
            provenance.RawDescriptorSha256 != CaptureContractJson.ComputeDescriptorSha256(exposure.Raw) ||
            provenance.RawPayloadSha256 != exposure.Raw.Artifact.ChecksumSha256 ||
            provenance.LinearArtifactId != product.ArtifactId || provenance.LinearPayloadSha256 != product.PayloadSha256 ||
            provenance.OutputIdentitySha256 != product.OutputIdentitySha256)
            throw new InvalidDataException("The centered preview lacks a committed, qualified linear binding.");
        return new(exposure, executionId, new(product.ArtifactId, product.OutputIdentitySha256, product.PayloadSha256));
    }

    private CenteredViewPreviewOptions BoundOptions(CenteredViewPreviewOptions options) =>
        options with { MaximumDimension = Math.Min(options.MaximumDimension, hostOptions.Value.ArtifactRead.MaximumPreviewDimension) };
}
