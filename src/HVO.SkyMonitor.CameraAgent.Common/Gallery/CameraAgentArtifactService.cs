using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal interface ICameraAgentPreviewEncoder
{
    CameraAgentEncodedPreview Encode(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> payload,
        int maximumDimension,
        int maximumEncodedBytes,
        CancellationToken cancellationToken,
        Mono16DisplayStretchOptions? displayOptions = null);
}

internal sealed record CameraAgentEncodedPreview(byte[] Content, int Width, int Height);

internal sealed class CameraAgentPreviewEncoder : ICameraAgentPreviewEncoder
{
    public CameraAgentEncodedPreview Encode(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> payload,
        int maximumDimension,
        int maximumEncodedBytes,
        CancellationToken cancellationToken,
        Mono16DisplayStretchOptions? displayOptions = null)
    {
        // Explicit comparison policies use the full source histogram, just like the retained recipe.
        // Convert once before resizing; Mono8/RGB and already encoded derivatives are never restretched.
        if (displayOptions is not null && layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16)
        {
            var mono = layout.PixelFormat == CameraPixelFormat.Mono16;
            payload = mono
                ? Mono16DisplayStretch.Apply(layout.Width, layout.Height, payload, cancellationToken, layout.StrideBytes, displayOptions)
                : BayerRggb16Demosaicer.DemosaicToRgb24(layout.Width, layout.Height, payload, cancellationToken, layout.StrideBytes, displayOptions);
            layout = layout with
            {
                PixelFormat = mono ? CameraPixelFormat.Mono8 : CameraPixelFormat.Rgb24,
                StrideBytes = checked(layout.Width * (mono ? 1 : 3)),
                ByteLength = payload.Length,
                ByteOrder = FrameByteOrder.NotApplicable,
                SampleDepthBits = 8,
                ContainerDepthBits = 8,
                CfaPattern = ColorFilterArrayPattern.None
            };
        }
        var dimension = maximumDimension;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resized = PackedImageDownsampler.Downsample(layout, payload, dimension);
            var result = layout.PixelFormat switch
            {
                CameraPixelFormat.Mono8 => SkiaPreviewEncoder.EncodeMono8ToJpeg(
                    resized.Width, resized.Height, resized.Payload),
                CameraPixelFormat.Mono16 => SkiaPreviewEncoder.EncodeMono16ToJpeg(
                    resized.Width, resized.Height, resized.Payload),
                CameraPixelFormat.Rgb24 => SkiaPreviewEncoder.EncodeRgb24ToJpeg(
                    resized.Width, resized.Height, resized.Payload),
                CameraPixelFormat.BayerRggb16 => SkiaPreviewEncoder.EncodeBayerRggb16ToJpeg(
                    resized.Width, resized.Height, resized.Payload),
                _ => throw new NotSupportedException()
            };
            if (result.IsFailure)
            {
                throw new InvalidDataException("The durable preview could not be encoded.", result.Error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Value.Length <= maximumEncodedBytes || resized.Width == 1 && resized.Height == 1)
            {
                return new(result.Value, resized.Width, resized.Height);
            }
            dimension = Math.Max(1, Math.Max(resized.Width, resized.Height) / 2);
        }
    }

    internal static (int Width, int Height, ReadOnlyMemory<byte> Payload) Downsample(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> payload,
        int maximumDimension)
        => PackedImageDownsampler.Downsample(layout, payload, maximumDimension);

}

internal sealed class CameraAgentArtifactService : ICameraAgentArtifactService, IDisposable
{
    private const long MaximumEvidenceBytes = 4L * 1024 * 1024;
    private const int AbsoluteMaximumPreviewEncodedBytes = 16 * 1024 * 1024;
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly ArtifactReadOptions _options;
    private readonly SqliteCaptureProcessingStore _processingStore;
    private readonly ICameraAgentPreviewEncoder _previewEncoder;
    private readonly SemaphoreSlim _previewGate;
    private readonly object _cacheGate = new();
    private readonly Dictionary<PreviewCacheKey, PreviewCacheEntry> _previewCache = [];
    private readonly Dictionary<PreviewCacheKey, PreviewGenerationGate> _previewGenerationGates = [];
    private readonly Dictionary<PreviewRequestKey, PreviewGenerationGate> _previewRequestGates = [];
    private readonly Dictionary<ArtifactValidationCacheKey, ArtifactValidationCacheEntry> _validationCache = [];
    private long _previewCacheBytes;
    private long _cacheSequence;
    private long _validationCacheSequence;
    private long _payloadValidationReads;
    private long _evidenceValidationReads;
    private long _jpegValidationReads;

    public CameraAgentArtifactService(
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore processingStore,
        ICameraAgentPreviewEncoder previewEncoder)
    {
        ArgumentNullException.ThrowIfNull(options);
        _processingStore = processingStore ?? throw new ArgumentNullException(nameof(processingStore));
        _previewEncoder = previewEncoder ?? throw new ArgumentNullException(nameof(previewEncoder));
        var values = options.Value;
        _root = Path.GetFullPath(values.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = values.RawIngressSqliteBusyTimeoutSeconds;
        _options = values.ArtifactRead;
        _previewGate = new SemaphoreSlim(_options.MaximumConcurrentPreviews, _options.MaximumConcurrentPreviews);
    }

    public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(
        Guid artifactId,
        CancellationToken cancellationToken)
        => OpenContentCoreAsync(artifactId, replayExecutionId: null, forPreview: false, cancellationToken);

    public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(
        Guid executionId,
        Guid artifactId,
        CancellationToken cancellationToken)
        => executionId == Guid.Empty
            ? ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound))
            : OpenContentCoreAsync(artifactId, executionId, forPreview: false, cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated retrieval boundary must fail closed without exposing storage or parser exceptions.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The opened immutable payload handle is intentionally transferred to the returned stream lease.")]
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The payload may be assigned when asynchronous validation fails and must then be disposed by this boundary.")]
    private async ValueTask<CameraAgentArtifactContentResult> OpenContentCoreAsync(
        Guid artifactId,
        Guid? replayExecutionId,
        bool forPreview,
        CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty)
        {
            return new(CameraAgentArtifactReadStatus.NotFound);
        }

        var lifecycleGate = StorageLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? payload = null;
        try
        {
            await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
            var evidence = replayExecutionId is { } executionId
                ? await FindReplayOutputEvidenceAsync(connection, executionId, artifactId, cancellationToken)
                    .ConfigureAwait(false)
                : await FindEvidenceAsync(connection, artifactId, cancellationToken).ConfigureAwait(false);
            if (evidence.Status != CameraAgentArtifactReadStatus.Found)
            {
                return new(evidence.Status);
            }

            var row = evidence.Row!;
            var payloadPath = ResolveSafePath(row.PayloadRelativePath);
            var sidecarPath = ResolveSafePath(row.SidecarRelativePath);
            ValidatedArtifact validated;
            var cachedValidation = TryGetValidatedArtifact(row, payloadPath, sidecarPath, out validated);
            if (!cachedValidation)
            {
                validated = await ValidateEvidenceAsync(row, cancellationToken).ConfigureAwait(false);
            }
            if (forPreview && PreviewStatus(validated) is { } previewStatus)
            {
                return new(previewStatus);
            }
            if (!File.Exists(payloadPath))
            {
                return new(CameraAgentArtifactReadStatus.Gone);
            }
            EnsurePhysicalPath(payloadPath);
            payload = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (payload.Length != validated.ByteLength)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
                payload = null;
                return new(CameraAgentArtifactReadStatus.Conflict);
            }
            Interlocked.Increment(ref _payloadValidationReads);
            var checksum = await PayloadChecksum.ComputeSha256Async(payload, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(checksum, validated.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                await payload.DisposeAsync().ConfigureAwait(false);
                payload = null;
                return new(CameraAgentArtifactReadStatus.Conflict);
            }
            if (!cachedValidation)
            {
                AddValidatedArtifact(row, validated, payloadPath, sidecarPath);
            }
            payload.Position = 0;
            var content = new CameraAgentArtifactContentStream(
                payload,
                validated.ArtifactId,
                validated.CaptureId,
                validated.Role,
                validated.MediaType,
                validated.ByteLength,
                validated.ChecksumSha256.ToUpperInvariant(),
                CreateFileName(validated.ArtifactId, validated.MediaType),
                validated.Descriptor,
                validated.EncodedWidth,
                validated.EncodedHeight)
            { ProductManifest = validated.ProductManifest };
            payload = null;
            return new(CameraAgentArtifactReadStatus.Found, content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (payload is not null)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (ArtifactEvidenceException)
        {
            if (payload is not null)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
            }
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or JsonException or ArgumentException or OverflowException)
        {
            if (payload is not null)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
            }
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        catch (FileNotFoundException)
        {
            if (payload is not null)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
            }
            return new(CameraAgentArtifactReadStatus.Gone);
        }
        catch (Exception)
        {
            if (payload is not null)
            {
                await payload.DisposeAsync().ConfigureAwait(false);
            }
            return new(CameraAgentArtifactReadStatus.Unavailable);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Preview encoding failures are converted to a sanitized retrieval status.")]
    public async ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(
        Guid artifactId,
        CancellationToken cancellationToken,
        Guid? displayReference = null)
    {
        if (displayReference == Guid.Empty) return new(CameraAgentArtifactReadStatus.InvalidRequest);
        // Coalesce the complete read, including reference validation, even with one preview slot and no cache.
        // This key lives only for overlapping requests; subsequent requests must validate evidence again.
        var requestKey = new PreviewRequestKey(artifactId, displayReference);
        var requestGate = AddPreviewRequestWaiter(requestKey);
        try
        {
            await requestGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (requestGate.Result is { } completed)
                {
                    return completed;
                }
                DisplayReferencePolicy? policy = null;
                if (displayReference is { } referenceId)
                {
                    await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        policy = await ResolveDisplayReferenceAsync(artifactId, referenceId, cancellationToken).ConfigureAwait(false);
                        if (policy is null) return requestGate.Result = new(CameraAgentArtifactReadStatus.Conflict);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                        JsonException or KeyNotFoundException or FormatException or OverflowException or IOException or InvalidDataException or SqliteException)
                    {
                        return requestGate.Result = new(CameraAgentArtifactReadStatus.Conflict);
                    }
                    finally { _previewGate.Release(); }
                }
                var result = await GetPreviewCoreAsync(artifactId, policy, cancellationToken).ConfigureAwait(false);
                requestGate.Result = result;
                return result;
            }
            finally
            {
                requestGate.Semaphore.Release();
            }
        }
        finally
        {
            RemovePreviewRequestWaiter(requestKey, requestGate);
        }
    }

    private async ValueTask<DisplayReferencePolicy?> ResolveDisplayReferenceAsync(
        Guid artifactId,
        Guid referenceId,
        CancellationToken cancellationToken)
    {
        var referenceRead = await OpenContentCoreAsync(referenceId, null, true, cancellationToken).ConfigureAwait(false);
        if (referenceRead.Content is not { } reference) return null;
        await using var referenceLease = reference.ConfigureAwait(false);
        var referenceArtifact = reference.Descriptor?.Artifact ?? reference.ProductManifest?.Artifact;
        if (reference.Role != FrameArtifactRole.Preview || referenceArtifact is null ||
            referenceArtifact.SourceArtifactIds.Count != 1 ||
            referenceArtifact.SourceArtifactIds[0] == referenceId ||
            referenceArtifact.Recipe is not
            {
                Name: BuiltInProcessingRecipes.EncodedPreview,
                SemanticVersion: "1.0.0", ImplementationVersion: "encoded-preview-v1"
            } recipe)
            return null;

        var options = recipe.Options;
        if (options.GetProperty("schema").GetString() != ProcessingIdentity.BoundInputSchemaVersion ||
            options.GetProperty("annotationIdentitySha256").ValueKind != JsonValueKind.Null ||
            options.GetProperty("auxiliaryInputs").GetArrayLength() != 0)
            return null;
        var parameters = options.GetProperty("parameters");
        var stretch = new Mono16DisplayStretchOptions(
            parameters.GetProperty("blackPercentile").GetDouble(),
            parameters.GetProperty("whitePercentile").GetDouble(),
            parameters.GetProperty("asinhStrength").GetDouble());
        stretch.Validate();
        if (stretch.AsinhStrength is < 0.01 or > 1000 || parameters.GetProperty("jpegQuality").GetInt32() is < 1 or > 100)
            return null;
        var encoding = parameters.GetProperty("outputEncoding").GetString();
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            referenceArtifact.Role, referenceArtifact.Variant, recipeIdentity, referenceArtifact.SourceArtifactIds);
        if (ProcessingIdentity.CreateArtifactId(outputIdentity) != referenceId) return null;
        var recorded = await _processingStore.ReadOutputByArtifactIdAsync(referenceId, cancellationToken).ConfigureAwait(false);
        if (recorded is null || recorded.AvailabilityState != "Available" || recorded.OutputIdentitySha256 != outputIdentity ||
            recorded.RecipeIdentitySha256 != recipeIdentity || recorded.Artifact.ChecksumSha256 != reference.ChecksumSha256)
            return null;

        var sourceRead = await OpenContentCoreAsync(referenceArtifact.SourceArtifactIds[0], null, true, cancellationToken).ConfigureAwait(false);
        if (sourceRead.Content is not { } source) return null;
        await using var sourceLease = source.ConfigureAwait(false);
        if (source.Descriptor is not { } sourceDescriptor ||
            source.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined) ||
            source.CaptureId != reference.CaptureId)
            return null;
        var input = options.GetProperty("input");
        var kind = input.GetProperty("kind").GetString();
        var inputRecipe = input.GetProperty("recipeIdentitySha256").GetString();
        var inputVariant = input.GetProperty("variant").GetString();
        if (input.GetProperty("role").GetString() != source.Role.ToString() ||
            (kind != source.Role.ToString() && kind != nameof(ProcessingInputKind.RecipeResult)) ||
            (kind == nameof(ProcessingInputKind.RecipeResult) && inputRecipe is null) ||
            (inputRecipe is not null && inputRecipe != ProcessingIdentity.CreateRecipeIdentity(sourceDescriptor.Artifact.Recipe).IdentitySha256) ||
            (inputVariant is not null && inputVariant != sourceDescriptor.Artifact.Variant))
            return null;

        var layout = sourceDescriptor.Layout;
        var displayFormat = layout.PixelFormat switch
        {
            CameraPixelFormat.Mono16 => CameraPixelFormat.Mono8,
            CameraPixelFormat.BayerRggb16 => CameraPixelFormat.Rgb24,
            _ => layout.PixelFormat
        };
        if (encoding == "Packed")
        {
            if (reference.MediaType != "application/x-hvo-packed-image" || reference.Descriptor is not { } derivative ||
                derivative.Capture != sourceDescriptor.Capture ||
                derivative.Profiles.Rig != sourceDescriptor.Profiles.Rig ||
                derivative.Profiles.Sensor != sourceDescriptor.Profiles.Sensor ||
                derivative.Layout.Width != layout.Width || derivative.Layout.Height != layout.Height ||
                derivative.Layout.PixelFormat != displayFormat)
                return null;
        }
        else if (encoding == "Jpeg")
        {
            if (reference.MediaType != JpegImageCodec.MediaType ||
                reference.ProductManifest is not DurableEncodedProductManifestV2 encoded ||
                encoded.Capture != sourceDescriptor.Capture || encoded.EncodedWidth != layout.Width ||
                encoded.EncodedHeight != layout.Height || encoded.EncodedPixelFormat != displayFormat)
                return null;
            var bytes = new byte[checked((int)reference.ByteLength)];
            await reference.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var info = JpegImageCodec.InspectJpeg(bytes);
            if (info.Width != layout.Width || info.Height != layout.Height || info.PixelFormat != displayFormat) return null;
            Interlocked.Increment(ref _jpegValidationReads);
            JpegImageCodec.ValidateJpeg(bytes, cancellationToken);
        }
        else return null;

        var targetRead = await OpenContentCoreAsync(artifactId, null, true, cancellationToken).ConfigureAwait(false);
        if (targetRead.Content is not { } target) return null;
        await using var targetLease = target.ConfigureAwait(false);
        if (artifactId != referenceId)
        {
            if (target.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined) ||
                target.Descriptor is not { } targetDescriptor || targetDescriptor.Capture != sourceDescriptor.Capture ||
                targetDescriptor.Profiles.Rig != sourceDescriptor.Profiles.Rig ||
                targetDescriptor.Profiles.Sensor != sourceDescriptor.Profiles.Sensor ||
                targetDescriptor.Layout.Width != layout.Width || targetDescriptor.Layout.Height != layout.Height ||
                targetDescriptor.Layout.PixelFormat != layout.PixelFormat ||
                targetDescriptor.Layout.CfaPattern != layout.CfaPattern ||
                (target.Role == FrameArtifactRole.Combined && target.ArtifactId != source.ArtifactId))
                return null;
        }

        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            version = "capture-display-reference-v1",
            algorithm = Mono16DisplayStretch.AlgorithmVersion,
            referenceId,
            recipeIdentity,
            reference.ChecksumSha256,
            sourceDescriptor = CaptureContractJson.ComputeDescriptorSha256(sourceDescriptor)
        });
        var description = FormattableString.Invariant(
            $"Capture-bound display policy from retained artifact {referenceId:D} (recipe {recipeIdentity}; {Mono16DisplayStretch.AlgorithmVersion}, black={stretch.BlackPercentile} white={stretch.WhitePercentile} asinh={stretch.AsinhStrength}). Same percentile settings, each image's own histogram; not a locked transfer curve and not calibration. Source pixels are never replaced. Mono8/RGB/JPEG pixels are not restretched.");
        return new(identity, stretch, description);
    }

    private async ValueTask<CameraAgentArtifactPreviewResult> GetPreviewCoreAsync(
        Guid artifactId,
        DisplayReferencePolicy? policy,
        CancellationToken cancellationToken)
    {
        await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previewGateHeld = true;
        try
        {
            var opened = await OpenContentCoreAsync(
                artifactId,
                replayExecutionId: null,
                forPreview: true,
                cancellationToken).ConfigureAwait(false);
            if (opened.Status != CameraAgentArtifactReadStatus.Found || opened.Content is null)
            {
                return new(opened.Status);
            }
            var content = opened.Content;
            if (CameraAgentPreviewEligibilityPolicy.IsEncodedJpeg(content.Role, content.MediaType))
            {
                var jpeg = await ReadEncodedJpegAsync(content, cancellationToken).ConfigureAwait(false);
                return jpeg with { DisplayPolicyIdentity = policy?.Identity, DisplayPolicy = policy?.Description };
            }
            if (content.Descriptor is not { } descriptor ||
                !CameraAgentPreviewEligibilityPolicy.IsSupportedLayout(descriptor.Layout))
            {
                await content.DisposeAsync().ConfigureAwait(false);
                return new(CameraAgentArtifactReadStatus.UnsupportedMediaType);
            }

            var layout = descriptor.Layout;
            var cacheKey = new PreviewCacheKey(
                content.ChecksumSha256,
                layout.Width,
                layout.Height,
                layout.PixelFormat,
                policy?.Identity);
            CameraAgentArtifactPreviewResult cached;
            bool cacheHit;
            lock (_cacheGate)
            {
                cacheHit = TryGetCachedLocked(cacheKey, out cached);
            }
            if (cacheHit)
            {
                await content.DisposeAsync().ConfigureAwait(false);
                return cached;
            }

            _previewGate.Release();
            previewGateHeld = false;
            var generationGate = AddPreviewGenerationWaiter(cacheKey);
            var contentOwned = true;
            try
            {
                await generationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (generationGate.Result is { } completed)
                    {
                        await content.DisposeAsync().ConfigureAwait(false);
                        contentOwned = false;
                        return completed;
                    }
                    lock (_cacheGate)
                    {
                        cacheHit = TryGetCachedLocked(cacheKey, out cached);
                    }
                    if (cacheHit)
                    {
                        await content.DisposeAsync().ConfigureAwait(false);
                        contentOwned = false;
                        return cached;
                    }
                    await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    previewGateHeld = true;
                    contentOwned = false;
                    var result = await EncodePreviewAsync(cacheKey, content, descriptor, policy, cancellationToken).ConfigureAwait(false);
                    generationGate.Result = result;
                    return result;
                }
                finally
                {
                    generationGate.Semaphore.Release();
                }
            }
            finally
            {
                RemovePreviewGenerationWaiter(cacheKey, generationGate);
                if (contentOwned)
                {
                    await content.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (previewGateHeld)
            {
                _previewGate.Release();
            }
        }
    }

    private async ValueTask<CameraAgentArtifactPreviewResult> ReadEncodedJpegAsync(
        CameraAgentArtifactContentStream content,
        CancellationToken cancellationToken)
    {
        await using var contentLease = content.ConfigureAwait(false);
        if (content.ByteLength > Math.Min(_options.MaximumPreviewEncodedBytes, AbsoluteMaximumPreviewEncodedBytes) ||
            content.ByteLength > int.MaxValue)
        {
            return new(CameraAgentArtifactReadStatus.TooLarge);
        }
        try
        {
            var encoded = new byte[checked((int)content.ByteLength)];
            await content.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
            var info = JpegImageCodec.InspectJpeg(encoded);
            if (content.EncodedWidth != info.Width || content.EncodedHeight != info.Height)
            {
                return new(CameraAgentArtifactReadStatus.Conflict);
            }
            return new(
                CameraAgentArtifactReadStatus.Found,
                encoded,
                content.ChecksumSha256,
                info.Width,
                info.Height,
                Operation: CameraAgentPreviewOperation.EncodedPassthrough);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        catch (EndOfStreamException)
        {
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        catch (IOException)
        {
            return new(CameraAgentArtifactReadStatus.Unavailable);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Preview encoder failures are converted to a sanitized artifact status.")]
    private async ValueTask<CameraAgentArtifactPreviewResult> EncodePreviewAsync(
        PreviewCacheKey cacheKey,
        CameraAgentArtifactContentStream content,
        ReconstructionDescriptor descriptor,
        DisplayReferencePolicy? policy,
        CancellationToken cancellationToken)
    {
        await using var contentLease = content.ConfigureAwait(false);
        var source = new byte[checked((int)content.ByteLength)];
        await content.ReadExactlyAsync(source, cancellationToken).ConfigureAwait(false);
        if (!FrameReconstructor.TryReconstruct(descriptor, source, out _, verifyChecksum: false).IsValid)
        {
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        CameraAgentEncodedPreview encoded;
        try
        {
            encoded = _previewEncoder.Encode(
                descriptor.Layout,
                source,
                _options.MaximumPreviewDimension,
                Math.Min(_options.MaximumPreviewEncodedBytes, AbsoluteMaximumPreviewEncodedBytes),
                cancellationToken,
                policy?.Options);
        }
        catch (NotSupportedException)
        {
            return new(CameraAgentArtifactReadStatus.UnsupportedMediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(CameraAgentArtifactReadStatus.Conflict);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (encoded.Content.Length > Math.Min(
                _options.MaximumPreviewEncodedBytes,
                AbsoluteMaximumPreviewEncodedBytes))
        {
            return new(CameraAgentArtifactReadStatus.TooLarge);
        }
        var result = new CameraAgentArtifactPreviewResult(
            CameraAgentArtifactReadStatus.Found,
            encoded.Content,
            PayloadChecksum.ComputeSha256(encoded.Content),
            encoded.Width,
            encoded.Height,
            policy?.Identity,
            policy?.Description,
            descriptor.Layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16
                ? CameraAgentPreviewOperation.PerImageStretch : CameraAgentPreviewOperation.EncodeOnly);
        cancellationToken.ThrowIfCancellationRequested();
        AddCached(cacheKey, result);
        return result;
    }

    private async ValueTask<ValidatedArtifact> ValidateEvidenceAsync(
        ArtifactEvidenceRow row,
        CancellationToken cancellationToken)
    {
        if (row.Kind == ArtifactEvidenceKind.Raw && row.RawState == "missing_evidence")
        {
            throw new FileNotFoundException();
        }
        if (row.Kind == ArtifactEvidenceKind.Raw && row.RawState != "committed")
        {
            throw new ArtifactEvidenceException();
        }
        var payloadPath = ResolveSafePath(row.PayloadRelativePath);
        var sidecarPath = ResolveSafePath(row.SidecarRelativePath);
        if (string.Equals(payloadPath, sidecarPath, PathComparison))
        {
            throw new ArtifactEvidenceException();
        }
        EnsurePhysicalPath(payloadPath);
        EnsurePhysicalPath(sidecarPath);
        if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
        {
            throw new FileNotFoundException();
        }
        var sidecarLength = new FileInfo(sidecarPath).Length;
        if (sidecarLength is < 1 or > MaximumEvidenceBytes)
        {
            throw new ArtifactEvidenceException();
        }
        Interlocked.Increment(ref _evidenceValidationReads);
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (!sidecar.AsSpan().SequenceEqual(row.EvidenceJson))
        {
            throw new ArtifactEvidenceException();
        }
        return row.Kind == ArtifactEvidenceKind.Raw
            ? ValidateRaw(row, sidecar)
            : ValidateProcessing(row, sidecar);
    }

    private static ValidatedArtifact ValidateRaw(ArtifactEvidenceRow row, byte[] sidecar)
    {
        if (!string.Equals(CaptureContractJson.ComputeManifestSha256(sidecar), row.ManifestSha256, StringComparison.Ordinal))
        {
            throw new ArtifactEvidenceException();
        }
        var parsed = CaptureContractJson.ParseManifest(sidecar);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null ||
            manifest.Descriptor.Capture.CaptureId != row.CaptureId ||
            manifest.Descriptor.Artifact.ArtifactId != row.ArtifactId ||
            manifest.Descriptor.Artifact.Role != FrameArtifactRole.Raw ||
            !string.Equals(manifest.RelativeArtifactPath, row.PayloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(manifest.Descriptor.Artifact.MediaType, row.MediaType, StringComparison.OrdinalIgnoreCase) ||
            manifest.Descriptor.Layout.ByteLength != row.ByteLength ||
            !string.Equals(manifest.Descriptor.Artifact.ChecksumSha256, row.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactEvidenceException();
        }
        return CreateValidated(manifest.Descriptor, manifest.RelativeArtifactPath);
    }

    private static ValidatedArtifact ValidateProcessing(ArtifactEvidenceRow row, byte[] sidecar)
    {
        try
        {
            using var document = JsonDocument.Parse(sidecar);
            if (document.RootElement.TryGetProperty("schemaVersion", out var schema) &&
                IsDurableProductSchema(schema.GetString()))
            {
                var product = DurableProcessingProductManifestJson.Parse(sidecar);
                ValidateProcessingFacts(
                    row,
                    product.Capture,
                    product.Artifact,
                    product.RelativeArtifactPath,
                    product.ByteLength);
                var encoded = product as DurableEncodedProductManifestV2;
                return new ValidatedArtifact(
                    product.Artifact.ArtifactId,
                    product.Capture.CaptureId,
                    product.Artifact.Role,
                    product.Artifact.MediaType,
                    product.ByteLength,
                    product.Artifact.ChecksumSha256,
                    product.RelativeArtifactPath,
                    null,
                    encoded?.EncodedWidth,
                    encoded?.EncodedHeight,
                    product);
            }
        }
        catch (JsonException exception)
        {
            throw new ArtifactEvidenceException(exception);
        }

        var parsed = CaptureContractJson.ParseManifest(sidecar);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null)
        {
            throw new ArtifactEvidenceException();
        }
        ValidateProcessingFacts(
            row,
            manifest.Descriptor.Capture,
            manifest.Descriptor.Artifact,
            manifest.RelativeArtifactPath,
            manifest.Descriptor.Layout.ByteLength);
        return CreateValidated(manifest.Descriptor, manifest.RelativeArtifactPath);
    }

    private static void ValidateProcessingFacts(
        ArtifactEvidenceRow row,
        CaptureIdentityDescriptor capture,
        ArtifactDescriptor artifact,
        string payloadRelativePath,
        long byteLength)
    {
        if (capture.CaptureId != row.CaptureId || artifact.ArtifactId != row.ArtifactId ||
            !string.Equals(payloadRelativePath, row.PayloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(artifact.MediaType, row.MediaType, StringComparison.OrdinalIgnoreCase) ||
            byteLength != row.ByteLength ||
            !string.Equals(artifact.ChecksumSha256, row.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactEvidenceException();
        }
    }

    private static ValidatedArtifact CreateValidated(ReconstructionDescriptor descriptor, string payloadRelativePath)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Capture.CaptureId,
            descriptor.Artifact.Role,
            descriptor.Artifact.MediaType,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256,
            payloadRelativePath,
            descriptor,
            null,
            null);

    private static async ValueTask<ArtifactEvidenceLookup> FindEvidenceAsync(
        SqliteConnection connection,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var rows = new List<ArtifactEvidenceRow>(2);
        using (var raw = connection.CreateCommand())
        {
            raw.CommandText = """
                SELECT capture_id, raw_artifact_id, payload_relative_path, sidecar_relative_path,
                       manifest_json, manifest_sha256, payload_sha256, payload_length, state
                FROM raw_captures WHERE raw_artifact_id = $artifact_id;
                """;
            raw.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            using var reader = await raw.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var evidence = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
                var descriptor = ParseRawDescriptor(evidence);
                rows.Add(new ArtifactEvidenceRow(
                    ArtifactEvidenceKind.Raw,
                    Guid.ParseExact(reader.GetString(0), "N"),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2),
                    reader.GetString(3),
                    evidence,
                    reader.GetString(5),
                    descriptor.Artifact.MediaType,
                    reader.GetInt64(7),
                    reader.GetString(6),
                    reader.GetString(8)));
            }
        }
        using (var output = connection.CreateCommand())
        {
            output.CommandText = """
                SELECT output.capture_id, output.artifact_id, output.payload_relative_path,
                       output.sidecar_relative_path, output.descriptor_json, output.availability_state
                FROM processing_outputs AS output
                INNER JOIN processing_nodes AS node
                    ON node.capture_id = output.capture_id AND node.node_id = output.node_id
                WHERE output.artifact_id = $artifact_id
                  AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                   WHERE association.output_identity_sha256 = output.output_identity_sha256)
                       OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                  WHERE association.output_identity_sha256 = output.output_identity_sha256
                                    AND association.published_flag = 1));
                """;
            output.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            using var reader = await output.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(5), "Available", StringComparison.Ordinal))
                {
                    return new(CameraAgentArtifactReadStatus.Unavailable);
                }
                var evidence = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
                var facts = ParseProcessingFacts(evidence);
                rows.Add(new ArtifactEvidenceRow(
                    ArtifactEvidenceKind.Processing,
                    Guid.ParseExact(reader.GetString(0), "N"),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2),
                    reader.GetString(3),
                    evidence,
                    null,
                    facts.MediaType,
                    facts.ByteLength,
                    facts.ChecksumSha256,
                    null));
            }
        }
        return rows.Count switch
        {
            0 => new(CameraAgentArtifactReadStatus.NotFound),
            1 => new(CameraAgentArtifactReadStatus.Found, rows[0]),
            _ => new(CameraAgentArtifactReadStatus.Conflict)
        };
    }

    private static async ValueTask<ArtifactEvidenceLookup> FindReplayOutputEvidenceAsync(
        SqliteConnection connection,
        Guid executionId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var rows = new List<ArtifactEvidenceRow>(2);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.capture_id, output.artifact_id, output.payload_relative_path,
                   output.sidecar_relative_path, output.descriptor_json, output.availability_state
            FROM processing_executions execution
            JOIN processing_execution_outputs association
              ON association.execution_id = execution.execution_id
            JOIN processing_outputs output
              ON output.output_identity_sha256 = association.output_identity_sha256
            WHERE execution.execution_id = $execution_id
              AND execution.execution_class = 'Replay'
              AND execution.allow_automatic_publication = 0
              AND output.capture_id = execution.capture_id
              AND output.artifact_id = $artifact_id;
            """;
        command.Parameters.AddWithValue("$execution_id", executionId.ToString("N"));
        command.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(reader.GetString(5), "Available", StringComparison.Ordinal))
            {
                return new(CameraAgentArtifactReadStatus.Unavailable);
            }
            var evidence = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
            var facts = ParseProcessingFacts(evidence);
            rows.Add(new ArtifactEvidenceRow(
                ArtifactEvidenceKind.Processing,
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetString(2),
                reader.GetString(3),
                evidence,
                null,
                facts.MediaType,
                facts.ByteLength,
                facts.ChecksumSha256,
                null));
        }
        return rows.Count switch
        {
            0 => new(CameraAgentArtifactReadStatus.NotFound),
            1 => new(CameraAgentArtifactReadStatus.Found, rows[0]),
            _ => new(CameraAgentArtifactReadStatus.Conflict)
        };
    }

    private static ReconstructionDescriptor ParseRawDescriptor(byte[] evidence)
    {
        var parsed = CaptureContractJson.ParseManifest(evidence);
        return parsed.IsValid && parsed.Document?.Manifest?.Descriptor is { } descriptor
            ? descriptor
            : throw new ArtifactEvidenceException();
    }

    private static ArtifactFacts ParseProcessingFacts(byte[] evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(evidence);
            if (document.RootElement.TryGetProperty("schemaVersion", out var schema) &&
                IsDurableProductSchema(schema.GetString()))
            {
                var product = DurableProcessingProductManifestJson.Parse(evidence);
                return new(product.Artifact.MediaType, product.ByteLength, product.Artifact.ChecksumSha256);
            }
        }
        catch (JsonException exception)
        {
            throw new ArtifactEvidenceException(exception);
        }
        var parsed = CaptureContractJson.ParseManifest(evidence);
        var manifest = parsed.Document?.Manifest;
        return parsed.IsValid && manifest is not null
            ? new(manifest.Descriptor.Artifact.MediaType, manifest.Descriptor.Layout.ByteLength,
                manifest.Descriptor.Artifact.ChecksumSha256)
            : throw new ArtifactEvidenceException();
    }

    private static bool IsDurableProductSchema(string? schema)
        => string.Equals(schema, DurableProcessingProductManifestV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
           string.Equals(schema, DurableEncodedProductManifestV2.CurrentSchemaVersion, StringComparison.Ordinal) ||
           string.Equals(schema, DurableTypedMetadataProductManifestV3.CurrentSchemaVersion, StringComparison.Ordinal);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async ValueTask<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        EnsurePhysicalPath(_databasePath);
        EnsurePhysicalPath(string.Concat(_databasePath, "-wal"));
        EnsurePhysicalPath(string.Concat(_databasePath, "-shm"));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            connection,
            async (configuredConnection, token) =>
        {
            using var command = configuredConnection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = $"PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArtifactEvidenceException();
        }
        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var normalized = Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(relativePath, normalized, StringComparison.Ordinal) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static part => part is "." or ".."))
        {
            throw new ArtifactEvidenceException();
        }
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            throw new ArtifactEvidenceException();
        }
        return fullPath;
    }

    private void EnsurePhysicalPath(string path)
    {
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        }
        catch (IOException exception)
        {
            throw new ArtifactEvidenceException(exception);
        }
    }

    private CameraAgentArtifactReadStatus? PreviewStatus(ValidatedArtifact artifact)
    {
        var eligibility = CameraAgentPreviewEligibilityPolicy.Evaluate(
            artifact.Role,
            artifact.MediaType,
            artifact.ByteLength,
            artifact.Descriptor?.Layout.PixelFormat,
            artifact.Descriptor is { } descriptor &&
                CameraAgentPreviewEligibilityPolicy.IsSupportedLayout(descriptor.Layout),
            artifact.EncodedWidth,
            artifact.EncodedHeight,
            _options);
        return eligibility switch
        {
            CameraAgentPreviewEligibility.Available => null,
            CameraAgentPreviewEligibility.UnsupportedMediaType => CameraAgentArtifactReadStatus.UnsupportedMediaType,
            CameraAgentPreviewEligibility.TooLarge => CameraAgentArtifactReadStatus.TooLarge,
            CameraAgentPreviewEligibility.Invalid => CameraAgentArtifactReadStatus.Conflict,
            _ => throw new InvalidOperationException("The preview eligibility is invalid.")
        };
    }

    // Internal formats keep .bin but name their encoding so a download is self-describing.
    private static readonly Dictionary<string, string> FileNameSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["application/json"] = ".json",
        ["application/x-skymonitor-mono8"] = "-raw-mono8.bin",
        ["application/x-skymonitor-mono16"] = "-raw-mono16.bin",
        ["application/x-skymonitor-rgb24"] = "-raw-rgb24.bin",
        ["application/x-skymonitor-bayer-rggb16"] = "-raw-bayer-rggb16.bin",
        ["application/x-hvo-linear-frame"] = "-linear-frame.bin",
        ["application/x-hvo-packed-image"] = "-packed-image.bin"
    };

    // Structured metadata uses vendor JSON types such as application/vnd.hvo.overlay-manifest+json.
    private static string CreateFileName(Guid artifactId, string mediaType) =>
        string.Concat(artifactId.ToString("D"), FileNameSuffixes.GetValueOrDefault(mediaType,
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".bin"));

    private bool TryGetCachedLocked(PreviewCacheKey key, out CameraAgentArtifactPreviewResult result)
    {
        if (_previewCache.TryGetValue(key, out var entry))
        {
            entry.Sequence = ++_cacheSequence;
            result = entry.Result;
            return true;
        }
        result = default!;
        return false;
    }

    private void AddCached(PreviewCacheKey key, CameraAgentArtifactPreviewResult result)
    {
        if (result.Content.Length > _options.PreviewCacheBytes)
        {
            return;
        }
        lock (_cacheGate)
        {
            if (_previewCache.ContainsKey(key))
            {
                return;
            }
            while (_previewCacheBytes + result.Content.Length > _options.PreviewCacheBytes && _previewCache.Count > 0)
            {
                var oldest = _previewCache.MinBy(static pair => pair.Value.Sequence);
                _previewCache.Remove(oldest.Key);
                _previewCacheBytes -= oldest.Value.Result.Content.Length;
            }
            _previewCache.Add(key, new PreviewCacheEntry(result, ++_cacheSequence));
            _previewCacheBytes += result.Content.Length;
        }
    }

    private PreviewGenerationGate AddPreviewGenerationWaiter(PreviewCacheKey key)
    {
        lock (_cacheGate)
        {
            if (!_previewGenerationGates.TryGetValue(key, out var gate))
            {
                gate = new PreviewGenerationGate();
                _previewGenerationGates.Add(key, gate);
            }
            gate.Waiters++;
            return gate;
        }
    }

    private PreviewGenerationGate AddPreviewRequestWaiter(PreviewRequestKey key)
    {
        lock (_cacheGate)
        {
            if (!_previewRequestGates.TryGetValue(key, out var gate))
            {
                gate = new PreviewGenerationGate();
                _previewRequestGates.Add(key, gate);
            }
            gate.Waiters++;
            return gate;
        }
    }

    private void RemovePreviewRequestWaiter(PreviewRequestKey key, PreviewGenerationGate gate)
    {
        lock (_cacheGate)
        {
            gate.Waiters--;
            if (gate.Waiters == 0)
            {
                _previewRequestGates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private void RemovePreviewGenerationWaiter(PreviewCacheKey key, PreviewGenerationGate gate)
    {
        lock (_cacheGate)
        {
            gate.Waiters--;
            if (gate.Waiters == 0)
            {
                _previewGenerationGates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private bool TryGetValidatedArtifact(
        ArtifactEvidenceRow row,
        string payloadPath,
        string sidecarPath,
        out ValidatedArtifact validated)
    {
        var key = new ArtifactValidationCacheKey(row.ArtifactId, row.ChecksumSha256);
        lock (_cacheGate)
        {
            if (_validationCache.TryGetValue(key, out var entry) &&
                entry.EvidenceIdentity == EvidenceIdentity(row) &&
                MatchesFileIdentity(payloadPath, entry.PayloadIdentity) &&
                MatchesFileIdentity(sidecarPath, entry.SidecarIdentity))
            {
                entry.Sequence = ++_validationCacheSequence;
                validated = entry.Validated;
                return true;
            }
            _validationCache.Remove(key);
        }
        validated = default!;
        return false;
    }

    private void AddValidatedArtifact(
        ArtifactEvidenceRow row,
        ValidatedArtifact validated,
        string payloadPath,
        string sidecarPath)
    {
        var entry = new ArtifactValidationCacheEntry(
            validated,
            EvidenceIdentity(row),
            ReadFileIdentity(payloadPath),
            ReadFileIdentity(sidecarPath),
            0);
        lock (_cacheGate)
        {
            var key = new ArtifactValidationCacheKey(row.ArtifactId, row.ChecksumSha256);
            entry.Sequence = ++_validationCacheSequence;
            _validationCache[key] = entry;
            while (_validationCache.Count > _options.ValidationCacheEntries)
            {
                var oldest = _validationCache.MinBy(static pair => pair.Value.Sequence);
                _validationCache.Remove(oldest.Key);
            }
        }
    }

    private static string EvidenceIdentity(ArtifactEvidenceRow row)
    {
        var prefix = string.Join('\n',
            row.Kind, row.CaptureId, row.ArtifactId, row.PayloadRelativePath, row.SidecarRelativePath,
            row.ManifestSha256, row.MediaType, row.ByteLength, row.ChecksumSha256, row.RawState);
        var prefixBytes = System.Text.Encoding.UTF8.GetBytes(prefix);
        var bytes = new byte[prefixBytes.Length + row.EvidenceJson.Length];
        prefixBytes.CopyTo(bytes, 0);
        row.EvidenceJson.CopyTo(bytes, prefixBytes.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static FileIdentity ReadFileIdentity(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new FileIdentity(info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks);
    }

    private static bool MatchesFileIdentity(string path, FileIdentity expected)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists && info.Length == expected.Length &&
            info.CreationTimeUtc.Ticks == expected.CreationUtcTicks &&
            info.LastWriteTimeUtc.Ticks == expected.LastWriteUtcTicks;
    }

    internal long PayloadValidationReads => Interlocked.Read(ref _payloadValidationReads);

    internal long EvidenceValidationReads => Interlocked.Read(ref _evidenceValidationReads);

    internal long JpegValidationReads => Interlocked.Read(ref _jpegValidationReads);

    internal int PreviewRequestWaiters
    {
        get
        {
            lock (_cacheGate)
            {
                return _previewRequestGates.Values.Sum(static gate => gate.Waiters);
            }
        }
    }

    public void Dispose()
    {
        _previewGate.Dispose();
        lock (_cacheGate)
        {
            foreach (var gate in _previewGenerationGates.Values)
            {
                gate.Semaphore.Dispose();
            }
            _previewGenerationGates.Clear();
            foreach (var gate in _previewRequestGates.Values)
            {
                gate.Semaphore.Dispose();
            }
            _previewRequestGates.Clear();
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private control-flow exception never crosses the service boundary.")]
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private control-flow exception never crosses the service boundary.")]
    private sealed class ArtifactEvidenceException : Exception
    {
        internal ArtifactEvidenceException()
        {
        }

        internal ArtifactEvidenceException(Exception innerException)
            : base("Durable artifact evidence is invalid.", innerException)
        {
        }
    }

    private enum ArtifactEvidenceKind
    {
        Raw,
        Processing
    }

    private sealed record ArtifactEvidenceRow(
        ArtifactEvidenceKind Kind,
        Guid CaptureId,
        Guid ArtifactId,
        string PayloadRelativePath,
        string SidecarRelativePath,
        byte[] EvidenceJson,
        string? ManifestSha256,
        string MediaType,
        long ByteLength,
        string ChecksumSha256,
        string? RawState);

    private sealed record ArtifactEvidenceLookup(
        CameraAgentArtifactReadStatus Status,
        ArtifactEvidenceRow? Row = null);

    private sealed record ArtifactFacts(string MediaType, long ByteLength, string ChecksumSha256);

    private sealed record ValidatedArtifact(
        Guid ArtifactId,
        Guid CaptureId,
        FrameArtifactRole Role,
        string MediaType,
        long ByteLength,
        string ChecksumSha256,
        string PayloadRelativePath,
        ReconstructionDescriptor? Descriptor,
        int? EncodedWidth,
        int? EncodedHeight,
        IDurableProcessingProductManifest? ProductManifest = null);

    private sealed record DisplayReferencePolicy(string Identity, Mono16DisplayStretchOptions Options, string Description);

    private sealed record PreviewRequestKey(Guid ArtifactId, Guid? ReferenceId);

    private sealed record PreviewCacheKey(
        string ChecksumSha256,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        string? PolicyIdentity);

    private sealed class PreviewCacheEntry(CameraAgentArtifactPreviewResult result, long sequence)
    {
        internal CameraAgentArtifactPreviewResult Result { get; } = result;

        internal long Sequence { get; set; } = sequence;
    }

    private sealed class PreviewGenerationGate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal CameraAgentArtifactPreviewResult? Result { get; set; }

        internal int Waiters { get; set; }
    }

    private sealed record ArtifactValidationCacheKey(Guid ArtifactId, string ChecksumSha256);

    private sealed record FileIdentity(long Length, long CreationUtcTicks, long LastWriteUtcTicks);

    private sealed class ArtifactValidationCacheEntry(
        ValidatedArtifact validated,
        string evidenceIdentity,
        FileIdentity payloadIdentity,
        FileIdentity sidecarIdentity,
        long sequence)
    {
        internal ValidatedArtifact Validated { get; } = validated;
        internal string EvidenceIdentity { get; } = evidenceIdentity;
        internal FileIdentity PayloadIdentity { get; } = payloadIdentity;
        internal FileIdentity SidecarIdentity { get; } = sidecarIdentity;
        internal long Sequence { get; set; } = sequence;
    }

}
