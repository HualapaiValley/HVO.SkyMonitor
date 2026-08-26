using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Minio.Exceptions;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralPresentationMaterializationStatus
{
    Saved,
    Unavailable,
    Invalid,
    Conflict,
    DependencyUnavailable
}

internal sealed record CentralPresentationMaterializationReceipt(
    Guid CaptureId,
    Guid ArtifactId,
    string OutputIdentitySha256,
    string ChecksumSha256,
    long ByteLength,
    string ContentPath,
    bool Replayed);

internal sealed record CentralPresentationMaterializationResult(
    CentralPresentationMaterializationStatus Status,
    CentralPresentationMaterializationReceipt? Receipt = null);

internal interface ICentralPresentationMaterializer
{
    Task<CentralPresentationMaterializationResult> SaveAsync(
        Guid captureId,
        IReadOnlyList<string> enabledLayerIdentitySha256,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

internal sealed class CentralPresentationMaterializer(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader,
    IArtifactIngestService ingest,
    CentralPresentationMaterializationGate materializationGate,
    CentralPresentationTelemetry telemetry,
    TimeProvider timeProvider) : ICentralPresentationMaterializer
{
    private const string OutputVariant = "operator-stack-v1";
    private const string RecipeImplementation = "central-packed-presentation-v1";
    private const int MaximumBaseBytes = 32 * 1024 * 1024;
    private const int MaximumSourcePayloadBytes = 16 * 1024 * 1024;
    private const long MaximumPixels = 32L * 1024 * 1024;
    private const int MaximumOutputBytes = 128 * 1024 * 1024;

    public async Task<CentralPresentationMaterializationResult> SaveAsync(
        Guid captureId,
        IReadOnlyList<string> enabledLayerIdentitySha256,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetTimestamp();
        ArgumentNullException.ThrowIfNull(enabledLayerIdentitySha256);
        ArgumentNullException.ThrowIfNull(principal);
        if (captureId == Guid.Empty ||
            enabledLayerIdentitySha256.Count > LayeredPresentationJson.MaximumLayerCount ||
            enabledLayerIdentitySha256.Distinct(StringComparer.Ordinal).Count() != enabledLayerIdentitySha256.Count ||
            enabledLayerIdentitySha256.Any(static identity => identity is not { Length: 64 } ||
                identity.Any(static character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) ||
            !CentralArtifactCredentialAccess.HasOwnerCredential(principal) ||
            CentralArtifactCredentialAccess.GetOwnerId(principal) is not { } ownerId ||
            principal.FindFirst(ApiKeyClaims.AccessLevel)?.Value == nameof(ApiKeyAccessLevel.Read) ||
            principal.Claims.Any(static claim => claim.Type == "scope") &&
            !CentralArtifactCredentialAccess.HasScope(principal, "api.owner.write") &&
            !CentralArtifactCredentialAccess.HasScope(principal, "api.admin"))
        {
            return new(CentralPresentationMaterializationStatus.Invalid);
        }
        using var admission = await materializationGate.EnterAsync(cancellationToken).ConfigureAwait(false);

        var observatories = ObservatoryMembershipAccess.ForManager(dbContext, ownerId)
            .Select(static membership => membership.ObservatoryId);
        var observatoryScope = CentralArtifactCredentialAccess.GetObservatoryScope(principal);
        var manifests = await dbContext.CentralArtifacts.AsNoTracking()
            .Include(static artifact => artifact.StructuredProduct)
            .Where(artifact => artifact.CentralFrameId == captureId &&
                observatories.Contains(artifact.Frame!.ObservatoryId) &&
                (observatoryScope == null || artifact.Frame.ObservatoryId == observatoryScope) &&
                artifact.ObjectState == CentralArtifactObjectState.Available &&
                artifact.ReconstructionState == CentralReconstructionState.Complete &&
                artifact.MediaType == PresentationProcessingProducts.ManifestMediaType &&
                artifact.StructuredProduct != null &&
                artifact.StructuredProduct.ProductSchemaVersion == OverlayManifestV1.CurrentSchemaVersion)
            .OrderByDescending(static artifact => artifact.CreatedUtc)
            .ThenByDescending(static artifact => artifact.ArtifactId)
            .Take(1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (manifests.Length == 0)
        {
            return new(CentralPresentationMaterializationStatus.Unavailable);
        }
        try
        {
            var manifestArtifact = manifests[0];
            var manifestBytes = await ReadBytesAsync(manifestArtifact, LayeredPresentationJson.MaximumPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var parsed = LayeredPresentationJson.ParseManifest(manifestBytes);
            if (!parsed.IsValid || parsed.Document is not { } manifest ||
                !string.Equals(manifest.ManifestIdentitySha256,
                    manifestArtifact.StructuredProduct!.ContentIdentitySha256, StringComparison.Ordinal))
            {
                return new(CentralPresentationMaterializationStatus.Conflict);
            }
            var enabled = enabledLayerIdentitySha256.ToHashSet(StringComparer.Ordinal);
            if (enabled.Any(identity => manifest.Layers.All(layer =>
                    !string.Equals(layer.LayerIdentitySha256, identity, StringComparison.Ordinal))) ||
                manifest.Layers.Any(layer => enabled.Contains(layer.LayerIdentitySha256) &&
                    layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels))
            {
                return new(CentralPresentationMaterializationStatus.Invalid);
            }
            if (checked((long)manifest.BaseProduct.Compatibility.WidthPixels *
                    manifest.BaseProduct.Compatibility.HeightPixels) > MaximumPixels)
            {
                return new(CentralPresentationMaterializationStatus.Invalid);
            }

            var referencedIds = manifest.Layers.Select(static layer => layer.SourceProduct.ArtifactId)
                .Append(manifest.BaseProduct.ArtifactId)
                .ToArray();
            var artifacts = await dbContext.CentralArtifacts.AsNoTracking()
                .Include(static artifact => artifact.Layout)
                .Include(static artifact => artifact.Recipe)
                .Include(static artifact => artifact.Sources)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Timing)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Control)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Profiles)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Location)
                .Where(artifact => artifact.CentralFrameId == captureId &&
                    referencedIds.Contains(artifact.ArtifactId) &&
                    artifact.ObjectState == CentralArtifactObjectState.Available &&
                    artifact.ReconstructionState == CentralReconstructionState.Complete)
                .ToDictionaryAsync(static artifact => artifact.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
            if (!artifacts.TryGetValue(manifest.BaseProduct.ArtifactId, out var baseArtifact) ||
                baseArtifact.MediaType != JpegImageCodec.MediaType ||
                !string.Equals(baseArtifact.ChecksumSha256,
                    manifest.BaseProduct.ProductIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                baseArtifact.ByteLength > MaximumBaseBytes ||
                baseArtifact.ReconstructionState != CentralReconstructionState.Complete)
            {
                return new(CentralPresentationMaterializationStatus.Conflict);
            }

            var baseBytes = await ReadBytesAsync(baseArtifact, MaximumBaseBytes, cancellationToken).ConfigureAwait(false);
            var encodedInfo = JpegImageCodec.InspectJpeg(baseBytes);
            if (encodedInfo.Width != manifest.BaseProduct.Compatibility.WidthPixels ||
                encodedInfo.Height != manifest.BaseProduct.Compatibility.HeightPixels ||
                checked((long)encodedInfo.Width * encodedInfo.Height) > MaximumPixels)
            {
                return new(CentralPresentationMaterializationStatus.Conflict);
            }
            var decoded = JpegImageCodec.DecodeJpeg(baseBytes, cancellationToken);
            if (decoded.Width != manifest.BaseProduct.Compatibility.WidthPixels ||
                decoded.Height != manifest.BaseProduct.Compatibility.HeightPixels)
            {
                return new(CentralPresentationMaterializationStatus.Conflict);
            }
            var compositorLayers = new List<PresentationCompositorLayer>();
            var sourcePayloadBytes = 0;
            foreach (var layer in manifest.Layers.Where(layer => enabled.Contains(layer.LayerIdentitySha256)))
            {
                if (!artifacts.TryGetValue(layer.SourceProduct.ArtifactId, out var artifact) ||
                    artifact.MediaType != PresentationLayerPayloadJson.MediaType)
                {
                    return new(CentralPresentationMaterializationStatus.Conflict);
                }
                sourcePayloadBytes = checked(sourcePayloadBytes + (int)artifact.ByteLength);
                if (sourcePayloadBytes > MaximumSourcePayloadBytes)
                {
                    return new(CentralPresentationMaterializationStatus.Invalid);
                }
                var payloadBytes = await ReadBytesAsync(
                    artifact, LayeredPresentationJson.MaximumPayloadBytes, cancellationToken).ConfigureAwait(false);
                var payload = PresentationLayerPayloadJson.Parse(payloadBytes).Payload;
                if (payload is null || payload.ContentIdentitySha256 != layer.SourceProduct.ProductIdentitySha256)
                {
                    return new(CentralPresentationMaterializationStatus.Conflict);
                }
                compositorLayers.Add(new(payload, true, layer.BlendMode switch
                {
                    PresentationBlendMode.Normal => PresentationRasterBlendMode.Normal,
                    PresentationBlendMode.Multiply => PresentationRasterBlendMode.Multiply,
                    PresentationBlendMode.Screen => PresentationRasterBlendMode.Screen,
                    PresentationBlendMode.Lighten => PresentationRasterBlendMode.Lighten,
                    _ => throw new InvalidDataException("The retained blend mode is unsupported.")
                }, layer.OpacityMillionths));
            }

            var layout = new ImageLayout(decoded.Width, decoded.Height, decoded.PixelFormat, decoded.StrideBytes);
            var output = PresentationLayerCompositor.Composite(
                layout, decoded.PixelData, compositorLayers, cancellationToken).ToArray();
            if (output.Length > MaximumOutputBytes)
            {
                return new(CentralPresentationMaterializationStatus.Invalid);
            }
            var sourceIds = new[] { baseArtifact.ArtifactId, manifestArtifact.ArtifactId }
                .Concat(manifest.Layers.Where(layer => enabled.Contains(layer.LayerIdentitySha256))
                    .Select(static layer => layer.SourceProduct.ArtifactId))
                .Distinct()
                .ToArray();
            var request = LayeredPresentationJson.CreateMaterializationRequest(
                manifest,
                enabled,
                PresentationLayerCompositor.AlgorithmVersion,
                PresentationMaterializationExecutor.PackedEncoderName,
                PresentationMaterializationExecutor.PackedEncoderVersion,
                JsonSerializer.SerializeToElement(new { format = "packed", pixelFormat = decoded.PixelFormat.ToString() }),
                sourceIds);
            var recipeOptions = JsonSerializer.SerializeToElement(new
            {
                request.MaterializationIdentitySha256,
                decoder = JpegImageCodec.AlgorithmVersion,
                pixelFormat = decoded.PixelFormat.ToString()
            });
            var recipe = RecipeIdentityDescriptor.Create(
                PresentationProcessingProducts.MaterializationRecipeName,
                "1.0.0",
                RecipeImplementation,
                recipeOptions);
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe);
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.AnnotatedPreview,
                OutputVariant,
                recipeIdentity.IdentitySha256,
                sourceIds);
            var artifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
            var checksum = ProcessingIdentity.ComputePayloadSha256(output);
            var replayed = await dbContext.CentralArtifacts.AsNoTracking()
                .AnyAsync(artifact => artifact.ArtifactId == artifactId, cancellationToken).ConfigureAwait(false);
            var baseDescriptor = CentralReconstructionDescriptorFactory.Create(baseArtifact.Frame!, baseArtifact);
            if (!string.Equals(PresentationProcessingProducts.ComputeLayoutIdentity(baseDescriptor.Layout),
                    manifest.BaseProduct.Compatibility.LayoutIdentitySha256, StringComparison.Ordinal))
            {
                return new(CentralPresentationMaterializationStatus.Conflict);
            }
            var descriptor = baseDescriptor with
            {
                Layout = baseDescriptor.Layout with
                {
                    Width = decoded.Width,
                    Height = decoded.Height,
                    StrideBytes = decoded.StrideBytes,
                    PixelFormat = decoded.PixelFormat,
                    ByteOrder = FrameByteOrder.NotApplicable,
                    SampleDepthBits = 8,
                    ContainerDepthBits = 8,
                    Packing = FrameSamplePacking.ByteAligned,
                    CfaPattern = ColorFilterArrayPattern.None,
                    BlackLevel = 0,
                    WhiteLevel = 255,
                    ByteLength = output.LongLength
                },
                Artifact = new ArtifactDescriptor(
                    artifactId,
                    FrameArtifactRole.AnnotatedPreview,
                    "central-presentation",
                    OutputVariant,
                    manifestArtifact.CreatedUtc ?? baseArtifact.CreatedUtc ?? baseDescriptor.Timing.ReadoutCompletedUtc,
                    sourceIds,
                    recipe,
                    "application/x-hvo-packed-image",
                    checksum)
            };
            var uploadManifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion,
                descriptor,
                $"materializations/{artifactId:D}.bin",
                ProducerStepId: "central-presentation");
            await using var payloadStream = new MemoryStream(output, writable: false);
            var ingestResult = await ingest.IngestAsync(
                ArtifactManifestDocument.FromCurrent(uploadManifest), payloadStream, cancellationToken).ConfigureAwait(false);
            if (!ingestResult.ReadyForAcknowledgement)
            {
                telemetry.RecordMaterialization("pending", timeProvider.GetElapsedTime(started));
                return new(CentralPresentationMaterializationStatus.DependencyUnavailable);
            }
            telemetry.RecordMaterialization(replayed ? "replayed" : "saved", timeProvider.GetElapsedTime(started));
            return new(CentralPresentationMaterializationStatus.Saved, new(
                captureId,
                artifactId,
                outputIdentity,
                checksum,
                output.LongLength,
                FormattableString.Invariant(
                    $"/api/v1.0/devices/{baseArtifact.Frame!.DevicePublicId:D}/artifacts/{artifactId:D}/content"),
                replayed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            telemetry.RecordMaterialization("unavailable", timeProvider.GetElapsedTime(started));
            return new(CentralPresentationMaterializationStatus.DependencyUnavailable);
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactStorageException or
            MinioException or HttpRequestException or TimeoutException or IOException)
        {
            telemetry.RecordMaterialization("unavailable", timeProvider.GetElapsedTime(started));
            return new(CentralPresentationMaterializationStatus.DependencyUnavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
            InvalidOperationException or CentralArtifactIntegrityException)
        {
            telemetry.RecordMaterialization("failed", timeProvider.GetElapsedTime(started));
            return new(CentralPresentationMaterializationStatus.Conflict);
        }
    }

    private async Task<byte[]> ReadBytesAsync(
        CentralArtifact artifact,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (artifact.ByteLength is < 1 || artifact.ByteLength > maximumBytes || artifact.ByteLength > int.MaxValue)
        {
            throw new InvalidDataException("The retained materialization input exceeds its bound.");
        }
        var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(checked((int)artifact.ByteLength));
        await objectReader.CopyToAsync(snapshot, output, null, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}

internal sealed class CentralPresentationMaterializationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
