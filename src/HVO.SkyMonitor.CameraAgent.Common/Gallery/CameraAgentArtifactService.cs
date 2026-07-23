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
    byte[] Encode(FrameLayoutDescriptor layout, ReadOnlyMemory<byte> payload);
}

internal sealed class CameraAgentPreviewEncoder : ICameraAgentPreviewEncoder
{
    public byte[] Encode(FrameLayoutDescriptor layout, ReadOnlyMemory<byte> payload)
    {
        var result = layout.PixelFormat switch
        {
            CameraPixelFormat.Mono8 => SkiaPreviewEncoder.EncodeMono8ToJpeg(
                layout.Width, layout.Height, payload),
            CameraPixelFormat.Mono16 => SkiaPreviewEncoder.EncodeMono16ToJpeg(
                layout.Width, layout.Height, payload),
            CameraPixelFormat.Rgb24 => SkiaPreviewEncoder.EncodeRgb24ToJpeg(
                layout.Width, layout.Height, payload),
            CameraPixelFormat.BayerRggb16 => SkiaPreviewEncoder.EncodeBayerRggb16ToJpeg(
                layout.Width, layout.Height, payload),
            _ => throw new NotSupportedException()
        };
        if (result.IsFailure)
        {
            throw new InvalidDataException("The durable preview could not be encoded.", result.Error);
        }
        return result.Value;
    }
}

internal sealed class CameraAgentArtifactService : ICameraAgentArtifactService, IDisposable
{
    private const long MaximumEvidenceBytes = 4L * 1024 * 1024;
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly ArtifactReadOptions _options;
    private readonly SqliteCaptureProcessingStore _processingStore;
    private readonly ICameraAgentPreviewEncoder _previewEncoder;
    private readonly SemaphoreSlim _previewGate;
    private readonly object _cacheGate = new();
    private readonly Dictionary<PreviewCacheKey, PreviewCacheEntry> _previewCache = [];
    private readonly Dictionary<PreviewCacheKey, Task<CameraAgentArtifactPreviewResult>> _previewFlights = [];
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated retrieval boundary must fail closed without exposing storage or parser exceptions.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The opened immutable payload handle is intentionally transferred to the returned stream lease.")]
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The payload may be assigned when asynchronous validation fails and must then be disposed by this boundary.")]
    public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(
        Guid artifactId,
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
            var evidence = await FindEvidenceAsync(connection, artifactId, cancellationToken).ConfigureAwait(false);
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
            if (!cachedValidation)
            {
                Interlocked.Increment(ref _payloadValidationReads);
                var checksum = await PayloadChecksum.ComputeSha256Async(payload, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(checksum, validated.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    await payload.DisposeAsync().ConfigureAwait(false);
                    payload = null;
                    return new(CameraAgentArtifactReadStatus.Conflict);
                }
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
                validated.Descriptor);
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
        var opened = await OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (opened.Status != CameraAgentArtifactReadStatus.Found || opened.Content is null)
        {
            return new(opened.Status);
        }
        CameraAgentArtifactContentStream? content = opened.Content;
        if (content.Role is not (FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview) ||
            !IsPreviewMediaType(content.MediaType) || content.Descriptor is null)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new(CameraAgentArtifactReadStatus.UnsupportedMediaType);
        }
        var descriptor = content.Descriptor;
        var layout = descriptor.Layout;
        if (layout.Width > _options.MaximumPreviewDimension ||
            layout.Height > _options.MaximumPreviewDimension ||
            content.ByteLength > _options.MaximumPreviewSourceBytes ||
            content.ByteLength > int.MaxValue)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new(CameraAgentArtifactReadStatus.TooLarge);
        }
        if (!HasPackedLayout(layout))
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return new(CameraAgentArtifactReadStatus.UnsupportedMediaType);
        }

        var cacheKey = new PreviewCacheKey(
            content.ChecksumSha256,
            layout.Width,
            layout.Height,
            layout.PixelFormat);
        Task<CameraAgentArtifactPreviewResult>? flight;
        CameraAgentArtifactPreviewResult cached = default!;
        lock (_cacheGate)
        {
            if (TryGetCachedLocked(cacheKey, out cached))
            {
                flight = null;
            }
            else if (!_previewFlights.TryGetValue(cacheKey, out flight))
            {
                flight = EncodePreviewAsync(cacheKey, content, descriptor);
                _previewFlights.Add(cacheKey, flight);
                content = null;
            }
        }
        if (content is not null)
        {
            await content.DisposeAsync().ConfigureAwait(false);
        }
        return flight is null
            ? cached
            : await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Preview encoder failures are converted to a sanitized artifact status for every single-flight waiter.")]
    private async Task<CameraAgentArtifactPreviewResult> EncodePreviewAsync(
        PreviewCacheKey cacheKey,
        CameraAgentArtifactContentStream content,
        ReconstructionDescriptor descriptor)
    {
        await Task.Yield();
        await _previewGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using var contentLease = content.ConfigureAwait(false);
            var source = new byte[checked((int)content.ByteLength)];
            await content.ReadExactlyAsync(source, CancellationToken.None).ConfigureAwait(false);
            if (!FrameReconstructor.TryReconstruct(descriptor, source, out _, verifyChecksum: false).IsValid)
            {
                return new(CameraAgentArtifactReadStatus.Conflict);
            }
            byte[] encoded;
            try
            {
                encoded = _previewEncoder.Encode(descriptor.Layout, source);
            }
            catch (NotSupportedException)
            {
                return new(CameraAgentArtifactReadStatus.UnsupportedMediaType);
            }
            catch (Exception)
            {
                return new(CameraAgentArtifactReadStatus.Conflict);
            }
            if (encoded.Length > _options.MaximumPreviewEncodedBytes)
            {
                return new(CameraAgentArtifactReadStatus.TooLarge);
            }
            var result = new CameraAgentArtifactPreviewResult(
                CameraAgentArtifactReadStatus.Found,
                encoded,
                PayloadChecksum.ComputeSha256(encoded),
                descriptor.Layout.Width,
                descriptor.Layout.Height);
            AddCached(cacheKey, result);
            return result;
        }
        finally
        {
            lock (_cacheGate)
            {
                _previewFlights.Remove(cacheKey);
            }
            _previewGate.Release();
        }
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
                string.Equals(schema.GetString(), DurableProcessingProductManifestV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                var product = DurableProcessingProductManifestJson.Parse(sidecar);
                ValidateProcessingFacts(
                    row,
                    product.Capture,
                    product.Artifact,
                    product.RelativeArtifactPath,
                    product.ByteLength);
                return new ValidatedArtifact(
                    product.Artifact.ArtifactId,
                    product.Capture.CaptureId,
                    product.Artifact.Role,
                    product.Artifact.MediaType,
                    product.ByteLength,
                    product.Artifact.ChecksumSha256,
                    product.RelativeArtifactPath,
                    null);
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
            descriptor);

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
                       output.sidecar_relative_path, output.descriptor_json
                FROM processing_outputs AS output
                INNER JOIN processing_nodes AS node
                    ON node.capture_id = output.capture_id AND node.node_id = output.node_id
                WHERE output.artifact_id = $artifact_id;
                """;
            output.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            using var reader = await output.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
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
                string.Equals(schema.GetString(), DurableProcessingProductManifestV1.CurrentSchemaVersion, StringComparison.Ordinal))
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
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA query_only=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool HasPackedLayout(FrameLayoutDescriptor layout)
    {
        try
        {
            return layout.StrideBytes == checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)) &&
                   layout.ByteLength == checked((long)layout.StrideBytes * layout.Height);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsPreviewMediaType(string mediaType)
        => string.Equals(mediaType, "application/x-hvo-packed-image", StringComparison.OrdinalIgnoreCase) ||
           mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

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

    public void Dispose() => _previewGate.Dispose();

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
        ReconstructionDescriptor? Descriptor);

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
