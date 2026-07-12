using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Background;

internal readonly record struct StorageRetentionPlan(string StorageRoot, int RetentionDays);

public sealed class RetentionBackgroundService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    IArtifactOutbox artifactOutbox,
    ILogger<RetentionBackgroundService> logger) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly CameraAgentHostOptions _hostOptions = hostOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IArtifactOutbox _artifactOutbox = artifactOutbox;
    private readonly ILogger<RetentionBackgroundService> _logger = logger;
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Retention sweeps must continue even when deleting files fails.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await _configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
        var sweepInterval = TimeSpan.FromMinutes(_hostOptions.RetentionSweepIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyRetentionAsync(config, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.RetentionSweepFailed(ex);
            }

            await Task.Delay(sweepInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ApplyRetentionAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        var plans = BuildRetentionPlans(config);
        if (plans.Count == 0)
        {
            return;
        }

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cutoffDate = _timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(-plan.RetentionDays);
            var pending = ReadPendingArtifacts(plan.StorageRoot, cancellationToken);
            var deletedFiles = PruneFrameDirectories(plan.StorageRoot, cutoffDate, pending, cancellationToken);
            await FrameIndexLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                deletedFiles += PruneIndexFiles(plan.StorageRoot, cutoffDate, pending.ArtifactIds, cancellationToken);
            }
            finally
            {
                FrameIndexLock.Gate.Release();
            }
            deletedFiles += PruneDerivedOutputs(plan.StorageRoot, cutoffDate, pending.AbsolutePaths, cancellationToken);
            _logger.RetentionSweepCompleted(plan.StorageRoot, deletedFiles, pending.ArtifactIds.Count);
        }
    }

    private static List<StorageRetentionPlan> BuildRetentionPlans(CameraModuleConfig config)
    {
        var plans = new List<StorageRetentionPlan>();
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

                var storageRoot = options.StorageRoot?.Trim();
                if (string.IsNullOrEmpty(storageRoot))
                {
                    continue;
                }

                var normalizedRoot = Path.GetFullPath(storageRoot);
                var retentionDays = Math.Max(1, options.RetentionDays);

                if (plans.Any(p => string.Equals(p.StorageRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                plans.Add(new StorageRetentionPlan(normalizedRoot, retentionDays));
            }
            catch (JsonException)
            {
                // Ignore malformed storage step options and continue evaluating other steps.
            }
        }

        return plans;
    }

    private static bool IsFileStorageStep(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        var implementationName = typeName.Split(',', 2)[0].Trim();
        return implementationName.Equals(FileStorageStepName, StringComparison.OrdinalIgnoreCase)
            || (FileStorageStepFullName is not null && implementationName.Equals(FileStorageStepFullName, StringComparison.OrdinalIgnoreCase))
            || implementationName.EndsWith(FileStorageStepName, StringComparison.OrdinalIgnoreCase);
    }

    private PendingArtifacts ReadPendingArtifacts(string storageRoot, CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        var rootPrefix = string.Concat(normalizedRoot, Path.DirectorySeparatorChar);
        var paths = new HashSet<string>(PathComparer);
        var artifactIds = new HashSet<Guid>();
        foreach (var manifest in _artifactOutbox.EnumeratePending(normalizedRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(manifest.RelativeArtifactPath) || Path.IsPathRooted(manifest.RelativeArtifactPath))
            {
                throw new InvalidDataException("Pending outbox manifest contains an invalid relative path.");
            }

            var path = Path.GetFullPath(Path.Combine(normalizedRoot, manifest.RelativeArtifactPath));
            if (!path.StartsWith(rootPrefix, PathComparison) || !File.Exists(path))
            {
                throw new InvalidDataException(
                    $"Pending outbox payload '{manifest.RelativeArtifactPath}' is missing or outside its storage root.");
            }

            paths.Add(path);
            paths.Add(Path.ChangeExtension(path, ".json"));
            artifactIds.Add(manifest.ArtifactId);
        }
        return new PendingArtifacts(paths, artifactIds);
    }

    private static int PruneFrameDirectories(
        string storageRoot,
        DateTime cutoffDate,
        PendingArtifacts pending,
        CancellationToken cancellationToken)
    {
        var framesRoot = Path.Combine(storageRoot, "frames");
        if (!Directory.Exists(framesRoot))
        {
            return 0;
        }

        var deletedFiles = 0;
        foreach (var yearDirectory in Directory.EnumerateDirectories(framesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var monthDirectory in Directory.EnumerateDirectories(yearDirectory))
            {
                foreach (var dayDirectory in Directory.EnumerateDirectories(monthDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dateString = string.Join('-',
                        Path.GetFileName(yearDirectory),
                        Path.GetFileName(monthDirectory),
                        Path.GetFileName(dayDirectory));

                    if (!DateTime.TryParseExact(
                            dateString,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out var directoryDate))
                    {
                        continue;
                    }

                    if (directoryDate.Date <= cutoffDate)
                    {
                        foreach (var file in Directory.EnumerateFiles(dayDirectory, "*", SearchOption.AllDirectories))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!pending.AbsolutePaths.Contains(Path.GetFullPath(file)))
                            {
                                File.Delete(file);
                                deletedFiles++;
                            }
                        }
                        DeleteEmptyDirectories(dayDirectory);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(monthDirectory).Any())
                {
                    Directory.Delete(monthDirectory, recursive: false);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(yearDirectory).Any())
            {
                Directory.Delete(yearDirectory, recursive: false);
            }
        }
        return deletedFiles;
    }

    private int PruneIndexFiles(
        string storageRoot,
        DateTime cutoffDate,
        IReadOnlySet<Guid> pendingArtifactIds,
        CancellationToken cancellationToken)
    {
        var indexRoot = Path.Combine(storageRoot, "index");
        if (!Directory.Exists(indexRoot))
        {
            return 0;
        }

        var deletedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(indexRoot, "frames_*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileNameWithoutExtension(file);
            var datePart = fileName.Replace("frames_", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fileDate)
                && fileDate.Date <= cutoffDate)
            {
                var retainedLines = File.ReadLines(file)
                    .Where(line => TryGetArtifactId(line, out var artifactId) && pendingArtifactIds.Contains(artifactId))
                    .ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (retainedLines.Length == 0)
                {
                    File.Delete(file);
                    deletedFiles++;
                    _logger.IndexFileDeleted(file);
                }
                else
                {
                    File.WriteAllLines(file, retainedLines);
                }
            }
        }
        return deletedFiles;
    }

    private int PruneDerivedOutputs(
        string storageRoot,
        DateTime cutoffDate,
        IReadOnlySet<string> pendingPaths,
        CancellationToken cancellationToken)
    {
        var derivedRoot = Path.Combine(storageRoot, "derived");
        if (!Directory.Exists(derivedRoot))
        {
            return 0;
        }

        var deletedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWrite = File.GetLastWriteTimeUtc(file).Date;
            if (lastWrite <= cutoffDate && !pendingPaths.Contains(Path.GetFullPath(file)))
            {
                File.Delete(file);
                deletedFiles++;
                _logger.DerivedFileDeleted(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(derivedRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        return deletedFiles;
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static directory => directory.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    private static bool TryGetArtifactId(string line, out Guid artifactId)
    {
        artifactId = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("artifactId", out var value) &&
                value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out artifactId);
        }
        catch (JsonException)
        {
            artifactId = default;
            return false;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record PendingArtifacts(IReadOnlySet<string> AbsolutePaths, IReadOnlySet<Guid> ArtifactIds);
}
