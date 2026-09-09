using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Background;

internal readonly record struct StorageRetentionPlan(
    string StorageRoot,
    int RetentionDays,
    IReadOnlyList<ArtifactStoragePolicyOptions>? Policies = null);

public sealed class RetentionBackgroundService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider,
    IArtifactOutbox artifactOutbox,
    IStorageCapacityProvider capacityProvider,
    StoragePressureState pressureState,
    ILogger<RetentionBackgroundService> logger,
    IRawIngressRetentionHolds? rawIngressHolds = null,
    IProcessingRetentionHolds? processingHolds = null,
    CaptureScheduleRuntimeCoordinator? scheduleCoordinator = null) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly CameraAgentHostOptions _hostOptions = hostOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IArtifactOutbox _artifactOutbox = artifactOutbox;
    private readonly IStorageCapacityProvider _capacityProvider = capacityProvider;
    private readonly StoragePressureState _pressureState = pressureState;
    private readonly ILogger<RetentionBackgroundService> _logger = logger;
    private readonly IRawIngressRetentionHolds? _rawIngressHolds = rawIngressHolds;
    private readonly IProcessingRetentionHolds? _processingHolds = processingHolds;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The dependency injection container owns the schedule coordinator.")]
    private readonly CaptureScheduleRuntimeCoordinator? _scheduleCoordinator = scheduleCoordinator;
    private readonly IRawIngressPressureReporter? _rawIngressPressureReporter = rawIngressHolds as IRawIngressPressureReporter;
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
                await ApplyRetentionAsync(
                    _scheduleCoordinator?.Snapshot?.Configuration ?? config,
                    stoppingToken).ConfigureAwait(false);
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

        Exception? firstFailure = null;
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawIngressGate = _rawIngressHolds is not null && PathsEqual(plan.StorageRoot, _hostOptions.RawIngressRoot)
                ? RawIngressLifecycleLock.ForRoot(plan.StorageRoot)
                : null;
            var processingHoldsUnderLifecycleLock = rawIngressGate is not null && _processingHolds is not null
                ? _processingHolds as IRawIngressLifecycleProcessingRetentionHolds
                    ?? throw new InvalidOperationException(
                        "Processing retention holds used under the raw-ingress lifecycle lock must provide a non-initializing read path.")
                : null;
            if (rawIngressGate is not null && _rawIngressHolds is RawCaptureIngress rawIngress)
            {
                // Initialization takes this same lifecycle lock so it must complete before retention owns the lock.
                await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            if (rawIngressGate is not null)
            {
                await rawIngressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var lifecycleGate = StorageLifecycleLock.ForRoot(plan.StorageRoot);
            var lifecycleGateAcquired = false;
            try
            {
                await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                lifecycleGateAcquired = true;
                StorageCapacity capacity;
                try
                {
                    capacity = _capacityProvider.GetCapacity(plan.StorageRoot);
                    if (capacity.TotalBytes <= 0 || capacity.AvailableBytes < 0 || capacity.AvailableBytes > capacity.TotalBytes)
                    {
                        throw new IOException($"Invalid storage capacity returned for '{plan.StorageRoot}'.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var prior = _pressureState.Get(plan.StorageRoot);
                    _pressureState.Set(new StoragePressureSnapshot(
                        plan.StorageRoot, default, prior?.IsUnderPressure ?? false,
                        prior?.EffectiveRetentionDays ?? plan.RetentionDays, _timeProvider.GetUtcNow(), ex.Message));
                    _logger.StorageCapacityProbeFailed(plan.StorageRoot, ex);
                    throw;
                }
                var previous = _pressureState.Get(plan.StorageRoot);
                var underPressure = previous?.IsUnderPressure == true
                    ? capacity.AvailablePercent < _hostOptions.DiskPressureRecoveryPercent
                    : capacity.AvailablePercent < _hostOptions.DiskPressureThresholdPercent;
                var effectiveRetentionDays = underPressure
                    ? Math.Min(plan.RetentionDays, _hostOptions.DiskPressureRetentionDays)
                    : plan.RetentionDays;
                var evaluatedUtc = _timeProvider.GetUtcNow();
                _pressureState.Set(new StoragePressureSnapshot(
                    plan.StorageRoot, capacity, underPressure, effectiveRetentionDays, evaluatedUtc));
                if (_rawIngressPressureReporter is not null && PathsEqual(plan.StorageRoot, _hostOptions.RawIngressRoot))
                {
                    _rawIngressPressureReporter.ReportPressure(underPressure);
                }
                if (underPressure && previous?.IsUnderPressure != true)
                {
                    _logger.DiskPressureEntered(plan.StorageRoot, capacity.AvailablePercent);
                }
                else if (!underPressure && previous?.IsUnderPressure == true)
                {
                    _logger.DiskPressureRecovered(plan.StorageRoot, capacity.AvailablePercent);
                }

                var cutoffDate = evaluatedUtc.UtcDateTime.Date.AddDays(-effectiveRetentionDays);
                // Frame day directories and index file names are written in UTC by every
                // producer, so the prunes that read those names compare whole days rather than
                // instants. DateOnly carries no time zone, which is what keeps the comparison in
                // the writers' frame on a host whose local zone is not UTC. See issue #823.
                var cutoffDay = DateOnly.FromDateTime(cutoffDate);
                var pending = await ReadPendingArtifactsAsync(
                    plan.StorageRoot, processingHoldsUnderLifecycleLock, cancellationToken).ConfigureAwait(false);
                pending = AddPolicyRetentionHolds(plan, evaluatedUtc, pending, cancellationToken);
                var deletedFiles = PruneFrameDirectories(plan.StorageRoot, cutoffDay, pending, cancellationToken);
                var indexGate = FrameIndexLock.ForRoot(plan.StorageRoot);
                await indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    deletedFiles += PruneIndexFiles(plan.StorageRoot, cutoffDay, pending.ArtifactIds, cancellationToken);
                }
                finally
                {
                    indexGate.Release();
                }
                deletedFiles += _processingHolds is IProcessingOutputExpiration expiration
                    ? await expiration.ExpireOutputsAsync(
                        plan.StorageRoot, new DateTimeOffset(cutoffDate, TimeSpan.Zero),
                        pending.AbsolutePaths, cancellationToken).ConfigureAwait(false)
                    : PruneDerivedOutputs(plan.StorageRoot, cutoffDate, pending.AbsolutePaths, cancellationToken);
                _logger.RetentionSweepCompleted(plan.StorageRoot, deletedFiles, pending.ArtifactIds.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= ex;
            }
            finally
            {
                if (lifecycleGateAcquired)
                {
                    lifecycleGate.Release();
                }
                rawIngressGate?.Release();
            }
        }
        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }

    private List<StorageRetentionPlan> BuildRetentionPlans(CameraModuleConfig config)
    {
        var configuredStorage = new List<StorageRetentionPlan>();
        var hasDurableOutputs = config.Pipeline.Steps.Any(static step =>
            step.Enabled != false && step.Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal);
        foreach (var step in config.Pipeline.Steps)
        {
            if (!IsFileStorageStep(step.Type) || step.Enabled == false)
            {
                continue;
            }

            try
            {
                var options = step.Options is { } configuredOptions
                    ? JsonSerializer.Deserialize<NoOpFileStorageProcessingStepOptions>(configuredOptions.GetRawText(), StepSerializerOptions)
                    : new NoOpFileStorageProcessingStepOptions();
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

                configuredStorage.Add(new StorageRetentionPlan(
                    normalizedRoot,
                    retentionDays,
                    options.Policies));
            }
            catch (JsonException)
            {
                // Ignore malformed storage step options and continue evaluating other steps.
            }
        }

        if (hasDurableOutputs && configuredStorage.Count == 0)
        {
            throw new InvalidOperationException(
                "Durable processing outputs require an enabled Storage step to define raw-root retention.");
        }

        var plans = configuredStorage
            .GroupBy(static plan => Path.TrimEndingDirectorySeparator(plan.StorageRoot), PathComparer)
            .Select(static group => new StorageRetentionPlan(
                group.Key,
                group.Max(static plan => plan.RetentionDays),
                MergePolicies(group.SelectMany(static plan => plan.Policies ?? []))))
            .OrderBy(static plan => plan.StorageRoot, PathComparer)
            .ToList();
        var needsRawRoot = hasDurableOutputs || _rawIngressHolds is not null || _processingHolds is not null;
        var rawRoot = needsRawRoot
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(_hostOptions.RawIngressRoot))
            : null;
        if (hasDurableOutputs && !plans.Any(plan => PathsEqual(plan.StorageRoot, rawRoot!)))
        {
            plans.Add(new StorageRetentionPlan(
                rawRoot!,
                configuredStorage.Max(static plan => plan.RetentionDays),
                MergePolicies(configuredStorage.SelectMany(static plan => plan.Policies ?? []))));
        }

        if (_rawIngressHolds is not null || _processingHolds is not null)
        {
            if (!plans.Any(plan => PathsEqual(plan.StorageRoot, rawRoot!)))
            {
                plans.Add(new StorageRetentionPlan(rawRoot!, 3650));
            }
        }
        return plans.OrderBy(static plan => plan.StorageRoot, PathComparer).ToList();
    }

    private static ArtifactStoragePolicyOptions[] MergePolicies(IEnumerable<ArtifactStoragePolicyOptions> policies)
        => policies
            .GroupBy(static policy => $"{policy.StepId?.ToUpperInvariant()}\0{policy.Role}\0{policy.Variant}\0{policy.RecipeName}", StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static policy => policy.RetentionDays ?? 0)
                .ThenByDescending(static policy => policy.QueueForUpload == true)
                .First())
            .OrderBy(static policy => policy.StepId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static policy => policy.Role)
            .ThenBy(static policy => policy.Variant, StringComparer.Ordinal)
            .ThenBy(static policy => policy.RecipeName, StringComparer.Ordinal)
            .ToArray();

    private static bool IsFileStorageStep(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        var implementationName = typeName.Split(',', 2)[0].Trim();
        return implementationName.Equals(NoOpFileStorageProcessingStep.StableAlias, StringComparison.OrdinalIgnoreCase)
            || implementationName.Equals(FileStorageStepName, StringComparison.OrdinalIgnoreCase)
            || (FileStorageStepFullName is not null && implementationName.Equals(FileStorageStepFullName, StringComparison.OrdinalIgnoreCase))
            || implementationName.EndsWith(FileStorageStepName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<PendingArtifacts> ReadPendingArtifactsAsync(
        string storageRoot,
        IRawIngressLifecycleProcessingRetentionHolds? processingHoldsUnderLifecycleLock,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        var rootPrefix = string.Concat(normalizedRoot, Path.DirectorySeparatorChar);
        var paths = new HashSet<string>(PathComparer);
        var artifactIds = new HashSet<Guid>();
        var outboxHolds = await _artifactOutbox.GetRetentionHoldsAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        foreach (var hold in outboxHolds)
        {
            AddHeldPath(normalizedRoot, rootPrefix, hold.RelativeArtifactPath, paths);
            AddHeldPath(normalizedRoot, rootPrefix, hold.RelativeSidecarPath, paths);
            artifactIds.Add(hold.ArtifactId);
        }
        if (_rawIngressHolds is not null)
        {
            var holds = _rawIngressHolds is RawCaptureIngress rawIngress
                ? await rawIngress.GetRetentionHoldsUnderLifecycleLockAsync(normalizedRoot, cancellationToken).ConfigureAwait(false)
                : await _rawIngressHolds.GetRetentionHoldsAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
            foreach (var hold in holds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddHeldPath(normalizedRoot, rootPrefix, hold.PayloadRelativePath, paths, allowMissing: true);
                AddHeldPath(normalizedRoot, rootPrefix, hold.SidecarRelativePath, paths, allowMissing: true);
                artifactIds.Add(hold.ArtifactId);
            }
        }
        if (_processingHolds is not null)
        {
            var holds = processingHoldsUnderLifecycleLock is null
                ? await _processingHolds.GetRetentionHoldsAsync(normalizedRoot, cancellationToken).ConfigureAwait(false)
                : await processingHoldsUnderLifecycleLock.GetRetentionHoldsUnderLifecycleLockAsync(
                    normalizedRoot, cancellationToken).ConfigureAwait(false);
            foreach (var hold in holds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddHeldPath(normalizedRoot, rootPrefix, hold.PayloadRelativePath, paths);
                AddHeldPath(normalizedRoot, rootPrefix, hold.SidecarRelativePath, paths);
                artifactIds.Add(hold.ArtifactId);
            }
        }
        return new PendingArtifacts(paths, artifactIds);
    }

    private static void AddHeldPath(
        string storageRoot,
        string rootPrefix,
        string relativePath,
        HashSet<string> paths,
        bool allowMissing = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Raw ingress hold contains an invalid relative path.");
        }
        var path = Path.GetFullPath(Path.Combine(storageRoot, relativePath));
        if (!path.StartsWith(rootPrefix, PathComparison) || !File.Exists(path))
        {
            if (allowMissing && path.StartsWith(rootPrefix, PathComparison)) return;
            throw new InvalidDataException("Raw ingress hold references missing or unsafe evidence.");
        }
        paths.Add(path);
    }

    private static PendingArtifacts AddPolicyRetentionHolds(
        StorageRetentionPlan plan,
        DateTimeOffset evaluatedUtc,
        PendingArtifacts pending,
        CancellationToken cancellationToken)
    {
        var policies = plan.Policies?.Where(static policy => policy.RetentionDays.HasValue).ToArray() ?? [];
        if (policies.Length == 0)
        {
            return pending;
        }
        var paths = pending.AbsolutePaths.ToHashSet(PathComparer);
        var artifactIds = pending.ArtifactIds.ToHashSet();

        var framesRoot = Path.Combine(plan.StorageRoot, "frames");
        if (Directory.Exists(framesRoot))
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(plan.StorageRoot, framesRoot);
            foreach (var sidecarPath in Directory.EnumerateFiles(framesRoot, "*.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RawIngressFileStore.EnsureNoSymbolicLinks(plan.StorageRoot, sidecarPath);
                var parsed = CaptureContractJson.ParseManifest(File.ReadAllBytes(sidecarPath));
                var manifest = parsed.Document?.Manifest;
                if (parsed.IsValid && manifest is not null)
                {
                    AddPolicyHold(
                        manifest.Descriptor.Artifact,
                        manifest.RelativeArtifactPath,
                        sidecarPath,
                        manifest.ProducerStepId);
                }
            }
        }

        var derivedRoot = Path.Combine(plan.StorageRoot, "derived");
        if (Directory.Exists(derivedRoot))
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(plan.StorageRoot, derivedRoot);
            foreach (var sidecarPath in Directory.EnumerateFiles(
                derivedRoot, "*.manifest.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RawIngressFileStore.EnsureNoSymbolicLinks(plan.StorageRoot, sidecarPath);
                try
                {
                    var manifest = DurableProcessingProductManifestJson.Parse(File.ReadAllBytes(sidecarPath));
                    AddPolicyHold(
                        manifest.Artifact,
                        manifest.RelativeArtifactPath,
                        sidecarPath,
                        manifest.ProducerStepId ?? manifest.Artifact.SourceId);
                }
                catch (InvalidDataException)
                {
                    // Unrecognized derived sidecars remain governed by the root retention policy.
                }
            }
        }
        return new PendingArtifacts(paths, artifactIds);

        void AddPolicyHold(
            ArtifactDescriptor artifact,
            string relativeArtifactPath,
            string sidecarPath,
            string? producerStepId)
        {
            var policy = policies
                .Where(policy => policy.StepId is null || string.Equals(
                    policy.StepId, producerStepId, StringComparison.OrdinalIgnoreCase))
                .Where(policy => policy.Role is null || policy.Role == artifact.Role)
                .Where(policy => policy.Variant is null || string.Equals(policy.Variant, artifact.Variant, StringComparison.Ordinal))
                .Where(policy => policy.RecipeName is null || string.Equals(
                    policy.RecipeName, artifact.Recipe.Name, StringComparison.Ordinal))
                .OrderByDescending(static policy =>
                    (policy.StepId is null ? 0 : 1) + (policy.Role is null ? 0 : 1) +
                    (policy.Variant is null ? 0 : 1) + (policy.RecipeName is null ? 0 : 1))
                .FirstOrDefault();
            if (policy?.RetentionDays is not { } retentionDays ||
                artifact.CreatedUtc < evaluatedUtc.AddDays(-retentionDays))
            {
                return;
            }
            var payloadPath = Path.GetFullPath(Path.Combine(plan.StorageRoot, relativeArtifactPath));
            RawIngressFileStore.EnsureNoSymbolicLinks(plan.StorageRoot, payloadPath);
            if (!File.Exists(payloadPath))
            {
                throw new InvalidDataException("Artifact retention policy references a missing payload.");
            }
            paths.Add(payloadPath);
            paths.Add(sidecarPath);
            artifactIds.Add(artifact.ArtifactId);
        }
    }

    private static int PruneFrameDirectories(
        string storageRoot,
        DateOnly cutoffDay,
        PendingArtifacts pending,
        CancellationToken cancellationToken)
    {
        var framesRoot = Path.Combine(storageRoot, "frames");
        if (!Directory.Exists(framesRoot))
        {
            return 0;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, framesRoot);

        var deletedFiles = 0;
        foreach (var yearDirectory in Directory.EnumerateDirectories(framesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, yearDirectory);
            foreach (var monthDirectory in Directory.EnumerateDirectories(yearDirectory))
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, monthDirectory);
                foreach (var dayDirectory in Directory.EnumerateDirectories(monthDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, dayDirectory);
                    var dateString = string.Join('-',
                        Path.GetFileName(yearDirectory),
                        Path.GetFileName(monthDirectory),
                        Path.GetFileName(dayDirectory));

                    // A name that does not parse leaves the day's frame unestablished, so the
                    // directory is kept. Retention that keeps too much is a bug report; retention
                    // that deletes too much is unrecoverable.
                    if (!DateOnly.TryParseExact(
                            dateString,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var directoryDay))
                    {
                        continue;
                    }

                    if (directoryDay <= cutoffDay)
                    {
                        foreach (var file in Directory.EnumerateFiles(dayDirectory, "*", SearchOption.AllDirectories))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, file);
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
        DateOnly cutoffDay,
        IReadOnlySet<Guid> pendingArtifactIds,
        CancellationToken cancellationToken)
    {
        var indexRoot = Path.Combine(storageRoot, "index");
        if (!Directory.Exists(indexRoot))
        {
            return 0;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, indexRoot);

        var deletedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(indexRoot, "frames_*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            var datePart = fileName.Replace("frames_", string.Empty, StringComparison.OrdinalIgnoreCase);
            // As above, an unparsable name is kept rather than guessed at.
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay)
                && fileDay <= cutoffDay)
            {
                var retainedLines = File.ReadLines(file)
                    .Where(line => TryGetArtifactId(line, out var artifactId) && pendingArtifactIds.Contains(artifactId))
                    .ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (retainedLines.Length == 0)
                {
                    File.Delete(file);
                    FrameIndexIdentityCache.Invalidate(file);
                    deletedFiles++;
                    _logger.IndexFileDeleted(file);
                }
                else
                {
                    File.WriteAllLines(file, retainedLines);
                    FrameIndexIdentityCache.Invalidate(file);
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
        RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, derivedRoot);

        var deletedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, file);
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
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, directory);
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
