using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public sealed class FileSystemFrameStorageService(
    ILogger<FileSystemFrameStorageService> logger) : IFrameStorageService
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

    public async ValueTask<StoredFrameReference> SaveAsync(
        CameraModuleConfig config,
        FrameArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;

        var storageOptions = ResolveStorageOptions(config)
            ?? throw new InvalidOperationException("No file-system storage processing step is configured.");
        var storageRoot = Path.GetFullPath(storageOptions.StorageRoot);
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
            frame.Metadata);

        var metadataPath = Path.Combine(directory, string.Concat(stem, ".json"));
        await WriteAtomicallyAsync(metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata, SerializerOptions), cancellationToken).ConfigureAwait(false);

        var indexDirectory = Path.Combine(storageRoot, "index");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, $"frames_{timestamp:yyyy-MM-dd}.jsonl");
        var metadataLine = JsonSerializer.Serialize(metadata, SerializerOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(indexPath, metadataLine, cancellationToken).ConfigureAwait(false);

        _logger.FrameStored(payloadPath);
        return new StoredFrameReference(
            RelativePath: Path.GetRelativePath(storageRoot, payloadPath),
            AbsolutePath: payloadPath,
            TimestampUtc: timestamp,
            Role: artifact.Role);
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
        FrameMetadata Metadata);

    private static readonly JsonSerializerOptions StepSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    private static readonly string FileStorageStepName = typeof(NoOpFileStorageProcessingStep).Name;
    private static readonly string? FileStorageStepFullName = typeof(NoOpFileStorageProcessingStep).FullName;

    private static NoOpFileStorageProcessingStepOptions? ResolveStorageOptions(CameraModuleConfig config)
    {
        foreach (var step in config.ResolveProcessingSteps())
        {
            if (!IsFileStorageStep(step.Type) || step.Options is null)
            {
                continue;
            }

            try
            {
                var options = JsonSerializer.Deserialize<NoOpFileStorageProcessingStepOptions>(step.Options.Value.GetRawText(), StepSerializerOptions);
                if (options is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(options.StorageRoot))
                {
                    continue;
                }

                return options;
            }
            catch (JsonException)
            {
                // Ignore malformed options and continue scanning for a valid storage step
            }
        }

        return null;
    }

    private static bool IsFileStorageStep(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        return typeName.Equals(FileStorageStepName, StringComparison.OrdinalIgnoreCase)
            || (FileStorageStepFullName is not null && typeName.Equals(FileStorageStepFullName, StringComparison.OrdinalIgnoreCase))
            || typeName.EndsWith(FileStorageStepName, StringComparison.OrdinalIgnoreCase);
    }
}
