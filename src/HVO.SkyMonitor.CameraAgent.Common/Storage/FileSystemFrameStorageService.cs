using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public sealed class FileSystemFrameStorageService(
    ILogger<FileSystemFrameStorageService> logger,
    Capture.CaptureMaintenanceGate? maintenance,
    IOptions<CameraAgentHostOptions>? hostOptions) : IFrameStorageService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    private readonly ILogger<FileSystemFrameStorageService> _logger = logger;

    public FileSystemFrameStorageService(
        ILogger<FileSystemFrameStorageService> logger, Capture.CaptureMaintenanceGate? maintenance = null)
        : this(logger, maintenance, null)
    {
    }

    internal Action? BrowseJournalDiscovered { get; init; }

    internal Action<string>? BeforeArchiveFileUnlink { get; set; }

    public async ValueTask<StoredFrameReference> SaveAsync(
        string storageRoot,
        FrameArtifact artifact,
        ReconstructionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return await SaveCoreAsync(storageRoot, artifact, descriptor, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<StoredFrameReference> SaveAsync(
        string storageRoot,
        FrameArtifact artifact,
        ReconstructionDescriptor descriptor,
        string producerStepId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerStepId);
        return await SaveCoreAsync(storageRoot, artifact, descriptor, producerStepId, null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StoredFrameReference> SaveCoreAsync(
        string storageRoot,
        FrameArtifact artifact,
        ReconstructionDescriptor descriptor,
        string? producerStepId,
        ArchivePublicationReceipt? archive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(artifact);
        producerStepId = producerStepId?.Trim();
        var frame = artifact.Frame;

        storageRoot = Path.GetFullPath(storageRoot);
        var timestamp = frame.TimestampUtc.ToUniversalTime();
        var payloadPath = GetPayloadPath(storageRoot, timestamp, artifact.ArtifactId, artifact.Role);
        var directory = Path.GetDirectoryName(payloadPath)!;
        var metadata = new StoredFrameMetadata(
            artifact.ArtifactId,
            artifact.Role,
            artifact.SourceArtifactIds,
            artifact.RecipeVersion,
            timestamp,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            new StoredFrameCaptureMetadata(
                frame.Metadata.Exposure,
                frame.Metadata.Gain,
                double.IsFinite(frame.Metadata.TemperatureC) ? frame.Metadata.TemperatureC : null,
                frame.Metadata.SourceId,
                frame.Metadata.Extra,
                frame.Metadata.Scene));

        var metadataPath = Path.ChangeExtension(payloadPath, ".json");
        var relativePayloadPath = Path.GetRelativePath(storageRoot, payloadPath);
        ValidateVersionedDescriptorAgreement(descriptor, artifact, frame);

        CameraAgentFileSystem.EnsureStorageRootDurable(storageRoot);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, directory);
        RawIngressFileStore.SyncDirectoryHierarchy(storageRoot, directory);
        var payloadExists = File.Exists(payloadPath);
        var sidecarExists = File.Exists(metadataPath);
        var upgradeProducerSidecar = await ValidateExistingVersionedEvidenceAsync(
            storageRoot,
            descriptor,
            payloadPath,
            metadataPath,
            relativePayloadPath,
            producerStepId,
            archive?.Producer,
            payloadExists,
            sidecarExists,
            cancellationToken).ConfigureAwait(false);
        if (archive is not null)
        {
            await StageArchiveAsync(storageRoot, payloadPath, archive, payloadExists && sidecarExists, cancellationToken)
                .ConfigureAwait(false);
            // An archived copy can retain an authentic older producerless sidecar. The receipt
            // records the producer without requiring a change to those immutable bytes.
            upgradeProducerSidecar = false;
        }
        else if ((!payloadExists || !sidecarExists || upgradeProducerSidecar) &&
            (File.Exists(ArchiveStagePath(payloadPath)) || File.Exists(ArchiveReceiptPath(payloadPath))))
        {
            if (!payloadExists || !sidecarExists)
                throw new InvalidDataException("Restoring a known centered archive requires its live Storage authority.");
            upgradeProducerSidecar = false;
        }
        if (payloadExists && sidecarExists)
        {
            RawIngressFileStore.SyncFile(storageRoot, payloadPath);
            RawIngressFileStore.SyncFile(storageRoot, metadataPath);
            RawIngressFileStore.SyncDirectory(directory);
        }

        try
        {
            if (!payloadExists)
            {
                await WriteAtomicallyAsync(storageRoot, payloadPath, frame.PixelData, cancellationToken).ConfigureAwait(false);
            }

            var sidecar = await CreateVersionedSidecarAsync(
                descriptor, payloadPath, relativePayloadPath, frame.Metadata.Scene, producerStepId, cancellationToken).ConfigureAwait(false);
            if (!sidecarExists)
            {
                await WriteAtomicallyAsync(storageRoot, metadataPath, sidecar, cancellationToken).ConfigureAwait(false);
            }
            else if (upgradeProducerSidecar)
            {
                // Rewriting a durable sidecar in place changes bytes a restored processing row may have recorded, so
                // it holds the capture maintenance barrier; while capture is not running the save fails retryably
                // and the node runs again once capture opens.
                using var lease = maintenance?.TryEnterIfInitialized();
                if (maintenance is not null && lease is null)
                    throw new Capture.CaptureMaintenanceHeldException("A producer sidecar upgrade");
                await ReplaceAtomicallyAsync(storageRoot, metadataPath, sidecar, cancellationToken).ConfigureAwait(false);
            }

            var indexDirectory = Path.Combine(storageRoot, "index");
            Directory.CreateDirectory(indexDirectory);
            RawIngressFileStore.SyncDirectoryHierarchy(storageRoot, indexDirectory);
            var indexPath = Path.Combine(indexDirectory, $"frames_{timestamp:yyyy-MM-dd}.jsonl");
            var indexGate = FrameIndexLock.ForRoot(storageRoot);
            await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureIndexEntryAsync(indexPath, metadata with { Metadata = null }, cancellationToken).ConfigureAwait(false);
                RawIngressFileStore.SyncDirectory(indexDirectory);
            }
            finally
            {
                indexGate.Release();
            }
        }
        catch
        {
            // Published immutable evidence is left for idempotent replay and reconciliation.
            throw;
        }

        _logger.FrameStored(payloadPath);
        return new StoredFrameReference(
            RelativePath: relativePayloadPath,
            AbsolutePath: payloadPath,
            TimestampUtc: timestamp,
            Role: artifact.Role);
    }

    private static void ValidateVersionedDescriptorAgreement(
        ReconstructionDescriptor descriptor,
        FrameArtifact artifact,
        CameraFrame frame)
    {
        var validation = descriptor.Validate();
        var sources = artifact.SourceArtifactIds ?? Array.Empty<Guid>();
        double? frameTemperature = double.IsFinite(frame.Metadata.TemperatureC) ? frame.Metadata.TemperatureC : null;
        if (!validation.IsValid ||
            descriptor.Artifact.ArtifactId != artifact.ArtifactId ||
            descriptor.Artifact.Role != artifact.Role ||
            !descriptor.Artifact.SourceArtifactIds.SequenceEqual(sources) ||
            descriptor.Layout.Width != frame.Width ||
            descriptor.Layout.Height != frame.Height ||
            descriptor.Layout.PixelFormat != frame.PixelFormat ||
            descriptor.Layout.StrideBytes != (frame.StrideBytes ?? GetPackedStride(frame.Width, frame.PixelFormat)) ||
            descriptor.Layout.ByteLength != frame.PixelData.Length ||
            descriptor.Timing.ExposureStartedUtc != frame.TimestampUtc.ToUniversalTime() ||
            descriptor.Controls.EffectiveExposure != frame.Metadata.Exposure ||
            descriptor.Controls.EffectiveGain != frame.Metadata.Gain ||
            descriptor.Controls.EffectiveOffset != frame.Metadata.Offset ||
            descriptor.Controls.EffectiveTemperatureC != frameTemperature ||
            !string.Equals(descriptor.Artifact.SourceId, frame.Metadata.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Reconstruction descriptor does not match the stored artifact ({validation.ReasonCode ?? "descriptor.mismatch"}).",
                nameof(descriptor));
        }
    }

    private static async ValueTask<byte[]> CreateVersionedSidecarAsync(
        ReconstructionDescriptor descriptor,
        string payloadPath,
        string relativePayloadPath,
        SceneProvenance? scene,
        string? producerStepId,
        CancellationToken cancellationToken)
    {
        using var payload = new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = await PayloadChecksum.ComputeSha256Async(payload, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(checksum, descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Stored payload checksum does not match the reconstruction descriptor.", nameof(descriptor));
        }

        return CaptureContractJson.Serialize(new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            relativePayloadPath,
            scene,
            producerStepId));
    }

    private static async ValueTask<bool> ValidateExistingVersionedEvidenceAsync(
        string storageRoot,
        ReconstructionDescriptor descriptor,
        string payloadPath,
        string sidecarPath,
        string relativePayloadPath,
        string? producerStepId,
        string? archiveProducer,
        bool payloadExists,
        bool sidecarExists,
        CancellationToken cancellationToken)
    {
        if (payloadExists)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, payloadPath);
            using var payload = new FileStream(
                payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var checksum = await PayloadChecksum.ComputeSha256Async(payload, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(checksum, descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Existing derivative payload conflicts with the requested output identity.");
            }
        }
        var upgradeProducerSidecar = false;
        if (sidecarExists)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, sidecarPath);
            var parsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
            var existing = parsed.Document?.Manifest;
            if (!parsed.IsValid || existing is null ||
                !string.Equals(existing.IdempotencyKey, CaptureContractJson.ComputeDescriptorSha256(descriptor), StringComparison.Ordinal) ||
                !string.Equals(existing.RelativeArtifactPath, relativePayloadPath, StringComparison.Ordinal) ||
                archiveProducer is not null && existing.ProducerStepId is { } retainedProducer && retainedProducer != archiveProducer)
            {
                throw new InvalidDataException("Existing derivative sidecar conflicts with the requested output identity.");
            }
            upgradeProducerSidecar = existing.ProducerStepId is null && producerStepId is not null;
        }
        return upgradeProducerSidecar;
    }

    private static int GetPackedStride(int width, CameraPixelFormat pixelFormat)
        => checked(width * (pixelFormat switch
        {
            CameraPixelFormat.Mono8 => 1,
            CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 => 2,
            CameraPixelFormat.Rgb24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        }));

    public async ValueTask RemoveAsync(
        string storageRoot,
        StoredFrameReference storedFrame,
        Guid artifactId,
        CancellationToken cancellationToken)
        => await RemoveBatchAsync(
            storageRoot,
            [new StoredFrameRemoval(storedFrame, artifactId)],
            cancellationToken).ConfigureAwait(false);

    public async ValueTask RemoveBatchAsync(
        string storageRoot,
        IReadOnlyCollection<StoredFrameRemoval> removals,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(removals);
        if (removals.Count == 0)
        {
            return;
        }

        storageRoot = Path.GetFullPath(storageRoot);
        var indexGate = FrameIndexLock.ForRoot(storageRoot);
        await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var removal in removals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(removal.StoredFrame);
                var payloadPath = removal.StoredFrame.AbsolutePath;
                RemoveArchiveFrameFile(storageRoot, payloadPath, BeforeArchiveFileUnlink);
                RemoveArchiveFrameFile(storageRoot, Path.ChangeExtension(payloadPath, ".json"), BeforeArchiveFileUnlink);
                ReclaimArchiveEvidenceIfAbsent(storageRoot, payloadPath);
            }
        }
        finally
        {
            indexGate.Release();
        }

        foreach (var dateGroup in removals.GroupBy(static removal => DateOnly.FromDateTime(removal.StoredFrame.TimestampUtc.UtcDateTime)))
        {
            var artifactIds = dateGroup.Select(static removal => removal.ArtifactId).ToHashSet();
            var indexPath = Path.Combine(storageRoot, "index", $"frames_{dateGroup.Key:yyyy-MM-dd}.jsonl");
            await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(indexPath))
                {
                    continue;
                }
                var remainingLines = (await File.ReadAllLinesAsync(indexPath, cancellationToken).ConfigureAwait(false))
                    .Where(line => !TryGetArtifactId(line, out var artifactId) || !artifactIds.Contains(artifactId))
                    .ToArray();
                if (remainingLines.Length == 0)
                {
                    RetainedFileMutations.Delete(indexPath);
                    continue;
                }

                var temporaryPath = string.Concat(indexPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
                try
                {
                    await File.WriteAllLinesAsync(temporaryPath, remainingLines, cancellationToken).ConfigureAwait(false);
                    CameraAgentFileSystem.PublishPrepared(storageRoot, temporaryPath, indexPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        RetainedFileMutations.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                FrameIndexIdentityCache.Invalidate(indexPath);
                indexGate.Release();
            }
        }
    }

    private static async Task WriteAtomicallyAsync(
        string storageRoot,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                CameraAgentFileSystem.FlushFile(stream);
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, Path.GetDirectoryName(destinationPath)!);
            try
            {
                CameraAgentFileSystem.PublishPrepared(storageRoot, temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException exception) when (!FileSystemFaultCompatibility.IsContainmentFailure(exception) && File.Exists(destinationPath))
            {
                var existing = await File.ReadAllBytesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(content.Span))
                {
                    throw new InvalidDataException("Existing frame evidence conflicts with the requested immutable content.");
                }
                return;
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                RetainedFileMutations.Delete(temporaryPath);
            }
        }
    }

    private static async Task ReplaceAtomicallyAsync(
        string storageRoot,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                CameraAgentFileSystem.FlushFile(stream);
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
            CameraAgentFileSystem.PublishPrepared(storageRoot, temporaryPath, destinationPath, overwrite: true);
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, destinationPath);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationPath)!);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                RetainedFileMutations.Delete(temporaryPath);
            }
        }
    }

    private static bool TryGetArtifactId(string line, out Guid artifactId)
    {
        artifactId = Guid.Empty;
        try
        {
            using var metadata = JsonDocument.Parse(line);
            return metadata.RootElement.TryGetProperty("artifactId", out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(value.GetString(), out artifactId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<StoredFrameReference> List(string storageRoot, DateOnly utcDate, FrameArtifactRole? role, int maximumResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        if (maximumResults is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        storageRoot = Path.GetFullPath(storageRoot);
        using var journal = OpenCenteredBrowseJournal(storageRoot);
        BrowseJournalDiscovered?.Invoke();
        using var visibility = journal is null ? null : CreateBrowseVisibility(journal);

        var references = new List<StoredFrameCandidate>();
        var seenArtifactIds = new HashSet<Guid>();
        var indexGate = FrameIndexLock.ForRoot(storageRoot);
        foreach (var indexPath in GetCandidateIndexPaths(storageRoot, utcDate))
        {
            string[] lines;
            indexGate.Wait();
            try
            {
                if (!File.Exists(indexPath))
                {
                    continue;
                }
                lines = File.ReadAllLines(indexPath);
            }
            catch (IOException)
            {
                _logger.FrameBrowseEntrySkipped(indexPath);
                continue;
            }
            finally
            {
                indexGate.Release();
            }

            foreach (var line in lines)
            {
                if (!TryReadMetadata(line, out var indexedMetadata))
                {
                    _logger.FrameBrowseEntrySkipped(indexPath);
                    continue;
                }
                if (!IsValid(indexedMetadata, utcDate, role) || seenArtifactIds.Contains(indexedMetadata.ArtifactId))
                {
                    continue;
                }

                var payloadPath = ResolvePayloadPath(storageRoot, indexedMetadata);
                var metadataPath = payloadPath is null ? null : Path.ChangeExtension(payloadPath, ".json");
                if (payloadPath is null || metadataPath is null || !File.Exists(metadataPath) ||
                    !TryReadMetadataFile(metadataPath, out var sidecarMetadata, out var versionedBinding) ||
                    !Matches(indexedMetadata, sidecarMetadata) ||
                    versionedBinding is not null && (!MatchesVersionedBinding(storageRoot, payloadPath, versionedBinding) ||
                        !IsPublishedForCompatibility(storageRoot, payloadPath, visibility, versionedBinding, indexedMetadata.ArtifactId)))
                {
                    _logger.FrameBrowseEntrySkipped(indexPath);
                    continue;
                }
                seenArtifactIds.Add(indexedMetadata.ArtifactId);

                references.Add(new StoredFrameCandidate(
                    new StoredFrameReference(
                        Path.GetRelativePath(storageRoot, payloadPath), payloadPath,
                        indexedMetadata.TimestampUtc, indexedMetadata.Role),
                    indexedMetadata.ArtifactId));
            }
        }

        return references
            .OrderByDescending(candidate => candidate.Reference.TimestampUtc)
            .ThenBy(candidate => candidate.ArtifactId)
            .Take(maximumResults)
            .Select(candidate => candidate.Reference)
            .ToArray();
    }

    private static bool TryReadMetadata(string value, out StoredFrameMetadata metadata)
    {
        try
        {
            metadata = JsonSerializer.Deserialize<StoredFrameMetadata>(value, SerializerOptions)!;
            return metadata is not null;
        }
        catch (JsonException)
        {
            metadata = null!;
            return false;
        }
    }

    private static bool TryReadMetadataFile(
        string path,
        out StoredFrameMetadata metadata,
        out VersionedSidecarBinding? versionedBinding)
    {
        versionedBinding = null;
        try
        {
            var json = File.ReadAllBytes(path);
            var parsed = CaptureContractJson.ParseManifest(json);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
            {
                metadata = null!;
                return false;
            }
            var descriptor = manifest.Descriptor;
            metadata = new StoredFrameMetadata(
                descriptor.Artifact.ArtifactId,
                descriptor.Artifact.Role,
                descriptor.Artifact.SourceArtifactIds,
                descriptor.Artifact.Recipe.ImplementationVersion,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Layout.Width,
                descriptor.Layout.Height,
                descriptor.Layout.PixelFormat,
                null);
            versionedBinding = new VersionedSidecarBinding(
                manifest.RelativeArtifactPath,
                descriptor.Layout.ByteLength,
                descriptor.Capture.CaptureId,
                manifest.ProducerStepId,
                descriptor.Artifact.Role == FrameArtifactRole.Raw,
                descriptor.Artifact.Role != FrameArtifactRole.Raw && descriptor.CenteredAcquisition is not null,
                manifest);
            return true;
        }
        catch (JsonException)
        {
            metadata = null!;
            return false;
        }
        catch (IOException)
        {
            metadata = null!;
            return false;
        }
    }

    private static SqliteConnection? OpenCenteredBrowseJournal(string root)
    {
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path))
            return null;

        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path + "-wal");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path + "-shm");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand CreateBrowseVisibility(SqliteConnection journal)
    {
        using var schema = journal.CreateCommand();
        schema.CommandText = """
            SELECT name FROM sqlite_schema WHERE type = 'table'
            AND name IN ('processing_outputs', 'processing_execution_outputs', 'processing_nodes');
            """;
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = schema.ExecuteReader())
            while (reader.Read())
                tables.Add(reader.GetString(0));

        var published = tables.Contains("processing_outputs") && tables.Contains("processing_execution_outputs") &&
            tables.Contains("processing_nodes")
            ? $$"""
               EXISTS (SELECT 1 FROM processing_outputs output
                       WHERE output.capture_id = $capture AND output.artifact_id = $artifact
                         AND output.payload_relative_path = $path AND output.availability_state = 'Available'
                         AND {{SqliteCaptureProcessingStore.OutputPublicationPredicate}})
               """
            : "0";
        var command = journal.CreateCommand();
        command.CommandText = $"SELECT {published};";
        command.Parameters.Add("$capture", SqliteType.Text);
        command.Parameters.Add("$artifact", SqliteType.Text);
        command.Parameters.Add("$path", SqliteType.Text);
        return command;
    }

    private bool IsPublishedForCompatibility(
        string storageRoot, string payloadPath, SqliteCommand? visibility, VersionedSidecarBinding binding,
        Guid artifactId)
    {
        if (binding.IsRaw)
            return true;
        var archived = ArchiveReceiptPath(payloadPath);
        var receiptExists = File.Exists(archived);
        if (receiptExists && MatchesArchiveReceipt(storageRoot, archived, binding, artifactId))
            return true;

        var needsProof = receiptExists || binding.RequiresSettlement || File.Exists(ArchiveStagePath(payloadPath));
        if (visibility is not null)
            return ReadBrowseVisibility(visibility, binding, artifactId);
        if (needsProof)
            return false;

        // A predecessor may lack the centered marker. Recheck after reading its indexed sidecar so a
        // journal initialized during discovery cannot lend its staged output the standalone fallback.
        using var lateJournal = OpenCenteredBrowseJournal(storageRoot);
        using var lateVisibility = lateJournal is null ? null : CreateBrowseVisibility(lateJournal);
        if (lateVisibility is not null)
            return ReadBrowseVisibility(lateVisibility, binding, artifactId);

        var indexGate = FrameIndexLock.ForRoot(storageRoot);
        indexGate.Wait();
        try
        {
            return !File.Exists(archived) && !File.Exists(ArchiveStagePath(payloadPath)) &&
                File.Exists(payloadPath) && File.Exists(Path.ChangeExtension(payloadPath, ".json")) &&
                !HasRetainedCenteredRaw(storageRoot, binding);
        }
        finally
        {
            indexGate.Release();
        }
    }

    private static bool HasRetainedCenteredRaw(string storageRoot, VersionedSidecarBinding binding)
    {
        var rawArtifact = RawCaptureDescriptorFactory.CreateRawArtifactId(binding.CaptureId);
        var paths = RawIngressFileStore.GetPaths(storageRoot,
            binding.Manifest.Descriptor.Timing.ExposureStartedUtc, rawArtifact);
        if (!File.Exists(paths.SidecarAbsolutePath))
            return false;

        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, paths.SidecarAbsolutePath);
        return TryReadMetadataFile(paths.SidecarAbsolutePath, out _, out var raw) &&
            raw is { IsRaw: true } && raw.CaptureId == binding.CaptureId &&
            raw.Manifest.Descriptor.Artifact.ArtifactId == rawArtifact && raw.Manifest.CenteredExposure is not null;
    }

    private static bool ReadBrowseVisibility(
        SqliteCommand visibility, VersionedSidecarBinding binding, Guid artifactId)
    {
        visibility.Parameters["$capture"].Value = binding.CaptureId.ToString("N");
        visibility.Parameters["$artifact"].Value = artifactId.ToString("N");
        visibility.Parameters["$path"].Value = binding.RelativeArtifactPath.Replace('\\', '/');
        return visibility.ExecuteScalar() is 1L;
    }

    internal const string ArchiveEvidenceVersion = "centered-archive-copy-v1";
    private const string ArchiveReceiptVersion = "centered-archive-receipt-v2";
    private const int ArchiveReceiptMaximumBytes = 4096;

    internal async ValueTask<(StoredFrameReference Stored, string Identity)> SaveArchiveAsync(
        string storageRoot, FrameArtifact frame, ReconstructionDescriptor descriptor, string? manifestProducer, RawCaptureReceipt raw,
        ProcessingExecutionContext execution, string producer, string outputIdentity, string? contentIdentity,
        CancellationToken cancellationToken)
    {
        var artifact = descriptor.Artifact;
        if (raw.Manifest.CenteredExposure is null || execution.ExecutionId == Guid.Empty ||
            execution.ExecutionClass != ProcessingGraphExecutionClass.Live || !execution.AllowAutomaticPublication ||
            raw.Manifest.Descriptor.Capture.CaptureId != descriptor.Capture.CaptureId ||
            raw.Manifest.CaptureGraphRevisionId != execution.GraphRevisionId ||
            artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(outputIdentity))
            throw new InvalidDataException("The archive copy is not bound to its centered execution output.");

        var root = StorageRootIdentity.CanonicalPath(storageRoot);
        var payloadPath = GetPayloadPath(root, frame.Frame.TimestampUtc.ToUniversalTime(), artifact.ArtifactId, artifact.Role);
        var relativePath = Path.GetRelativePath(root, payloadPath).Replace('\\', '/');
        var receipt = new ArchivePublicationReceipt(ArchiveRootDigest(root), relativePath,
            descriptor.Capture.CaptureId, execution.ExecutionId, string.Empty, producer, artifact.ArtifactId,
            outputIdentity, contentIdentity, CaptureContractJson.ComputeDescriptorSha256(descriptor), artifact.ChecksumSha256,
            ArchiveReceiptVersion);
        var stored = await SaveCoreAsync(root, frame, descriptor, manifestProducer, receipt, cancellationToken).ConfigureAwait(false);
        return (stored, ArchiveIdentity(receipt));
    }

    private static async ValueTask StageArchiveAsync(
        string storageRoot, string payloadPath, ArchivePublicationReceipt expected, bool complete,
        CancellationToken cancellationToken)
    {
        var stagePath = ArchiveStagePath(payloadPath);
        var finalPath = ArchiveReceiptPath(payloadPath);
        var indexGate = FrameIndexLock.ForRoot(storageRoot);
        await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var staged = ReadArchiveReceipt(storageRoot, stagePath);
            var final = ReadArchiveReceipt(storageRoot, finalPath);
            if (staged is not null && (!string.IsNullOrEmpty(staged.StorageNode) ||
                    !MatchesArchiveCopy(storageRoot, staged, expected)) ||
                final is not null && (string.IsNullOrWhiteSpace(final.StorageNode) ||
                    !MatchesArchiveCopy(storageRoot, final, expected)))
                throw new InvalidDataException("The retained archive evidence differs from the exact requested copy.");

            if (staged is null)
                await WriteAtomicallyAsync(storageRoot, stagePath, EncodeArchiveReceipt(expected), cancellationToken)
                    .ConfigureAwait(false);
            else if (staged.SchemaVersion is null)
                await ReplaceAtomicallyAsync(storageRoot, stagePath, EncodeArchiveReceipt(expected), cancellationToken)
                    .ConfigureAwait(false);

            if (!complete && final is not null)
            {
                RetainedFileMutations.Delete(finalPath);
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(finalPath)!);
            }
        }
        finally
        {
            indexGate.Release();
        }
    }

    private static bool MatchesArchiveCopy(string root, ArchivePublicationReceipt actual, ArchivePublicationReceipt expected)
        => MatchesArchiveRoot(root, actual) &&
            (actual with { Root = expected.Root, StorageNode = string.Empty, SchemaVersion = ArchiveReceiptVersion }) == expected;

    private static string ArchiveRootDigest(string root)
        => PayloadChecksum.ComputeSha256(Encoding.UTF8.GetBytes(StorageRootIdentity.Key(root)));

    private static bool MatchesArchiveRoot(string root, ArchivePublicationReceipt receipt)
        => receipt.SchemaVersion switch
        {
            ArchiveReceiptVersion => receipt.Root == ArchiveRootDigest(root),
            null => receipt.Root == StorageRootIdentity.Key(root),
            _ => false
        };

    private static byte[] EncodeArchiveReceipt(ArchivePublicationReceipt receipt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, SerializerOptions);
        if (bytes.Length > ArchiveReceiptMaximumBytes)
            throw new InvalidDataException("The archive receipt exceeds its length limit.");
        return bytes;
    }

    internal static async ValueTask PublishCommittedArchiveAsync(
        string storageRoot, string relativePath, string identity, Guid executionId, string nodeId,
        CancellationToken cancellationToken)
    {
        var payloadPath = Path.GetFullPath(Path.Combine(storageRoot, relativePath));
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, payloadPath);
        var stagePath = ArchiveStagePath(payloadPath);
        var receiptPath = ArchiveReceiptPath(payloadPath);
        var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
        if (!File.Exists(payloadPath) && !File.Exists(sidecarPath))
            return; // Retention removed the copy after its Storage node committed.
        var final = ReadArchiveReceipt(storageRoot, receiptPath);
        if (final is not null)
        {
            if (string.IsNullOrWhiteSpace(final.StorageNode) || !MatchesCommittedArchive(final))
                throw new InvalidDataException("The existing archive receipt differs from the committed copy.");
            // A second authorized Storage node keeps the first exact post-commit receipt.
            if (final.SchemaVersion == ArchiveReceiptVersion && File.Exists(payloadPath) && File.Exists(sidecarPath))
                return;
        }

        var staged = ReadArchiveReceipt(storageRoot, stagePath) ?? final;
        if (staged is null || !MatchesCommittedArchive(staged))
            throw new InvalidDataException("The staged archive differs from the committed copy.");

        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, sidecarPath);
        if (!TryReadMetadataFile(sidecarPath, out var metadata, out var binding) || binding is null ||
            !MatchesVersionedBinding(storageRoot, payloadPath, binding) ||
            !MatchesArchiveBinding(storageRoot, staged, binding, metadata.ArtifactId))
            throw new InvalidDataException("The staged archive files differ from the committed copy.");

        var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (payload.ConfigureAwait(false))
        {
            var checksum = await PayloadChecksum.ComputeSha256Async(payload, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(checksum, staged.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged archive payload checksum differs from the committed copy.");
        }

        var indexGate = FrameIndexLock.ForRoot(storageRoot);
        await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var receipt = staged with
            {
                Root = ArchiveRootDigest(storageRoot),
                SchemaVersion = ArchiveReceiptVersion,
                StorageNode = nodeId
            };
            if (final is null)
                await WriteAtomicallyAsync(storageRoot, receiptPath, EncodeArchiveReceipt(receipt), cancellationToken)
                    .ConfigureAwait(false);
            else
                await ReplaceAtomicallyAsync(storageRoot, receiptPath, EncodeArchiveReceipt(receipt), cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            indexGate.Release();
        }

        bool MatchesCommittedArchive(ArchivePublicationReceipt receipt)
            => receipt.ExecutionId == executionId && receipt.RelativePath == relativePath &&
               MatchesArchiveRoot(storageRoot, receipt) &&
               (ArchiveIdentity(receipt) == identity ||
                ArchiveIdentity(receipt with { Root = StorageRootIdentity.Key(storageRoot), SchemaVersion = null }) == identity ||
                ArchiveIdentity(receipt with { Root = ArchiveRootDigest(storageRoot), SchemaVersion = ArchiveReceiptVersion }) == identity);
    }

    private static string ArchiveIdentity(ArchivePublicationReceipt receipt)
        => CaptureContractJson.ComputeCanonicalJsonSha256(receipt with { StorageNode = string.Empty });

    private static ArchivePublicationReceipt? ReadArchiveReceipt(string storageRoot, string path)
    {
        if (!File.Exists(path))
            return null;
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, path);
        using var stream = File.OpenRead(path);
        if (stream.Length > ArchiveReceiptMaximumBytes)
            throw new InvalidDataException("The archive receipt exceeds its length limit.");
        return JsonSerializer.Deserialize<ArchivePublicationReceipt>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The archive receipt is missing its evidence.");
    }

    private bool MatchesArchiveReceipt(
        string storageRoot, string path, VersionedSidecarBinding binding, Guid artifactId)
    {
        try
        {
            var receipt = ReadArchiveReceipt(storageRoot, path);
            return receipt is not null && !string.IsNullOrWhiteSpace(receipt.StorageNode) &&
                MatchesArchiveBinding(storageRoot, receipt, binding, artifactId) &&
                (receipt.SchemaVersion == ArchiveReceiptVersion || HasCommittedPredecessorArchive(storageRoot, receipt));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    private bool HasCommittedPredecessorArchive(string storageRoot, ArchivePublicationReceipt receipt)
    {
        var sourceRoot = hostOptions?.Value.RawIngressRoot;
        if (receipt.SchemaVersion is not null || string.IsNullOrWhiteSpace(sourceRoot) ||
            StorageRootIdentity.AreEqual(sourceRoot, storageRoot))
            return false;
        using var source = OpenCenteredBrowseJournal(sourceRoot);
        if (source is null)
            return false;
        using var proof = source.CreateCommand();
        // The exact node commit already checked its durable lease and deadline inside the journal.
        // Completed timestamps are recorded by the worker and can come from a different clock.
        proof.CommandText = """
            SELECT execution.graph_revision_id, raw.manifest_json, raw.manifest_sha256,
                   execution.configuration_json, output.descriptor_json,
                   EXISTS (SELECT 1 FROM processing_node_inputs input
                           WHERE input.capture_id = node.capture_id AND input.node_id = node.node_id
                             AND input.kind = 'CanonicalContext' AND input.name = 'centered-archive'
                             AND input.schema_version = $schema AND input.variant = $path
                             AND input.identity_sha256 = $identity)
            FROM processing_executions execution
            JOIN raw_captures raw ON raw.capture_id = execution.capture_id
                                 AND raw.raw_artifact_id = execution.primary_artifact_id
            JOIN centered_exposure_attempts exposure ON exposure.capture_id = raw.capture_id
            JOIN processing_execution_nodes bound ON bound.execution_id = execution.execution_id AND bound.node_id = $storage
            JOIN processing_nodes node ON node.capture_id = raw.capture_id AND node.node_id = bound.node_id
                 AND node.plan_sha256 = bound.plan_sha256 AND node.attempt = bound.attempt_count
                 AND node.completed_unix_ms = bound.completed_unix_ms
            LEFT JOIN processing_outputs output ON output.output_identity_sha256 = $output
                 AND output.capture_id = raw.capture_id AND output.artifact_id = $artifact
                 AND output.content_identity_sha256 IS $content
                 AND EXISTS (SELECT 1 FROM processing_execution_outputs association
                             WHERE association.execution_id = execution.execution_id AND association.node_id = $producer
                               AND association.output_identity_sha256 = output.output_identity_sha256)
                 AND EXISTS (SELECT 1 FROM processing_node_inputs input
                             WHERE input.capture_id = node.capture_id AND input.node_id = node.node_id
                               AND input.kind = 'Artifact' AND input.artifact_id = output.artifact_id AND input.selected_flag = 1)
            WHERE execution.execution_id = $execution AND execution.capture_id = $capture
              AND execution.execution_class = 'Live' AND execution.trigger_kind = 'capture'
              AND execution.allow_automatic_publication = 1 AND bound.status = 'Completed' AND node.status = 'Completed';
            """;
        proof.Parameters.AddWithValue("$schema", ArchiveEvidenceVersion);
        proof.Parameters.AddWithValue("$path", receipt.RelativePath);
        proof.Parameters.AddWithValue("$identity", ArchiveIdentity(receipt));
        proof.Parameters.AddWithValue("$storage", receipt.StorageNode);
        proof.Parameters.AddWithValue("$output", receipt.OutputIdentity);
        proof.Parameters.AddWithValue("$artifact", receipt.ArtifactId.ToString("N"));
        proof.Parameters.AddWithValue("$content", (object?)receipt.ContentIdentity ?? DBNull.Value);
        proof.Parameters.AddWithValue("$producer", receipt.Producer);
        proof.Parameters.AddWithValue("$execution", receipt.ExecutionId.ToString("N"));
        proof.Parameters.AddWithValue("$capture", receipt.CaptureId.ToString("N"));
        using var reader = proof.ExecuteReader();
        if (!reader.Read())
            return false;
        var revision = reader.GetString(0);
        var raw = reader.GetFieldValue<byte[]>(1);
        var manifest = CaptureContractJson.ParseManifest(raw).Document?.Manifest;
        if (CaptureContractJson.ComputeManifestSha256(raw) != reader.GetString(2) ||
            manifest?.CenteredExposure is null || manifest.CaptureGraphRevisionId != revision ||
            manifest.Descriptor.Capture.CaptureId != receipt.CaptureId ||
            ProcessingGraphOperationsCoordinator.GetLiveExecutionId(receipt.CaptureId, revision) != receipt.ExecutionId)
            return false;
        if (reader.GetBoolean(5))
            return true; // The later predecessor already recorded exact destination evidence.
        if (reader.IsDBNull(4))
            return false;

        var output = CaptureContractJson.ParseManifest(reader.GetFieldValue<byte[]>(4)).Document?.Manifest?.Descriptor;
        if (output is null || output.Capture.CaptureId != receipt.CaptureId || output.Artifact.ArtifactId != receipt.ArtifactId ||
            output.Artifact.ChecksumSha256 != receipt.PayloadSha256 ||
            (CaptureContractJson.ComputeDescriptorSha256(output) != receipt.DescriptorSha256 &&
             CaptureContractJson.ComputeDescriptorSha256(output with { CenteredAcquisition = null }) != receipt.DescriptorSha256))
            return false;
        var configuration = JsonSerializer.Deserialize<CameraModuleConfig>(reader.GetFieldValue<byte[]>(3), SerializerOptions);
        return configuration?.Pipeline.Steps.Any(step =>
            RetentionBackgroundService.IsFileStorageStep(step.Type) &&
            (step.Id ?? FileStorageCaptureProcessingStep.StableAlias) == receipt.StorageNode &&
            step.Options?.Deserialize<FileStorageCaptureProcessingStepOptions>(SerializerOptions) is { StorageRoot.Length: > 0 } options &&
            StorageRootIdentity.AreEqual(options.StorageRoot, storageRoot)) == true;
    }

    private static bool MatchesArchiveBinding(
        string storageRoot, ArchivePublicationReceipt receipt, VersionedSidecarBinding binding, Guid artifactId)
        => receipt.ExecutionId != Guid.Empty && !string.IsNullOrWhiteSpace(receipt.Producer) &&
           (binding.Producer is null || binding.Producer == receipt.Producer) &&
           MatchesArchiveRoot(storageRoot, receipt) &&
           receipt.RelativePath == binding.RelativeArtifactPath.Replace('\\', '/') &&
           receipt.CaptureId == binding.CaptureId && receipt.ArtifactId == artifactId &&
           ProcessingIdentity.CreateArtifactId(receipt.OutputIdentity) == artifactId &&
           receipt.DescriptorSha256 == binding.Manifest.IdempotencyKey &&
           receipt.PayloadSha256 == binding.Manifest.Descriptor.Artifact.ChecksumSha256;

    internal static string ArchiveReceiptPath(string payloadPath) => Path.ChangeExtension(payloadPath, ".archive");

    internal const string ArchiveStageExtension = ".archive-stage";

    internal static string ArchiveStagePath(string payloadPath) => Path.ChangeExtension(payloadPath, ArchiveStageExtension);

    internal static bool IsArchiveEvidencePath(string path)
        => Path.GetExtension(path) is ".archive" or ArchiveStageExtension;

    internal static void RemoveArchiveFrameFile(string storageRoot, string path, Action<string>? beforeUnlink)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, path);
        if (!File.Exists(path))
            return;
        if (beforeUnlink is not null &&
            (File.Exists(ArchiveReceiptPath(path)) || File.Exists(ArchiveStagePath(path))))
            beforeUnlink(path);
        RetainedFileMutations.Delete(path);
    }

    internal static int ReclaimArchiveEvidenceIfAbsent(string storageRoot, string payloadPath)
    {
        var receiptPath = ArchiveReceiptPath(payloadPath);
        var stagePath = ArchiveStagePath(payloadPath);
        var hasReceipt = File.Exists(receiptPath);
        var hasStage = File.Exists(stagePath);
        var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
        if ((!hasReceipt && !hasStage) || File.Exists(payloadPath) || File.Exists(sidecarPath))
            return 0;
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, payloadPath);
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, sidecarPath);
        var directory = Path.GetDirectoryName(payloadPath)!;
        // Make the absences durable before an interrupted sweep can forget the copy's custody.
        RawIngressFileStore.SyncDirectory(directory);
        if (hasReceipt)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, receiptPath);
            RetainedFileMutations.Delete(receiptPath);
        }
        if (hasStage)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, stagePath);
            RetainedFileMutations.Delete(stagePath);
        }
        RawIngressFileStore.SyncDirectory(directory);
        return (hasReceipt ? 1 : 0) + (hasStage ? 1 : 0);
    }

    private static bool MatchesVersionedBinding(
        string storageRoot,
        string payloadPath,
        VersionedSidecarBinding binding)
    {
        try
        {
            var declaredPath = Path.GetFullPath(Path.Combine(storageRoot, binding.RelativeArtifactPath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(declaredPath, Path.GetFullPath(payloadPath), comparison) &&
                   new FileInfo(payloadPath).Length == binding.ByteLength;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValid(StoredFrameMetadata metadata, DateOnly utcDate, FrameArtifactRole? role)
        => metadata.ArtifactId != Guid.Empty &&
           Enum.IsDefined(metadata.Role) &&
           Enum.IsDefined(metadata.PixelFormat) &&
           metadata.Width > 0 && metadata.Height > 0 &&
           DateOnly.FromDateTime(metadata.TimestampUtc.UtcDateTime) == utcDate &&
           (role is null || metadata.Role == role);

    private static bool Matches(StoredFrameMetadata indexed, StoredFrameMetadata sidecar)
        => indexed.ArtifactId == sidecar.ArtifactId &&
           indexed.Role == sidecar.Role &&
           indexed.TimestampUtc == sidecar.TimestampUtc &&
           indexed.Width == sidecar.Width &&
           indexed.Height == sidecar.Height &&
           indexed.PixelFormat == sidecar.PixelFormat;

    private static async Task AppendIndexEntryAsync(
        string indexPath,
        StoredFrameMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.Seek(0, SeekOrigin.End);
                await stream.WriteAsync(NewLineBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        stream.Seek(0, SeekOrigin.End);
        var line = JsonSerializer.SerializeToUtf8Bytes(metadata, SerializerOptions);
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(NewLineBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        CameraAgentFileSystem.FlushFile(stream);
    }

    private static async Task EnsureIndexEntryAsync(
        string indexPath,
        StoredFrameMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            FrameIndexIdentityCache.Invalidate(indexPath);
        }
        var artifactIds = FrameIndexIdentityCache.GetOrCreate(indexPath, static path =>
            File.Exists(path)
                ? File.ReadLines(path)
                    .Select(line => TryGetArtifactId(line, out var artifactId) ? artifactId : Guid.Empty)
                    .Where(static artifactId => artifactId != Guid.Empty)
                    .ToHashSet()
                : []);
        if (artifactIds.Contains(metadata.ArtifactId))
        {
            RawIngressFileStore.SyncFile(Path.GetDirectoryName(Path.GetDirectoryName(indexPath)!)!, indexPath);
            return;
        }
        await AppendIndexEntryAsync(indexPath, metadata, cancellationToken).ConfigureAwait(false);
        artifactIds.Add(metadata.ArtifactId);
    }

    private static IEnumerable<string> GetCandidateIndexPaths(string storageRoot, DateOnly utcDate)
    {
        if (utcDate > DateOnly.MinValue)
        {
            yield return GetIndexPath(storageRoot, utcDate.AddDays(-1));
        }
        yield return GetIndexPath(storageRoot, utcDate);
        if (utcDate < DateOnly.MaxValue)
        {
            yield return GetIndexPath(storageRoot, utcDate.AddDays(1));
        }
    }

    private static string GetIndexPath(string storageRoot, DateOnly date)
        => Path.Combine(storageRoot, "index", $"frames_{date:yyyy-MM-dd}.jsonl");

    private static string? ResolvePayloadPath(string storageRoot, StoredFrameMetadata metadata)
    {
        var currentPath = GetPayloadPath(storageRoot, metadata);
        return File.Exists(currentPath) ? currentPath : null;
    }

    private static string GetPayloadPath(string storageRoot, StoredFrameMetadata metadata)
        => GetPayloadPath(storageRoot, metadata.TimestampUtc.ToUniversalTime(), metadata.ArtifactId, metadata.Role);

    internal static string GetPayloadPath(string storageRoot, DateTimeOffset timestamp, Guid artifactId, FrameArtifactRole role)
    {
        var stem = string.Concat(
            timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture),
            "-", artifactId.ToString("N"));
        return Path.Combine(
            storageRoot, "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture),
            role.ToString(), string.Concat(stem, ".bin"));
    }

    private sealed record StoredFrameMetadata(
        Guid ArtifactId,
        FrameArtifactRole Role,
        IReadOnlyList<Guid>? SourceArtifactIds,
        string? RecipeVersion,
        DateTimeOffset TimestampUtc,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        StoredFrameCaptureMetadata? Metadata);

    private sealed record StoredFrameCaptureMetadata(
        TimeSpan Exposure,
        double Gain,
        double? TemperatureC,
        string? SourceId,
        IReadOnlyDictionary<string, string>? Extra,
        SceneProvenance? Scene);

    private sealed record StoredFrameCandidate(StoredFrameReference Reference, Guid ArtifactId);

    private sealed record VersionedSidecarBinding(
        string RelativeArtifactPath, long ByteLength, Guid CaptureId, string? Producer, bool IsRaw,
        bool RequiresSettlement, ArtifactManifestV2 Manifest);

    private sealed record ArchivePublicationReceipt(
        string Root, string RelativePath, Guid CaptureId, Guid ExecutionId, string StorageNode, string Producer,
        Guid ArtifactId, string OutputIdentity, string? ContentIdentity, string DescriptorSha256, string PayloadSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SchemaVersion = null);

    public void Dispose() => GC.SuppressFinalize(this);

}

internal static class FrameIndexLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static SemaphoreSlim ForRoot(string storageRoot)
        => Gates.GetOrAdd(StorageRootIdentity.Key(storageRoot), static _ => new SemaphoreSlim(1, 1));
}

internal static class FrameIndexIdentityCache
{
    private const int MaximumCachedIndexes = 32;
    private static readonly ConcurrentDictionary<string, HashSet<Guid>> Entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static HashSet<Guid> GetOrCreate(string indexPath, Func<string, HashSet<Guid>> factory)
    {
        indexPath = Path.GetFullPath(indexPath);
        var result = Entries.GetOrAdd(indexPath, factory);
        if (Entries.Count > MaximumCachedIndexes)
        {
            foreach (var key in Entries.Keys.Where(key => !string.Equals(
                key,
                indexPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                if (Entries.Count <= MaximumCachedIndexes)
                {
                    break;
                }
                Entries.TryRemove(key, out _);
            }
        }
        return result;
    }

    internal static void Invalidate(string indexPath) => Entries.TryRemove(Path.GetFullPath(indexPath), out _);
}
