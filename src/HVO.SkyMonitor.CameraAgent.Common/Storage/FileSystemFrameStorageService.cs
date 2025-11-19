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
        CameraFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(frame);

        var storageOptions = ResolveStorageOptions(config)
            ?? throw new InvalidOperationException("No file-system storage processing step is configured.");
        var storageRoot = Path.GetFullPath(storageOptions.StorageRoot);
        var timestamp = frame.TimestampUtc;
        var directory = Path.Combine(
            storageRoot,
            "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);

        var stem = timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture);

        var payloadPath = Path.Combine(directory, string.Concat(stem, ".raw"));
        await File.WriteAllBytesAsync(payloadPath, frame.PixelData.ToArray(), cancellationToken).ConfigureAwait(false);

        var metadata = new StoredFrameMetadata(
            frame.TimestampUtc,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            frame.Metadata);

        var metadataPath = Path.Combine(directory, string.Concat(stem, ".json"));
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        var indexDirectory = Path.Combine(storageRoot, "index");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, $"frames_{timestamp:yyyy-MM-dd}.jsonl");
        var metadataLine = JsonSerializer.Serialize(metadata, SerializerOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(indexPath, metadataLine, cancellationToken).ConfigureAwait(false);

        _logger.FrameStored(payloadPath);
        return new StoredFrameReference(
            RelativePath: Path.GetRelativePath(storageRoot, payloadPath),
            AbsolutePath: payloadPath,
            TimestampUtc: timestamp);
    }

    private sealed record StoredFrameMetadata(
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
