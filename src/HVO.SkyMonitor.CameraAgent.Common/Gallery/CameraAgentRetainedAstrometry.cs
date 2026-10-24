using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>Capture-bound evidence only. A previous fit is never projected onto a new capture by this read API.</summary>
public sealed record CameraAgentRetainedAstrometry(Guid CaptureId, string Availability, string Reason,
    Guid? ArtifactId = null, string? SolutionIdentitySha256 = null, string? GeometryBasis = null,
    MeasuredAstrometrySolutionV1? Solution = null);

public interface ICameraAgentRetainedAstrometryService
{
    ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken);
}

internal sealed class CameraAgentRetainedAstrometryService(SqliteCaptureProcessingStore store,
    ICameraAgentArtifactService artifacts, int maximumReadBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes) : ICameraAgentRetainedAstrometryService, IDisposable
{
    internal const int MaximumRawBytes = 32 * 1024 * 1024;
    internal const int MaximumReadBytes = 44 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim readGate = new(1, 1);

    public void Dispose() => readGate.Dispose();

    public async ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        // Do not accumulate raw buffers behind concurrent Current Sky/archive requests.
        if (!await readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(captureId, "Unavailable", "read-capacity");
        try { return await ReadAsync(captureId, cancellationToken).ConfigureAwait(false); }
        finally { readGate.Release(); }
    }

    private async ValueTask<CameraAgentRetainedAstrometry> ReadAsync(Guid captureId, CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty) return new(captureId, "Unavailable", "invalid-capture");
        try
        {
            var candidates = await store.ReadCaptureProductsAsync(captureId,
                MeasuredAstrometrySolutionV1.CurrentSchemaVersion, 2, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0) return new(captureId, "NotRetained", "assessment-not-retained");
            if (candidates.Count != 1 || candidates[0].AvailabilityState != "Available")
                return new(captureId, "Unavailable", "assessment-ambiguous-or-unavailable");
            var remaining = Math.Min(maximumReadBytes, MaximumReadBytes);
            var (solutionArtifact, solutionProduct) = await ReadProductAsync(candidates[0].ArtifactId,
                MeasuredAstrometryJson.MaximumSolutionBytes).ConfigureAwait(false);
            var solution = MeasuredAstrometryJson.Parse(solutionArtifact.Payload);
            if (solution.Assessment.Frame.CaptureId != captureId || solutionProduct.SourceArtifactIds.Count != 4)
                throw new InvalidDataException("Retained assessment source mismatch.");
            var rawId = solution.Assessment.Frame.SourceArtifactId;
            var rawRead = await artifacts.OpenContentAsync(rawId, cancellationToken).ConfigureAwait(false);
            var rawContent = rawRead.Content ?? throw new IOException("The retained raw is unavailable.");
            await using var rawLease = rawContent.ConfigureAwait(false);
            var descriptor = rawContent.Descriptor ?? throw new InvalidDataException("Missing raw descriptor.");
            if (rawRead.Status != CameraAgentArtifactReadStatus.Found || rawContent.CaptureId != captureId ||
                rawContent.Role != FrameArtifactRole.Raw || descriptor.Artifact.ArtifactId != rawId ||
                CaptureContractJson.ComputeDescriptorSha256(descriptor) != solution.Assessment.Frame.SourceDescriptorSha256)
                throw new InvalidDataException("Raw descriptor mismatch.");
            var rawBytes = await ReadBytesAsync(rawContent, MaximumRawBytes).ConfigureAwait(false);
            var raw = new ProcessingArtifact(rawId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType, descriptor.Layout, rawBytes, descriptor.Artifact.CreatedUtc,
                descriptor.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
                descriptor.Capture.CaptureSequence, descriptor.Artifact.SourceArtifactIds,
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc)
            { CaptureId = captureId, DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor) };
            var (measured, _) = await ReadProductAsync(solution.MeasuredArtifactId, StructuredProcessingProductDescriptorV1.MaximumPayloadBytes).ConfigureAwait(false);
            var (scene, _) = await ReadProductAsync(solutionProduct.SourceArtifactIds[2], StructuredProcessingProductDescriptorV1.MaximumPayloadBytes).ConfigureAwait(false);
            var (context, _) = await ReadProductAsync(solutionProduct.SourceArtifactIds[3], MeasuredAstrometryJson.MaximumContextBytes).ConfigureAwait(false);
            var options = solutionProduct.Recipe.Descriptor.Options.GetProperty("parameters").Deserialize<MeasuredAstrometryOptions>(Json)!;
            var request = RetainedAstrometryProducts.Request(raw, measured, scene, context, options, solutionProduct.Variant);
            solution = RetainedAstrometryProducts.Verify(request, solutionProduct, cancellationToken);
            return new(captureId, "Retained", solution.Assessment.ReasonCode, solutionArtifact.ArtifactId,
                solution.SolutionIdentitySha256, solution.Assessment.HasMeasuredMapping ? "MeasuredFit" : "Unavailable", solution);

            async ValueTask<(ProcessingArtifact Artifact, ProcessingProduct Product)> ReadProductAsync(Guid id, int maximum)
            {
                var result = await artifacts.OpenContentAsync(id, cancellationToken).ConfigureAwait(false);
                var content = result.Content ?? throw new IOException("A retained input is unavailable.");
                await using var contentLease = content.ConfigureAwait(false);
                if (result.Status != CameraAgentArtifactReadStatus.Found || content.CaptureId != captureId || content.ProductManifest is not { } manifest)
                    throw new InvalidDataException("Invalid retained input envelope.");
                var bytes = await ReadBytesAsync(content, maximum).ConfigureAwait(false);
                var identity = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
                var product = new ProcessingProduct(manifest.Artifact.Role, manifest.Artifact.Variant!, manifest.OutputIdentitySha256,
                    manifest.Artifact.MediaType, null, bytes, manifest.Artifact.ChecksumSha256, identity, manifest.Algorithms,
                    manifest.Artifact.SourceArtifactIds, TimeSpan.FromTicks(manifest.TotalIntegrationTicks), manifest.Compatibility)
                { Kind = manifest.Kind, SchemaVersion = manifest.ProductSchemaVersion, ContentIdentitySha256 = manifest.ContentIdentitySha256 };
                var artifact = new ProcessingArtifact(id, product.Role, product.Variant, identity.IdentitySha256, product.MediaType,
                    null, bytes, manifest.Artifact.CreatedUtc, product.TotalIntegration, product.Compatibility,
                    SourceArtifactIds: product.SourceArtifactIds)
                {
                    CaptureId = captureId,
                    ProductKind = product.Kind,
                    SchemaVersion = product.SchemaVersion,
                    ContentIdentitySha256 = product.ContentIdentitySha256,
                    RecipeDescriptor = identity.Descriptor
                };
                return (artifact, product);
            }

            async ValueTask<byte[]> ReadBytesAsync(CameraAgentArtifactContentStream content, int maximum)
            {
                if (content.ByteLength is <= 0 || content.ByteLength > maximum || content.ByteLength > remaining)
                    throw new InvalidDataException("Retained astrometry read exceeds its byte bound.");
                remaining -= checked((int)content.ByteLength);
                var bytes = new byte[(int)content.ByteLength];
                await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return bytes;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or InvalidOperationException or JsonException or OverflowException)
        {
            return new(captureId, "Unavailable", "retained-evidence-invalid-or-unavailable");
        }
    }
}
