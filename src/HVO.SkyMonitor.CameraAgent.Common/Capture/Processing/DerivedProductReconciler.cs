using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record DerivedProductReconciliationSummary(
    int Inspected, int Available, int Recoverable, int Cleaned,
    int Missing, int Quarantined, long QuarantineBytes);

internal sealed class DerivedProductReconciler(
    string storageRoot,
    SqliteCaptureProcessingStore store,
    DerivedProductLifecycleOptions? options = null,
    TimeProvider? timeProvider = null,
    Action? laneWakeup = null)
{
    private readonly string _root = Path.GetFullPath(storageRoot);
    private readonly string _derivedRoot = Path.Combine(Path.GetFullPath(storageRoot), "derived");
    private readonly DerivedProductLifecycleOptions _options = options ?? new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async ValueTask<DerivedProductReconciliationSummary> RunAsync(CancellationToken cancellationToken)
    {
        var gate = StorageLifecycleLock.ForRoot(_root);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var inspected = 0;
            var available = 0;
            var recoverable = 0;
            var cleaned = 0;
            var missing = 0;
            var quarantined = 0;
            long quarantineBytes = 0;

            string? reclaimedCursor = null;
            do
            {
                var orphanPage = await store.ReadOrphanLifecyclePageAsync(
                    reclaimedCursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
                _ = await store.DeleteReclaimedOrphanPageAsync(
                    reclaimedCursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
                reclaimedCursor = orphanPage.NextOperationId;
            }
            while (reclaimedCursor is not null);

            string? actionableCursor = null;
            do
            {
                var actionable = await store.ReadActionableLifecyclePageAsync(
                    actionableCursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
                foreach (var operation in actionable.Items)
                    await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                actionableCursor = actionable.NextOperationId;
            }
            while (actionableCursor is not null);

            Directory.CreateDirectory(_derivedRoot);
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, _derivedRoot);
            foreach (var temporary in EnumeratePage("*.tmp"))
            {
                File.Delete(temporary);
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(temporary)!);
                cleaned++;
            }

            var cursor = await store.ReadReconciliationCursorAsync(cancellationToken).ConfigureAwait(false);
            var page = await store.ReadProcessingEvidencePageAsync(
                cursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Items)
            {
                inspected++;
                if (row.AvailabilityState == "Quarantined")
                {
                    quarantined++;
                    continue;
                }
                string payload;
                string sidecar;
                try
                {
                    payload = ResolveCommitted(row, row.PayloadRelativePath);
                    sidecar = ResolveCommitted(row, row.SidecarRelativePath);
                }
                catch (InvalidDataException)
                {
                    var transition = await store.TransitionOutputUnavailableAsync(
                        row.OutputIdentitySha256, "Quarantined", "unsafe-committed-path", null,
                        cancellationToken).ConfigureAwait(false);
                    if (transition.Reactivated) laneWakeup?.Invoke();
                    quarantined++;
                    continue;
                }
                if (!File.Exists(payload) || !File.Exists(sidecar))
                {
                    var transition = await store.TransitionOutputUnavailableAsync(
                        row.OutputIdentitySha256, "Missing", "required-file-missing", null, cancellationToken).ConfigureAwait(false);
                    if (transition.Reactivated) laneWakeup?.Invoke();
                    missing++;
                    continue;
                }
                var reason = await ValidateCommittedAsync(row, payload, sidecar, cancellationToken).ConfigureAwait(false);
                if (reason is null)
                {
                    if (row.AvailabilityState == "Missing")
                        _ = await store.RestoreMissingOutputAvailableAsync(
                            row.OutputIdentitySha256, cancellationToken).ConfigureAwait(false);
                    else
                        await store.SetOutputAvailabilityAsync(row.OutputIdentitySha256, "Available", null, cancellationToken).ConfigureAwait(false);
                    available++;
                    continue;
                }
                var operation = await PlanQuarantineAsync(row.OutputIdentitySha256, payload, sidecar, reason, cancellationToken).ConfigureAwait(false);
                await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                quarantined++;
                quarantineBytes += operation.ObservedBytes;
            }
            await store.SetReconciliationCursorAsync(page.NextOutputIdentitySha256, cancellationToken).ConfigureAwait(false);

            var claimedPaths = new HashSet<string>(StringComparer.Ordinal);
            var modernCursor = await store.ReadFileCursorAsync("modern", cancellationToken).ConfigureAwait(false);
            var modernPage = EnumeratePage("*.manifest.json", modernCursor).ToArray();
            foreach (var sidecar in modernPage)
            {
                var sidecarRelative = Relative(sidecar);
                if (await store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false))
                {
                    _ = await store.DeleteClaimedOrphanOperationsAsync(
                        sidecarRelative, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                inspected++;
                var result = await InspectOrphanSidecarAsync(sidecar, cancellationToken).ConfigureAwait(false);
                if (result.Recoverable)
                {
                    recoverable++;
                    claimedPaths.Add(result.PayloadPath!);
                    claimedPaths.Add(sidecar);
                    continue;
                }
                if (result.PayloadPath is not null && string.Equals(result.Reason, "invalid-or-unsafe-sidecar", StringComparison.Ordinal))
                {
                    var bytes = await File.ReadAllBytesAsync(sidecar, cancellationToken).ConfigureAwait(false);
                    var identity = ProcessingIdentity.ComputePayloadSha256(bytes);
                    if (await InspectOrphanAgeAsync(
                            $"orphan:modern-malformed:{identity}", Relative(result.PayloadPath), sidecarRelative,
                            cancellationToken).ConfigureAwait(false))
                        continue;
                }
                var operation = await PlanQuarantineAsync(null, result.PayloadPath, sidecar, result.Reason!, cancellationToken).ConfigureAwait(false);
                await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                quarantined++;
                quarantineBytes += operation.ObservedBytes;
            }
            await store.SetFileCursorAsync("modern",
                modernPage.Length == _options.ReconciliationBatchSize ? Relative(modernPage[^1]) : null,
                cancellationToken).ConfigureAwait(false);

            var legacyCursor = await store.ReadFileCursorAsync("legacy", cancellationToken).ConfigureAwait(false);
            var legacyPage = EnumerateLegacyPage(legacyCursor).ToArray();
            foreach (var sidecar in legacyPage)
            {
                var sidecarRelative = Relative(sidecar);
                if (await store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false))
                {
                    _ = await store.DeleteClaimedOrphanOperationsAsync(
                        sidecarRelative, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(sidecar, cancellationToken).ConfigureAwait(false);
                var parsed = CaptureContractJson.ParseManifest(bytes);
                if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
                {
                    var sibling = FindSingleLegacyPayload(sidecar);
                    if (sibling is null) continue;
                    var identity = ProcessingIdentity.ComputePayloadSha256(bytes);
                    if (await InspectOrphanAgeAsync(
                            $"orphan:legacy-malformed:{identity}", Relative(sibling), sidecarRelative,
                            cancellationToken).ConfigureAwait(false))
                    {
                        claimedPaths.Add(sibling);
                        claimedPaths.Add(sidecar);
                        continue;
                    }
                    var malformed = await PlanQuarantineAsync(null, sibling, sidecar,
                        "legacy-orphan-invalid-sidecar", cancellationToken).ConfigureAwait(false);
                    await ResumeOperationAsync(malformed, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    quarantineBytes += malformed.ObservedBytes;
                    continue;
                }
                inspected++;
                if (!SqliteCaptureProcessingStore.IsCanonicalRelativePath(manifest.RelativeArtifactPath) ||
                    !manifest.RelativeArtifactPath.StartsWith("derived/", StringComparison.Ordinal) ||
                    !string.Equals(sidecarRelative, Path.ChangeExtension(manifest.RelativeArtifactPath, ".json"), StringComparison.Ordinal))
                {
                    var operation = await PlanQuarantineAsync(null, null, sidecar, "legacy-orphan-path-mismatch", cancellationToken).ConfigureAwait(false);
                    await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    continue;
                }
                var payloadPath = ResolveDerived(manifest.RelativeArtifactPath);
                if (!File.Exists(payloadPath))
                {
                    var operation = await PlanQuarantineAsync(null, null, sidecar, "legacy-sidecar-without-payload", cancellationToken).ConfigureAwait(false);
                    await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    continue;
                }
                var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
                if (payload.LongLength != manifest.Descriptor.Layout.ByteLength ||
                    !string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), manifest.Descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var operation = await PlanQuarantineAsync(null, payloadPath, sidecar, "legacy-orphan-payload-mismatch", cancellationToken).ConfigureAwait(false);
                    await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    continue;
                }
                var legacyIdentity = CaptureContractJson.ComputeManifestSha256(bytes);
                var aging = await InspectOrphanAgeAsync(
                    $"orphan:legacy:{legacyIdentity}", manifest.RelativeArtifactPath, sidecarRelative,
                    cancellationToken).ConfigureAwait(false);
                if (!aging)
                {
                    var operation = await PlanQuarantineAsync(null, payloadPath, sidecar,
                        "legacy-orphan-recovery-window-expired", cancellationToken).ConfigureAwait(false);
                    await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    continue;
                }
                recoverable++;
                claimedPaths.Add(payloadPath);
                claimedPaths.Add(sidecar);
            }
            await store.SetFileCursorAsync("legacy",
                legacyPage.Length == _options.ReconciliationBatchSize ? Relative(legacyPage[^1]) : null,
                cancellationToken).ConfigureAwait(false);

            var payloadCursor = await store.ReadFileCursorAsync("payload", cancellationToken).ConfigureAwait(false);
            var payloadPage = EnumeratePage("*", payloadCursor).ToArray();
            foreach (var payload in payloadPage)
            {
                var relative = Relative(payload);
                if (payload.EndsWith(".manifest.json", StringComparison.Ordinal) || payload.EndsWith(".tmp", StringComparison.Ordinal) ||
                    claimedPaths.Contains(payload) || await store.IsProcessingPathClaimedAsync(relative, cancellationToken).ConfigureAwait(false)) continue;
                if (HasPotentialCompanion(payload)) continue;
                inspected++;
                var operation = await PlanQuarantineAsync(null, payload, null, "payload-without-sidecar", cancellationToken).ConfigureAwait(false);
                await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                quarantined++;
                quarantineBytes += operation.ObservedBytes;
            }
            await store.SetFileCursorAsync("payload",
                payloadPage.Length == _options.ReconciliationBatchSize ? Relative(payloadPage[^1]) : null,
                cancellationToken).ConfigureAwait(false);
            return new(inspected, available, recoverable, cleaned, missing, quarantined, quarantineBytes);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<(bool Recoverable, string? PayloadPath, string? Reason)> InspectOrphanSidecarAsync(
        string sidecarPath, CancellationToken cancellationToken)
    {
        IDurableProcessingProductManifest manifest;
        try
        {
            manifest = DurableProcessingProductManifestJson.Parse(
                await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return (false, FindSingleModernPayload(sidecarPath), "invalid-or-unsafe-sidecar");
        }

        // Never resolve a manifest-named path before proving it names this sidecar's canonical sibling.
        var sidecarRelative = Relative(sidecarPath);
        if (!SqliteCaptureProcessingStore.IsCanonicalRelativePath(manifest.RelativeArtifactPath) ||
            !manifest.RelativeArtifactPath.StartsWith("derived/", StringComparison.Ordinal) ||
            !string.Equals(sidecarRelative, Path.ChangeExtension(manifest.RelativeArtifactPath, ".manifest.json"), StringComparison.Ordinal))
            return (false, null, "orphan-path-mismatch");
        var payloadPath = ResolveDerived(manifest.RelativeArtifactPath);
        if (!PathsEqual(Path.ChangeExtension(sidecarPath, Path.GetExtension(payloadPath)), payloadPath))
            return (false, null, "orphan-path-mismatch");
        if (!File.Exists(payloadPath)) return (false, null, "sidecar-without-payload");
        var reason = await ValidatePayloadAsync(manifest, payloadPath, cancellationToken).ConfigureAwait(false);
        if (reason is not null) return (false, null, reason);

        var operationId = $"orphan:{manifest.OutputIdentitySha256}";
        if (await InspectOrphanAgeAsync(
                operationId, manifest.RelativeArtifactPath, sidecarRelative, cancellationToken).ConfigureAwait(false))
            return (true, payloadPath, null);
        return (false, payloadPath, "orphan-recovery-window-expired");
    }

    private async ValueTask<bool> InspectOrphanAgeAsync(
        string operationId, string payloadRelativePath, string sidecarRelativePath,
        CancellationToken cancellationToken)
    {
        ProcessingLifecycleOperation? existing = null;
        string? orphanCursor = null;
        do
        {
            var orphanPage = await store.ReadOrphanLifecyclePageAsync(
                orphanCursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
            existing = orphanPage.Items.SingleOrDefault(item => item.OperationId == operationId);
            orphanCursor = existing is null ? orphanPage.NextOperationId : null;
        }
        while (orphanCursor is not null);
        if (existing is null)
        {
            await store.PlanLifecycleOperationAsync(new(
                operationId, "orphan", null, payloadRelativePath, sidecarRelativePath,
                string.Empty, "awaiting-deterministic-retry", 0), cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - existing.PlannedUnixMilliseconds <
            TimeSpan.FromMinutes(_options.OrphanRecoveryWindowMinutes).TotalMilliseconds)
            return true;
        await store.DeleteLifecycleOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<string?> ValidateCommittedAsync(
        DurableProcessingEvidence row, string payloadPath, string sidecarPath, CancellationToken cancellationToken)
    {
        try
        {
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            if (!sidecar.AsSpan().SequenceEqual(row.EvidenceJson)) return "sidecar-commit-conflict";
            using var document = System.Text.Json.JsonDocument.Parse(sidecar);
            var schema = document.RootElement.GetProperty("schemaVersion").GetString();
            if (schema is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                DurableEncodedProductManifestV2.CurrentSchemaVersion or DurableTypedMetadataProductManifestV3.CurrentSchemaVersion)
            {
                var manifest = DurableProcessingProductManifestJson.Parse(sidecar);
                if (!string.Equals(row.PayloadRelativePath, manifest.RelativeArtifactPath, StringComparison.Ordinal) ||
                    !string.Equals(row.SidecarRelativePath, Path.ChangeExtension(manifest.RelativeArtifactPath, ".manifest.json"), StringComparison.Ordinal) ||
                    row.ArtifactId != manifest.Artifact.ArtifactId || row.CaptureId != manifest.Capture.CaptureId ||
                    !string.Equals(row.OutputIdentitySha256, manifest.OutputIdentitySha256, StringComparison.Ordinal))
                    return "committed-identity-conflict";
                return await ValidatePayloadAsync(manifest, payloadPath, cancellationToken).ConfigureAwait(false);
            }
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } legacy ||
                !string.Equals(row.PayloadRelativePath, legacy.RelativeArtifactPath, StringComparison.Ordinal) ||
                !string.Equals(row.SidecarRelativePath, Path.ChangeExtension(row.PayloadRelativePath, ".json"), StringComparison.Ordinal) ||
                !PathsEqual(sidecarPath, ResolveCommitted(row, row.SidecarRelativePath)) ||
                row.ArtifactId != legacy.Descriptor.Artifact.ArtifactId || row.CaptureId != legacy.Descriptor.Capture.CaptureId)
                return "committed-identity-conflict";
            var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            return payload.LongLength == legacy.Descriptor.Layout.ByteLength &&
                string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), legacy.Descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
                ? null : "payload-checksum-mismatch";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return "invalid-product-manifest";
        }
    }

    private async ValueTask<string?> ValidatePayloadAsync(
        IDurableProcessingProductManifest manifest, string payloadPath, CancellationToken cancellationToken)
    {
        if (new FileInfo(payloadPath).Length != manifest.ByteLength) return "payload-length-mismatch";
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), manifest.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            return "payload-checksum-mismatch";
        if (manifest is DurableTypedMetadataProductManifestV3 typed &&
            string.Equals(typed.ProductSchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            var parsed = ProjectedSceneJson.Parse(payload);
            if (parsed.Scene is not { } scene) return "projected-scene-invalid";
            if (!string.Equals(scene.SceneIdentitySha256, typed.ContentIdentitySha256, StringComparison.Ordinal) ||
                scene.Source.CaptureId != manifest.Capture.CaptureId ||
                !manifest.Artifact.SourceArtifactIds.Contains(scene.Source.ArtifactId) ||
                !await store.MatchesRawSourceAsync(scene.Source.CaptureId, scene.Source.ArtifactId,
                    scene.Source.ArtifactIdentitySha256, cancellationToken).ConfigureAwait(false))
                return "projected-scene-source-conflict";
        }
        return null;
    }

    private async ValueTask<ProcessingLifecycleOperation> PlanQuarantineAsync(
        string? outputIdentity, string? payloadPath, string? sidecarPath, string reason, CancellationToken cancellationToken)
    {
        var sources = new[] { payloadPath, sidecarPath }.Where(static path => path is not null && File.Exists(path)).Cast<string>().ToArray();
        if (sources.Length == 0) throw new InvalidDataException("Processing quarantine has no physical evidence.");
        foreach (var source in sources) RawIngressFileStore.EnsureNoSymbolicLinks(_root, source);
        var operation = new ProcessingLifecycleOperation(
            $"quarantine:{Guid.NewGuid():N}", "quarantine", outputIdentity, Relative(sources[0]),
            sources.Length > 1 ? Relative(sources[1]) : null,
            $"processing-quarantine/{_timeProvider.GetUtcNow():yyyyMMdd}/{Guid.NewGuid():N}", reason,
            sources.Sum(static path => new FileInfo(path).Length), _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await store.PlanLifecycleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        return operation;
    }

    internal async ValueTask ResumeOperationAsync(ProcessingLifecycleOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Kind == "delete" && operation.Phase == "files-deleted")
        {
            await store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var destinationRoot = ResolveRoot(operation.DestinationRelativePath);
        if (operation.Kind == "delete" && operation.Phase == "database-completed")
        {
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationRoot)!);
            await store.SetLifecyclePhaseAsync(operation.OperationId, "files-deleted", cancellationToken).ConfigureAwait(false);
            await store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(destinationRoot);
        RawIngressFileStore.SyncDirectoryHierarchy(_root, destinationRoot);
        if (operation.Phase == "planned")
        {
            foreach (var relative in new[] { operation.SourceRelativePath, operation.CompanionRelativePath }.Where(static path => path is not null).Cast<string>())
            {
                var source = ResolveRoot(relative);
                var destination = Path.Combine(destinationRoot, Path.GetFileName(source));
                if (File.Exists(source) && File.Exists(destination))
                {
                    if (!await FilesEqualAsync(source, destination, cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("Processing lifecycle source and destination conflict.");
                    File.Delete(source);
                }
                else if (File.Exists(source)) File.Move(source, destination, overwrite: false);
                else if (!File.Exists(destination)) throw new InvalidDataException("Processing lifecycle evidence is missing.");
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(source)!);
            }
            RawIngressFileStore.SyncDirectory(destinationRoot);
            await store.SetLifecyclePhaseAsync(operation.OperationId, "moved", cancellationToken).ConfigureAwait(false);
            operation = operation with { Phase = "moved" };
        }
        if (operation.Phase == "moved")
        {
            if (operation.Kind == "quarantine" && operation.OutputIdentitySha256 is { } outputIdentity)
            {
                var transition = await store.TransitionOutputUnavailableAsync(
                    outputIdentity, "Quarantined", operation.Reason, operation.DestinationRelativePath,
                    cancellationToken).ConfigureAwait(false);
                await store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                if (transition.Reactivated) laneWakeup?.Invoke();
            }
            else
            {
                await store.CompleteLifecycleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            if (operation.Kind == "delete")
            {
                if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationRoot)!);
                await store.SetLifecyclePhaseAsync(operation.OperationId, "files-deleted", cancellationToken).ConfigureAwait(false);
                await store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<string> EnumeratePage(string pattern, string? afterRelativePath = null) => Directory.EnumerateFiles(_derivedRoot, pattern,
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
        .Order(StringComparer.Ordinal)
        .Where(path => afterRelativePath is null || string.CompareOrdinal(Relative(path), afterRelativePath) > 0)
        .Take(_options.ReconciliationBatchSize);

    private IEnumerable<string> EnumerateLegacyPage(string? afterRelativePath) => Directory.EnumerateFiles(
            _derivedRoot, "*.json", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
        .Where(static path => !path.EndsWith(".manifest.json", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .Where(path => afterRelativePath is null || string.CompareOrdinal(Relative(path), afterRelativePath) > 0)
        .Take(_options.ReconciliationBatchSize);

    private static bool HasPotentialCompanion(string payloadPath)
    {
        var modern = Path.ChangeExtension(payloadPath, ".manifest.json");
        if (!PathsEqual(modern, payloadPath) && File.Exists(modern)) return true;
        var legacy = Path.ChangeExtension(payloadPath, ".json");
        return !PathsEqual(legacy, payloadPath) && File.Exists(legacy);
    }

    private static string? FindSingleModernPayload(string sidecarPath)
    {
        var fileName = Path.GetFileName(sidecarPath);
        const string suffix = ".manifest.json";
        if (!fileName.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var stem = fileName[..^suffix.Length];
        return Directory.EnumerateFiles(Path.GetDirectoryName(sidecarPath)!, $"{stem}.*")
            .Where(path => !PathsEqual(path, sidecarPath) && !path.EndsWith(".tmp", StringComparison.Ordinal))
            .Take(2).ToArray() is [var only] ? only : null;
    }

    private static string? FindSingleLegacyPayload(string sidecarPath)
    {
        if (!sidecarPath.EndsWith(".json", StringComparison.Ordinal) ||
            sidecarPath.EndsWith(".manifest.json", StringComparison.Ordinal)) return null;
        var stem = Path.GetFileNameWithoutExtension(sidecarPath);
        return Directory.EnumerateFiles(Path.GetDirectoryName(sidecarPath)!, $"{stem}.*")
            .Where(path => !PathsEqual(path, sidecarPath) &&
                !path.EndsWith(".manifest.json", StringComparison.Ordinal) &&
                !path.EndsWith(".tmp", StringComparison.Ordinal))
            .Take(2).ToArray() is [var only] ? only : null;
    }

    private string ResolveDerived(string relativePath)
    {
        var path = ResolveRoot(relativePath);
        var prefix = Path.TrimEndingDirectorySeparator(_derivedRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison)) throw new InvalidDataException("Processing evidence is outside the derived root.");
        return path;
    }

    private string ResolveCommitted(DurableProcessingEvidence row, string relativePath)
    {
        if (relativePath.StartsWith("derived/", StringComparison.Ordinal))
        {
            return ResolveDerived(relativePath);
        }
        var parts = relativePath.Split('/');
        if (row.Role == FrameArtifactRole.Raw || parts.Length != 6 || parts[0] != "frames" ||
            parts[1].Length != 4 || parts[2].Length != 2 || parts[3].Length != 2 ||
            !string.Equals(parts[4], row.Role.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Processing evidence is outside an allowed committed role path.");
        }
        return ResolveRoot(relativePath);
    }

    private string ResolveRoot(string relativePath)
    {
        if (!SqliteCaptureProcessingStore.IsCanonicalRelativePath(relativePath)) throw new InvalidDataException("Processing path is not canonical.");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison)) throw new InvalidDataException("Processing path escapes its root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }

    private static async ValueTask<bool> FilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
