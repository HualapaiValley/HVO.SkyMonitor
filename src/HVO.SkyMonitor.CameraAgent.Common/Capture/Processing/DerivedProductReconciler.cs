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

internal sealed partial class DerivedProductReconciler(
    string storageRoot,
    SqliteCaptureProcessingStore store,
    DerivedProductLifecycleOptions? options = null,
    TimeProvider? timeProvider = null,
    Action? laneWakeup = null,
    Microsoft.Extensions.Logging.ILogger? logger = null,
    Func<CancellationToken, ValueTask>? observationCompleted = null)
{
    private readonly Microsoft.Extensions.Logging.ILogger? _logger = logger;
    private readonly Func<CancellationToken, ValueTask>? _observationCompleted = observationCompleted;
    private readonly SqliteCaptureProcessingStore _store = store;
    private readonly string _root = Path.GetFullPath(storageRoot);
    private readonly string _derivedRoot = Path.Combine(Path.GetFullPath(storageRoot), "derived");
    private readonly DerivedProductLifecycleOptions _options = options ?? new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async ValueTask<DerivedProductReconciliationSummary> RunAsync(CancellationToken cancellationToken, CancellationToken interruptionToken = default)
    {
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptionToken);
        var passToken = interruption.Token;
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
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
            passToken.ThrowIfCancellationRequested();
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            var orphanPage = await _store.ReadOrphanLifecyclePageAsync(
                reclaimedCursor, 16, cancellationToken).ConfigureAwait(false);
            _ = await _store.DeleteReclaimedOrphanPageAsync(
                reclaimedCursor, 16, cancellationToken).ConfigureAwait(false);
            reclaimedCursor = orphanPage.NextOperationId;
        }
        while (reclaimedCursor is not null);

        string? actionableCursor = null;
        do
        {
            passToken.ThrowIfCancellationRequested();
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            var actionable = await _store.ReadActionableLifecyclePageAsync(
                actionableCursor, 1, cancellationToken).ConfigureAwait(false);
            foreach (var operation in actionable.Items)
            {
                passToken.ThrowIfCancellationRequested();
                var copies = await ObserveOperationCopiesAsync(operation, unit, cancellationToken).ConfigureAwait(false);
                if (copies.Current)
                    await ResumeOperationCoreAsync(operation, cancellationToken, copies.Value).ConfigureAwait(false);
            }
            actionableCursor = actionable.NextOperationId;
        }
        while (actionableCursor is not null);

        using (await EnterUnitAsync(passToken).ConfigureAwait(false))
        {
            Directory.CreateDirectory(_derivedRoot);
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, _derivedRoot);
        }
        foreach (var temporary in EnumeratePage("*.tmp"))
        {
            passToken.ThrowIfCancellationRequested();
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            if (!File.Exists(temporary)) continue;
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, temporary);
            File.Delete(temporary);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(temporary)!);
            cleaned++;
        }

        var cursor = await _store.ReadReconciliationCursorAsync(cancellationToken).ConfigureAwait(false);
        var page = await _store.ReadProcessingEvidencePageAsync(
            cursor, _options.ReconciliationBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var row in page.Items)
        {
            passToken.ThrowIfCancellationRequested();
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            // The page is an inventory hint; retention/publication may have changed it between units.
            if (!await IsCurrentRowAsync(row, cancellationToken).ConfigureAwait(false)) continue;
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
                var transition = await _store.TransitionOutputUnavailableAsync(
                    row.OutputIdentitySha256, "Quarantined", "unsafe-committed-path", null,
                    cancellationToken).ConfigureAwait(false);
                if (transition.Reactivated) laneWakeup?.Invoke();
                quarantined++;
                continue;
            }
            if (!File.Exists(payload) || !File.Exists(sidecar))
            {
                var transition = await _store.TransitionOutputUnavailableAsync(
                    row.OutputIdentitySha256, "Missing", "required-file-missing", null, cancellationToken).ConfigureAwait(false);
                if (transition.Reactivated) laneWakeup?.Invoke();
                missing++;
                continue;
            }
            var observation = await unit.ObserveAsync(
                () => ValidateCommittedAsync(row, payload, sidecar, cancellationToken),
                [payload, sidecar], cancellationToken).ConfigureAwait(false);
            if (!observation.Current || !await IsCurrentRowAsync(row, cancellationToken).ConfigureAwait(false)) continue;
            var reason = observation.Value;
            if (reason is null)
            {
                if (row.AvailabilityState == "Missing")
                    _ = await _store.RestoreMissingOutputAvailableAsync(
                        row.OutputIdentitySha256, cancellationToken).ConfigureAwait(false);
                else
                    await _store.SetOutputAvailabilityAsync(row.OutputIdentitySha256, "Available", null, cancellationToken).ConfigureAwait(false);
                available++;
                continue;
            }
            var operation = await PlanQuarantineAsync(row.OutputIdentitySha256, payload, sidecar, reason, cancellationToken).ConfigureAwait(false);
            await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            quarantined++;
            quarantineBytes += operation.ObservedBytes;
        }
        await _store.SetReconciliationCursorAsync(page.NextOutputIdentitySha256, cancellationToken).ConfigureAwait(false);

        var claimedPaths = new HashSet<string>(StringComparer.Ordinal);
        var modernCursor = await _store.ReadFileCursorAsync("modern", cancellationToken).ConfigureAwait(false);
        var modernPage = EnumeratePage("*.json", modernCursor).ToArray();
        foreach (var sidecar in modernPage)
        {
            passToken.ThrowIfCancellationRequested();
            var observedPaths = await ObservationPathsAsync(sidecar, cancellationToken).ConfigureAwait(false);
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            if (!File.Exists(sidecar)) continue;
            var sidecarRelative = Relative(sidecar);
            if (!sidecar.EndsWith(".manifest.json", StringComparison.Ordinal))
            {
                var observation = await unit.ObserveAsync(
                    () => InspectCurrentOrphanSidecarAsync(sidecar, cancellationToken),
                    observedPaths, cancellationToken).ConfigureAwait(false);
                if (!observation.Current || observation.Value.PayloadPath is { } currentPayload && !observedPaths.Contains(currentPayload)) continue;
                var current = observation.Value;
                if (!current.IsCurrentManifest) continue;
                inspected++;
                if (await _store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false))
                {
                    _ = await _store.DeleteClaimedOrphanOperationsAsync(
                        sidecarRelative, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var currentRecoverable = current.Recoverable && await InspectOrphanAgeAsync(
                    current.OperationId!, Relative(current.PayloadPath!), sidecarRelative, cancellationToken).ConfigureAwait(false);
                if (currentRecoverable)
                {
                    recoverable++;
                    claimedPaths.Add(current.PayloadPath!);
                    claimedPaths.Add(sidecar);
                    continue;
                }
                var currentOperation = await PlanQuarantineAsync(
                    null, current.PayloadPath, sidecar, current.Reason ?? "orphan-recovery-window-expired", cancellationToken).ConfigureAwait(false);
                await ResumeOperationAsync(currentOperation, cancellationToken).ConfigureAwait(false);
                quarantined++;
                quarantineBytes += currentOperation.ObservedBytes;
                continue;
            }
            if (await _store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false))
            {
                _ = await _store.DeleteClaimedOrphanOperationsAsync(
                    sidecarRelative, cancellationToken).ConfigureAwait(false);
                continue;
            }
            inspected++;
            var modernObservation = await unit.ObserveAsync(
                () => InspectOrphanSidecarAsync(sidecar, cancellationToken),
                observedPaths, cancellationToken).ConfigureAwait(false);
            if (!modernObservation.Current || modernObservation.Value.PayloadPath is { } modernPayload && !observedPaths.Contains(modernPayload)) continue;
            if (await _store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false)) continue;
            var result = modernObservation.Value;
            var modernRecoverable = result.Recoverable && await InspectOrphanAgeAsync(
                result.OperationId!, Relative(result.PayloadPath!), sidecarRelative, cancellationToken).ConfigureAwait(false);
            if (modernRecoverable)
            {
                recoverable++;
                claimedPaths.Add(result.PayloadPath!);
                claimedPaths.Add(sidecar);
                continue;
            }
            if (result.PayloadPath is not null && string.Equals(result.Reason, "invalid-or-unsafe-sidecar", StringComparison.Ordinal))
            {
                var malformed = await unit.ObserveAsync(async () => ProcessingIdentity.ComputePayloadSha256(
                    await ReadObservedBytesAsync(sidecar, cancellationToken).ConfigureAwait(false)),
                    observedPaths, cancellationToken).ConfigureAwait(false);
                if (!malformed.Current ||
                    await _store.IsProcessingPathClaimedAsync(sidecarRelative, cancellationToken).ConfigureAwait(false)) continue;
                var identity = malformed.Value;
                if (await InspectOrphanAgeAsync(
                        $"orphan:modern-malformed:{identity}", Relative(result.PayloadPath), sidecarRelative,
                        cancellationToken).ConfigureAwait(false))
                    continue;
            }
            var operation = await PlanQuarantineAsync(null, result.PayloadPath, sidecar, result.Reason ?? "orphan-recovery-window-expired", cancellationToken).ConfigureAwait(false);
            await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            quarantined++;
            quarantineBytes += operation.ObservedBytes;
        }
        await _store.SetFileCursorAsync("modern",
            modernPage.Length == _options.ReconciliationBatchSize ? Relative(modernPage[^1]) : null,
            cancellationToken).ConfigureAwait(false);

        var payloadCursor = await _store.ReadFileCursorAsync("payload", cancellationToken).ConfigureAwait(false);
        var payloadPage = EnumeratePage("*", payloadCursor).ToArray();
        foreach (var payload in payloadPage)
        {
            passToken.ThrowIfCancellationRequested();
            using var unit = await EnterUnitAsync(passToken).ConfigureAwait(false);
            if (!File.Exists(payload)) continue;
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, payload);
            var relative = Relative(payload);
            if (payload.EndsWith(".manifest.json", StringComparison.Ordinal) || payload.EndsWith(".tmp", StringComparison.Ordinal) ||
                claimedPaths.Contains(payload) || await _store.IsProcessingPathClaimedAsync(relative, cancellationToken).ConfigureAwait(false)) continue;
            if (payload.EndsWith(".json", StringComparison.Ordinal))
            {
                var observation = await unit.ObserveAsync(
                    () => IsCurrentManifestAsync(payload, cancellationToken), [payload], cancellationToken).ConfigureAwait(false);
                if (!observation.Current || observation.Value ||
                    await _store.IsProcessingPathClaimedAsync(relative, cancellationToken).ConfigureAwait(false)) continue;
            }
            if (HasPotentialCompanion(payload)) continue;
            inspected++;
            var operation = await PlanQuarantineAsync(null, payload, null, "payload-without-sidecar", cancellationToken).ConfigureAwait(false);
            await ResumeOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            quarantined++;
            quarantineBytes += operation.ObservedBytes;
        }
        await _store.SetFileCursorAsync("payload",
            payloadPage.Length == _options.ReconciliationBatchSize ? Relative(payloadPage[^1]) : null,
            cancellationToken).ConfigureAwait(false);
        return new(inspected, available, recoverable, cleaned, missing, quarantined, quarantineBytes);
    }

    private async ValueTask<(bool Recoverable, string? PayloadPath, string? Reason, string? OperationId)> InspectOrphanSidecarAsync(
        string sidecarPath, CancellationToken cancellationToken)
    {
        IDurableProcessingProductManifest manifest;
        try
        {
            manifest = DurableProcessingProductManifestJson.Parse(
                await ReadObservedBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return (false, FindSingleModernPayload(sidecarPath), "invalid-or-unsafe-sidecar", null);
        }

        // Never resolve a manifest-named path before proving it names this sidecar's canonical sibling.
        var sidecarRelative = Relative(sidecarPath);
        if (!SqliteCaptureProcessingStore.IsCanonicalRelativePath(manifest.RelativeArtifactPath) ||
            !manifest.RelativeArtifactPath.StartsWith("derived/", StringComparison.Ordinal) ||
            !string.Equals(sidecarRelative, Path.ChangeExtension(manifest.RelativeArtifactPath, ".manifest.json"), StringComparison.Ordinal))
            return (false, null, "orphan-path-mismatch", null);
        var payloadPath = ResolveDerived(manifest.RelativeArtifactPath);
        if (!PathsEqual(Path.ChangeExtension(sidecarPath, Path.GetExtension(payloadPath)), payloadPath))
            return (false, null, "orphan-path-mismatch", null);
        if (!File.Exists(payloadPath)) return (false, null, "sidecar-without-payload", null);
        var reason = await ValidatePayloadAsync(manifest, payloadPath, cancellationToken).ConfigureAwait(false);
        if (reason is not null) return (false, null, reason, null);

        var operationId = $"orphan:{manifest.OutputIdentitySha256}";
        return (true, payloadPath, null, operationId);
    }

    private async ValueTask<(bool IsCurrentManifest, bool Recoverable, string? PayloadPath, string? Reason, string? OperationId)>
        InspectCurrentOrphanSidecarAsync(string sidecarPath, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await ReadObservedBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return (false, false, null, null, null);
        }
        var parsed = CaptureContractJson.ParseManifest(bytes);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
            return (false, false, null, null, null);

        var sidecarRelative = Relative(sidecarPath);
        if (!SqliteCaptureProcessingStore.IsCanonicalRelativePath(manifest.RelativeArtifactPath) ||
            !manifest.RelativeArtifactPath.StartsWith("derived/", StringComparison.Ordinal) ||
            !string.Equals(sidecarRelative, Path.ChangeExtension(manifest.RelativeArtifactPath, ".json"), StringComparison.Ordinal))
            return (true, false, null, "orphan-path-mismatch", null);
        var payloadPath = ResolveDerived(manifest.RelativeArtifactPath);
        if (!File.Exists(payloadPath)) return (true, false, null, "sidecar-without-payload", null);
        if (new FileInfo(payloadPath).Length != manifest.Descriptor.Layout.ByteLength)
            return (true, false, payloadPath, "payload-length-mismatch", null);
        var payload = await ReadObservedBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                ProcessingIdentity.ComputePayloadSha256(payload),
                manifest.Descriptor.Artifact.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
            return (true, false, payloadPath, "payload-checksum-mismatch", null);

        var operationId = $"orphan:current:{CaptureContractJson.ComputeManifestSha256(bytes)}";
        return (true, true, payloadPath, null, operationId);
    }

    private static async ValueTask<bool> IsCurrentManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var parsed = CaptureContractJson.ParseManifest(
                await ReadObservedBytesAsync(path, cancellationToken).ConfigureAwait(false));
            return parsed.IsValid && parsed.Document?.Manifest is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async ValueTask<bool> InspectOrphanAgeAsync(
        string operationId, string payloadRelativePath, string sidecarRelativePath,
        CancellationToken cancellationToken)
    {
        var existing = await _store.ReadOrphanLifecycleOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await _store.PlanLifecycleOperationAsync(new(
                operationId, "orphan", null, payloadRelativePath, sidecarRelativePath,
                string.Empty, "awaiting-deterministic-retry", 0), cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - existing.PlannedUnixMilliseconds <
            TimeSpan.FromMinutes(_options.OrphanRecoveryWindowMinutes).TotalMilliseconds)
            return true;
        await _store.DeleteLifecycleOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<string?> ValidateCommittedAsync(
        DurableProcessingEvidence row, string payloadPath, string sidecarPath, CancellationToken cancellationToken)
    {
        try
        {
            var sidecar = await ReadObservedBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
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
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } current ||
                !string.Equals(row.PayloadRelativePath, current.RelativeArtifactPath, StringComparison.Ordinal) ||
                !string.Equals(row.SidecarRelativePath, Path.ChangeExtension(row.PayloadRelativePath, ".json"), StringComparison.Ordinal) ||
                !PathsEqual(sidecarPath, ResolveCommitted(row, row.SidecarRelativePath)) ||
                row.ArtifactId != current.Descriptor.Artifact.ArtifactId ||
                row.CaptureId != current.Descriptor.Capture.CaptureId)
            {
                return "committed-identity-conflict";
            }
            var payload = await ReadObservedBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            return payload.LongLength == current.Descriptor.Layout.ByteLength &&
                string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(payload),
                    current.Descriptor.Artifact.ChecksumSha256,
                    StringComparison.OrdinalIgnoreCase)
                ? null
                : "payload-checksum-mismatch";
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
        var payload = await ReadObservedBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), manifest.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            return "payload-checksum-mismatch";
        if (manifest is DurableTypedMetadataProductManifestV3 typed &&
            ProjectedSceneV1.IsSupportedSchemaVersion(typed.ProductSchemaVersion))
        {
            var parsed = ProjectedSceneJson.Parse(payload);
            if (parsed.Scene is not { } scene) return "projected-scene-invalid";
            if (!string.Equals(scene.SceneIdentitySha256, typed.ContentIdentitySha256, StringComparison.Ordinal) ||
                scene.Source.CaptureId != manifest.Capture.CaptureId ||
                !manifest.Artifact.SourceArtifactIds.Contains(scene.Source.ArtifactId) ||
                !await _store.MatchesRawSourceAsync(scene.Source.CaptureId, scene.Source.ArtifactId,
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
        await _store.PlanLifecycleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        return operation;
    }

    internal ValueTask ResumeOperationAsync(ProcessingLifecycleOperation operation, CancellationToken cancellationToken)
        => ResumeOperationCoreAsync(operation, cancellationToken);

    private async ValueTask ResumeOperationCoreAsync(
        ProcessingLifecycleOperation operation, CancellationToken cancellationToken,
        Dictionary<string, bool>? comparedCopies = null)
    {
        if (operation.Kind == "delete" && operation.Phase == "files-deleted")
        {
            await _store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var destinationRoot = ResolveRoot(operation.DestinationRelativePath);
        if (operation.Kind == "delete" && operation.Phase == "database-completed")
        {
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationRoot)!);
            await _store.SetLifecyclePhaseAsync(operation.OperationId, "files-deleted", cancellationToken).ConfigureAwait(false);
            await _store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
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
                    var equal = comparedCopies is not null && comparedCopies.TryGetValue(source, out var observedEqual)
                        ? observedEqual : await FilesEqualAsync(source, destination, cancellationToken).ConfigureAwait(false);
                    if (!equal)
                        throw new InvalidDataException("Processing lifecycle source and destination conflict.");
                    File.Delete(source);
                }
                else if (File.Exists(source)) File.Move(source, destination, overwrite: false);
                else if (!File.Exists(destination)) throw new InvalidDataException("Processing lifecycle evidence is missing.");
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(source)!);
            }
            RawIngressFileStore.SyncDirectory(destinationRoot);
            await _store.SetLifecyclePhaseAsync(operation.OperationId, "moved", cancellationToken).ConfigureAwait(false);
            operation = operation with { Phase = "moved" };
        }
        if (operation.Phase == "moved")
        {
            if (operation.Kind == "quarantine" && operation.OutputIdentitySha256 is { } outputIdentity)
            {
                var transition = await _store.TransitionOutputUnavailableAsync(
                    outputIdentity, "Quarantined", operation.Reason, operation.DestinationRelativePath,
                    cancellationToken).ConfigureAwait(false);
                await _store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                if (transition.Reactivated) laneWakeup?.Invoke();
            }
            else
            {
                await _store.CompleteLifecycleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            if (operation.Kind == "delete")
            {
                if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
                RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(destinationRoot)!);
                await _store.SetLifecyclePhaseAsync(operation.OperationId, "files-deleted", cancellationToken).ConfigureAwait(false);
                await _store.DeleteLifecycleOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<string> EnumeratePage(string pattern, string? afterRelativePath = null) => Directory.EnumerateFiles(_derivedRoot, pattern,
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
        .Order(StringComparer.Ordinal)
        .Where(path => afterRelativePath is null || string.CompareOrdinal(Relative(path), afterRelativePath) > 0)
        .Take(_options.ReconciliationBatchSize);

    private static bool HasPotentialCompanion(string payloadPath)
    {
        var modern = Path.ChangeExtension(payloadPath, ".manifest.json");
        var current = Path.ChangeExtension(payloadPath, ".json");
        return !PathsEqual(modern, payloadPath) && File.Exists(modern) ||
            !PathsEqual(current, payloadPath) && File.Exists(current);
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
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
