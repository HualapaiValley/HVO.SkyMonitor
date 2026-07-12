using System;
using System.Collections.Generic;
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

    private readonly ILogger<FileSystemFrameStorageService> _logger = logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public async ValueTask<StoredFrameReference> SaveAsync(
        string storageRoot,
        FrameArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;

        storageRoot = Path.GetFullPath(storageRoot);
        var timestamp = frame.TimestampUtc;
        var directory = Path.Combine(
            storageRoot,
            "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture),
            artifact.Role.ToString());

        Directory.CreateDirectory(directory);

        var stem = string.Concat(timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture), "-", artifact.ArtifactId.ToString("N"));

        var payloadPath = Path.Combine(directory, string.Concat(stem, ".bin"));
        await WriteAtomicallyAsync(payloadPath, frame.PixelData, cancellationToken).ConfigureAwait(false);

        var metadata = new StoredFrameMetadata(
            artifact.ArtifactId,
            artifact.Role,
            artifact.SourceArtifactIds,
            artifact.RecipeVersion,
            frame.TimestampUtc,
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
        await WriteAtomicallyAsync(metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata, SerializerOptions), cancellationToken).ConfigureAwait(false);

        var indexDirectory = Path.Combine(storageRoot, "index");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, $"frames_{timestamp:yyyy-MM-dd}.jsonl");
        var metadataLine = JsonSerializer.Serialize(metadata, SerializerOptions) + Environment.NewLine;
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(indexPath, metadataLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }

        _logger.FrameStored(payloadPath);
        return new StoredFrameReference(
            RelativePath: Path.GetRelativePath(storageRoot, payloadPath),
            AbsolutePath: payloadPath,
            TimestampUtc: timestamp,
            Role: artifact.Role);
    }

    public async ValueTask RemoveAsync(
        string storageRoot,
        StoredFrameReference storedFrame,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(storedFrame);

        var payloadPath = storedFrame.AbsolutePath;
        if (File.Exists(payloadPath))
        {
            File.Delete(payloadPath);
        }

        var metadataPath = Path.ChangeExtension(payloadPath, ".json");
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        storageRoot = Path.GetFullPath(storageRoot);
        var indexPath = Path.Combine(storageRoot, "index", $"frames_{storedFrame.TimestampUtc:yyyy-MM-dd}.jsonl");
        if (!File.Exists(indexPath))
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remainingLines = (await File.ReadAllLinesAsync(indexPath, cancellationToken).ConfigureAwait(false))
                .Where(line => !HasArtifactId(line, artifactId))
                .ToArray();
            if (remainingLines.Length == 0)
            {
                File.Delete(indexPath);
                return;
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
            _indexGate.Release();
        }
    }

    private static async Task WriteAtomicallyAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var temporaryPath = string.Concat(destinationPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken).ConfigureAwait(false);
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

    private static bool HasArtifactId(string line, Guid artifactId)
    {
        try
        {
            using var metadata = JsonDocument.Parse(line);
            return metadata.RootElement.TryGetProperty("artifactId", out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(value.GetString(), out var parsedArtifactId) &&
                   parsedArtifactId == artifactId;
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

        var dateDirectory = Path.Combine(Path.GetFullPath(storageRoot), "frames", utcDate.Year.ToString("D4", CultureInfo.InvariantCulture),
            utcDate.Month.ToString("D2", CultureInfo.InvariantCulture), utcDate.Day.ToString("D2", CultureInfo.InvariantCulture));
        if (!Directory.Exists(dateDirectory))
        {
            return Array.Empty<StoredFrameReference>();
        }

        var roleDirectories = role is { } selectedRole
            ? new[] { Path.Combine(dateDirectory, selectedRole.ToString()) }
            : Directory.EnumerateDirectories(dateDirectory);
        return roleDirectories.Where(Directory.Exists).SelectMany(directory => Directory.EnumerateFiles(directory, "*.bin")
                .Select(path => new StoredFrameReference(Path.GetRelativePath(storageRoot, path), path,
                    DateTimeOffset.FromFileTime(File.GetLastWriteTimeUtc(path).ToFileTimeUtc()),
                    Enum.Parse<FrameArtifactRole>(Path.GetFileName(Path.GetDirectoryName(path)!)))))
            .OrderByDescending(reference => reference.TimestampUtc).Take(maximumResults).ToArray();
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
        StoredFrameCaptureMetadata Metadata);

    private sealed record StoredFrameCaptureMetadata(
        TimeSpan Exposure,
        double Gain,
        double? TemperatureC,
        string? SourceId,
        IReadOnlyDictionary<string, string>? Extra,
        SceneProvenance? Scene);

    public void Dispose() => _indexGate.Dispose();

}
