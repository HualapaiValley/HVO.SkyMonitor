using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
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
        CancellationToken cancellationToken);
}

internal sealed record CameraAgentEncodedPreview(byte[] Content, int Width, int Height);

internal sealed class CameraAgentPreviewEncoder : ICameraAgentPreviewEncoder
{
    public CameraAgentEncodedPreview Encode(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> payload,
        int maximumDimension,
        int maximumEncodedBytes,
        CancellationToken cancellationToken)
    {
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
    private readonly Dictionary<Guid, PreviewGenerationGate> _previewRequestGates = [];
    private readonly Dictionary<ArtifactValidationCacheKey, ArtifactValidationCacheEntry> _validationCache = [];
    private long _previewCacheBytes;
    private long _cacheSequence;
    private long _validationCacheSequence;
    private long _payloadValidationReads;
    private long _evidenceValidationReads;

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
                validated.EncodedHeight);
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
        CancellationToken cancellationToken)
    {
        var requestGate = AddPreviewRequestWaiter(artifactId);
        try
        {
            await requestGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (requestGate.Result is { } completed)
                {
                    return completed;
                }
                var result = await GetPreviewCoreAsync(artifactId, cancellationToken).ConfigureAwait(false);
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
            RemovePreviewRequestWaiter(artifactId, requestGate);
        }
    }

    private async ValueTask<CameraAgentArtifactPreviewResult> GetPreviewCoreAsync(
        Guid artifactId,
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
                return await ReadEncodedJpegAsync(content, cancellationToken).ConfigureAwait(false);
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
                layout.PixelFormat);
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
                    var result = await EncodePreviewAsync(cacheKey, content, descriptor, cancellationToken).ConfigureAwait(false);
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
                info.Height);
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
                cancellationToken);
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
            encoded.Height);
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
                    encoded?.EncodedHeight);
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

    private static string CreateFileName(Guid artifactId, string mediaType)
    {
        var extension = string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                    ? ".json"
                    : ".bin";
        return string.Concat(artifactId.ToString("D"), extension);
    }

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

    private PreviewGenerationGate AddPreviewRequestWaiter(Guid artifactId)
    {
        lock (_cacheGate)
        {
            if (!_previewRequestGates.TryGetValue(artifactId, out var gate))
            {
                gate = new PreviewGenerationGate();
                _previewRequestGates.Add(artifactId, gate);
            }
            gate.Waiters++;
            return gate;
        }
    }

    private void RemovePreviewRequestWaiter(Guid artifactId, PreviewGenerationGate gate)
    {
        lock (_cacheGate)
        {
            gate.Waiters--;
            if (gate.Waiters == 0)
            {
                _previewRequestGates.Remove(artifactId);
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
        int? EncodedHeight);

    private sealed record PreviewCacheKey(
        string ChecksumSha256,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat);

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
