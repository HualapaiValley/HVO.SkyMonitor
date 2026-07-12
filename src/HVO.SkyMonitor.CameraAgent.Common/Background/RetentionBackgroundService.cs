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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Background;

internal readonly record struct StorageRetentionPlan(string StorageRoot, int RetentionDays);

public sealed class RetentionBackgroundService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    ILogger<RetentionBackgroundService> logger) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly CameraAgentHostOptions _hostOptions = hostOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
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

    internal Task ApplyRetentionAsync(CameraModuleConfig config, CancellationToken cancellationToken)
    {
        var plans = BuildRetentionPlans(config);
        if (plans.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var plan in plans)
        {
            var cutoffDate = _timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(-plan.RetentionDays);
            PruneFrameDirectories(plan.StorageRoot, cutoffDate);
            PruneIndexFiles(plan.StorageRoot, cutoffDate);
            PruneDerivedOutputs(plan.StorageRoot, cutoffDate);
        }

        return Task.CompletedTask;
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

    private void PruneFrameDirectories(string storageRoot, DateTime cutoffDate)
    {
        var framesRoot = Path.Combine(storageRoot, "frames");
        if (!Directory.Exists(framesRoot))
        {
            return;
        }

        foreach (var yearDirectory in Directory.EnumerateDirectories(framesRoot))
        {
            foreach (var monthDirectory in Directory.EnumerateDirectories(yearDirectory))
            {
                foreach (var dayDirectory in Directory.EnumerateDirectories(monthDirectory))
                {
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
                        Directory.Delete(dayDirectory, recursive: true);
                        _logger.FrameDirectoryDeleted(dayDirectory);
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
    }

    private void PruneIndexFiles(string storageRoot, DateTime cutoffDate)
    {
        var indexRoot = Path.Combine(storageRoot, "index");
        if (!Directory.Exists(indexRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(indexRoot, "frames_*.jsonl"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var datePart = fileName.Replace("frames_", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fileDate)
                && fileDate.Date <= cutoffDate)
            {
                File.Delete(file);
                _logger.IndexFileDeleted(file);
            }
        }
    }

    private void PruneDerivedOutputs(string storageRoot, DateTime cutoffDate)
    {
        var derivedRoot = Path.Combine(storageRoot, "derived");
        if (!Directory.Exists(derivedRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories))
        {
            var lastWrite = File.GetLastWriteTimeUtc(file).Date;
            if (lastWrite <= cutoffDate)
            {
                File.Delete(file);
                _logger.DerivedFileDeleted(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(derivedRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }
}
