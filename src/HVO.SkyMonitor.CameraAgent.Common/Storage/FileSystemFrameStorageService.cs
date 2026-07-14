using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public sealed class FileSystemFrameStorageService(
    ILogger<FileSystemFrameStorageService> logger) : IFrameStorageService, IDisposable
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

    public async ValueTask<StoredFrameReference> SaveAsync(
        string storageRoot,
        FrameArtifact artifact,
        CancellationToken cancellationToken)
        => await SaveCoreAsync(storageRoot, artifact, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<StoredFrameReference> SaveAsync(
        string storageRoot,
        FrameArtifact artifact,
        ReconstructionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return await SaveCoreAsync(storageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StoredFrameReference> SaveCoreAsync(
        string storageRoot,
        FrameArtifact artifact,
        ReconstructionDescriptor? descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;

        storageRoot = Path.GetFullPath(storageRoot);
        var timestamp = frame.TimestampUtc.ToUniversalTime();
        var directory = Path.Combine(
            storageRoot,
            "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture),
            artifact.Role.ToString());

        var stem = string.Concat(timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture), "-", artifact.ArtifactId.ToString("N"));

        var payloadPath = Path.Combine(directory, string.Concat(stem, ".bin"));
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

        var metadataPath = Path.Combine(directory, string.Concat(stem, ".json"));
        var relativePayloadPath = Path.GetRelativePath(storageRoot, payloadPath);
        if (descriptor is not null)
        {
            ValidateVersionedDescriptorAgreement(descriptor, artifact, frame);
        }

        Directory.CreateDirectory(directory);
        var payloadPublished = false;
        var sidecarPublished = false;
        try
        {
            await WriteAtomicallyAsync(payloadPath, frame.PixelData, cancellationToken).ConfigureAwait(false);
            payloadPublished = true;

            var sidecar = descriptor is null
                ? JsonSerializer.SerializeToUtf8Bytes(metadata, SerializerOptions)
                : await CreateVersionedSidecarAsync(
                    descriptor, payloadPath, relativePayloadPath, frame.Metadata.Scene, cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(metadataPath, sidecar, cancellationToken).ConfigureAwait(false);
            sidecarPublished = true;

            var indexDirectory = Path.Combine(storageRoot, "index");
            Directory.CreateDirectory(indexDirectory);
            var indexPath = Path.Combine(indexDirectory, $"frames_{timestamp:yyyy-MM-dd}.jsonl");
            var indexGate = FrameIndexLock.ForRoot(storageRoot);
            await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AppendIndexEntryAsync(indexPath, metadata with { Metadata = null }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                indexGate.Release();
            }
        }
        catch
        {
            if (descriptor is not null)
            {
                if (sidecarPublished)
                {
                    TryDelete(metadataPath);
                }
                if (payloadPublished)
                {
                    TryDelete(payloadPath);
                }
            }
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
            (artifact.RecipeVersion is null
                ? artifact.Role != FrameArtifactRole.Raw
                : !string.Equals(descriptor.Artifact.Recipe.ImplementationVersion, artifact.RecipeVersion, StringComparison.Ordinal)) ||
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
            scene));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

        foreach (var removal in removals)
        {
            ArgumentNullException.ThrowIfNull(removal.StoredFrame);
            var payloadPath = removal.StoredFrame.AbsolutePath;
            if (File.Exists(payloadPath))
            {
                File.Delete(payloadPath);
            }

            var metadataPath = Path.ChangeExtension(payloadPath, ".json");
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }

        storageRoot = Path.GetFullPath(storageRoot);
        foreach (var dateGroup in removals.GroupBy(static removal => DateOnly.FromDateTime(removal.StoredFrame.TimestampUtc.UtcDateTime)))
        {
            var artifactIds = dateGroup.Select(static removal => removal.ArtifactId).ToHashSet();
            var indexPath = Path.Combine(storageRoot, "index", $"frames_{dateGroup.Key:yyyy-MM-dd}.jsonl");
            var indexGate = FrameIndexLock.ForRoot(storageRoot);
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
                    File.Delete(indexPath);
                    continue;
                }

                var temporaryPath = string.Concat(indexPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
                try
                {
                    await File.WriteAllLinesAsync(temporaryPath, remainingLines, cancellationToken).ConfigureAwait(false);
                    File.Move(temporaryPath, indexPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                indexGate.Release();
            }
        }
    }

    private static async Task WriteAtomicallyAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
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
                    versionedBinding is not null && !MatchesVersionedBinding(storageRoot, payloadPath, versionedBinding))
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
            if (!TryReadSchemaVersion(json, out var hasSchemaVersion, out var schemaVersion))
            {
                metadata = null!;
                return false;
            }
            if (hasSchemaVersion)
            {
                if (!string.Equals(schemaVersion, ArtifactManifestV2.CurrentSchemaVersion, StringComparison.Ordinal))
                {
                    metadata = null!;
                    return false;
                }

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
                    descriptor.Layout.ByteLength);
                return true;
            }

            metadata = JsonSerializer.Deserialize<StoredFrameMetadata>(json, SerializerOptions)!;
            return metadata is not null;
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

    private static bool TryReadSchemaVersion(
        ReadOnlySpan<byte> json,
        out bool hasSchemaVersion,
        out string? schemaVersion)
    {
        hasSchemaVersion = false;
        schemaVersion = null;
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return true;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var isSchemaVersion = reader.ValueTextEquals("schemaVersion"u8);
            if (!reader.Read())
            {
                return false;
            }
            if (isSchemaVersion)
            {
                hasSchemaVersion = true;
                if (reader.TokenType != JsonTokenType.String)
                {
                    return false;
                }
                schemaVersion = reader.GetString();
                return true;
            }
            reader.Skip();
        }
        return false;
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
        var currentPath = GetPayloadPath(storageRoot, metadata, normalizeToUtc: true);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = GetPayloadPath(storageRoot, metadata, normalizeToUtc: false);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string GetPayloadPath(
        string storageRoot,
        StoredFrameMetadata metadata,
        bool normalizeToUtc)
    {
        var timestamp = normalizeToUtc ? metadata.TimestampUtc.ToUniversalTime() : metadata.TimestampUtc;
        var stem = string.Concat(
            timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture),
            "-", metadata.ArtifactId.ToString("N"));
        return Path.Combine(
            storageRoot, "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture),
            metadata.Role.ToString(), string.Concat(stem, ".bin"));
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

    private sealed record VersionedSidecarBinding(string RelativeArtifactPath, long ByteLength);

    public void Dispose() => GC.SuppressFinalize(this);

}

internal static class FrameIndexLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static SemaphoreSlim ForRoot(string storageRoot)
        => Gates.GetOrAdd(Path.GetFullPath(storageRoot), static _ => new SemaphoreSlim(1, 1));
}
