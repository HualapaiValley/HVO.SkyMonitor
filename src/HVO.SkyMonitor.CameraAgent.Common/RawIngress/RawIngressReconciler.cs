using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawIngressReconciler(
    string root,
    SqliteRawCaptureJournal journal,
    Action? fileFlushRecorder = null,
    Action? directorySyncRecorder = null,
    IReadOnlyList<CaptureLaneDefinition>? laneDefinitions = null)
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly SqliteRawCaptureJournal _journal = journal;
    private readonly IReadOnlyList<CaptureLaneDefinition> _laneDefinitions = laneDefinitions ?? [];
    private readonly Action? _fileFlushRecorder = fileFlushRecorder;
    private readonly Action? _directorySyncRecorder = directorySyncRecorder;

    /// <summary>A quarantine move found by inspection and made only under the maintenance barrier.</summary>
    private sealed record PendingQuarantine(string? PayloadPath, string? SidecarPath, string Reason, Guid? CaptureId);

    /// <summary>
    /// Inspects the journal and the raw tree, recovers valid orphan evidence and removes temporary files, and then
    /// makes every quarantine move it found, and resumes every planned one, only while
    /// <paramref name="enterMaintenance"/> grants the capture maintenance barrier. A snapshot restore can bring back the
    /// rows that name those files, so while capture admission is not running no evidence is moved: the moves are
    /// counted as deferred and nothing is marked for them, and the next reconciliation makes them.
    /// </summary>
    internal async Task<RawIngressReconciliationSummary> RunAsync(
        Func<CancellationToken, ValueTask<Capture.CaptureMaintenanceEntry>> enterMaintenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enterMaintenance);
        var planned = new List<RawIngressPlannedQuarantine>();
        var pending = new List<PendingQuarantine>();
        var pendingSidecars = new HashSet<string>(StringComparer.Ordinal);
        void Defer(string? payloadPath, string? sidecarPath, string reason, Guid? captureId = null)
        {
            pending.Add(new PendingQuarantine(payloadPath, sidecarPath, reason, captureId));
            if (sidecarPath is not null) pendingSidecars.Add(sidecarPath);
        }
        var inspected = 0;
        var recovered = 0;
        var cleaned = 0;
        var quarantined = 0;
        var missingEvidence = 0;
        var indexProjectionFailures = 0;
        long quarantineBytes = 0;
        var framesRoot = Path.Combine(_root, "frames");
        Directory.CreateDirectory(framesRoot);
        EnsureTreeHasNoLinks(framesRoot);

        foreach (var cleanup in await _journal.ReadPlannedCleanupsAsync(cancellationToken).ConfigureAwait(false))
        {
            var temporaryPath = Resolve(cleanup.Value);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
                SyncDirectory(Path.GetDirectoryName(temporaryPath)!);
            }
            await _journal.CompleteReconciliationAsync(cleanup.Key, cancellationToken).ConfigureAwait(false);
            cleaned++;
        }

        planned.AddRange(await _journal.ReadPlannedQuarantinesAsync(cancellationToken).ConfigureAwait(false));

        foreach (var temporaryPath in EnumerateFiles(framesRoot, "*.tmp", recurse: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, temporaryPath);
            var bytes = new FileInfo(temporaryPath).Length;
            var relative = Relative(temporaryPath);
            var evidenceKey = $"temp:{Guid.NewGuid():N}";
            await _journal.PlanCleanupAsync(
                evidenceKey, relative, bytes, cancellationToken).ConfigureAwait(false);
            File.Delete(temporaryPath);
            SyncDirectory(Path.GetDirectoryName(temporaryPath)!);
            await _journal.CompleteReconciliationAsync(evidenceKey, cancellationToken).ConfigureAwait(false);
            cleaned++;
        }

        Dictionary<Guid, List<string>> indexedArtifacts;
        try
        {
            indexedArtifacts = ReadIndexedArtifactIds();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            indexedArtifacts = [];
            indexProjectionFailures++;
        }
        var claimedPayloads = new HashSet<string>(StringComparer.Ordinal);
        var claimedSidecars = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in _journal.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            claimedPayloads.Add(entry.PayloadRelativePath);
            claimedSidecars.Add(entry.SidecarRelativePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EvidenceWrittenOff)
            {
                continue;
            }
            inspected++;
            var expectedPaths = RawIngressFileStore.GetPaths(_root, entry.ExposureStartedUtc, entry.ArtifactId);
            var payloadPath = Resolve(entry.PayloadRelativePath);
            var sidecarPath = Resolve(entry.SidecarRelativePath);
            if (!string.Equals(entry.PayloadRelativePath, expectedPaths.PayloadRelativePath, StringComparison.Ordinal) ||
                !string.Equals(entry.SidecarRelativePath, expectedPaths.SidecarRelativePath, StringComparison.Ordinal))
            {
                Defer(payloadPath, sidecarPath, "committed-path-mismatch", entry.CaptureId);
                continue;
            }
            if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
            {
                if (!entry.RetentionHold)
                {
                    continue;
                }
                await _journal.MarkEvidenceFailureAsync(
                    entry.CaptureId, "missing_evidence", "required-file-missing", cancellationToken).ConfigureAwait(false);
                missingEvidence++;
                continue;
            }
            if (!await MatchesCommittedEntryAsync(entry, payloadPath, sidecarPath, cancellationToken).ConfigureAwait(false))
            {
                Defer(payloadPath, sidecarPath, "committed-evidence-mismatch", entry.CaptureId);
                continue;
            }
            // Recovered bytes do not discharge an evidence quarantine without an audited operator release.
            if (!string.Equals(entry.State, "committed", StringComparison.Ordinal))
            {
                continue;
            }
            if (!await TryEnsureCompatibilityIndexAsync(
                    entry.ManifestJson, indexedArtifacts, cancellationToken).ConfigureAwait(false))
            {
                indexProjectionFailures++;
            }
        }

        foreach (var sidecarPath in EnumerateRawFiles("*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sidecarRelative = Relative(sidecarPath);
            if (claimedSidecars.Contains(sidecarRelative))
            {
                continue;
            }
            inspected++;
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
            {
                var siblingPayload = Path.ChangeExtension(sidecarPath, ".bin");
                Defer(claimedPayloads.Contains(Relative(siblingPayload)) ? null : siblingPayload, sidecarPath, "invalid-v2-sidecar");
                continue;
            }
            if (claimedPayloads.Contains(manifest.RelativeArtifactPath))
            {
                Defer(null, sidecarPath, "payload-already-claimed");
                continue;
            }
            var payloadPath = Resolve(manifest.RelativeArtifactPath);
            if (!File.Exists(payloadPath) || !PathsEqual(Path.ChangeExtension(sidecarPath, ".bin"), payloadPath) ||
                !await PayloadMatchesAsync(payloadPath, manifest.Descriptor, cancellationToken).ConfigureAwait(false))
            {
                Defer(payloadPath, sidecarPath, "orphan-evidence-mismatch");
                continue;
            }

            var descriptor = manifest.Descriptor;
            var expectedPaths = RawIngressFileStore.GetPaths(
                _root,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Artifact.ArtifactId);
            if (!string.Equals(manifest.RelativeArtifactPath, expectedPaths.PayloadRelativePath, StringComparison.Ordinal) ||
                !string.Equals(sidecarRelative, expectedPaths.SidecarRelativePath, StringComparison.Ordinal))
            {
                Defer(payloadPath, sidecarPath, "orphan-path-mismatch");
                continue;
            }
            var entry = new RawIngressJournalEntry(
                descriptor.Capture.AgentId,
                descriptor.Capture.CaptureSequence,
                descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(sidecar),
                descriptor.Artifact.ChecksumSha256,
                descriptor.Layout.ByteLength,
                manifest.RelativeArtifactPath,
                sidecarRelative,
                sidecar,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.DurableIngressUtc,
                EvidenceOrigin: GalleryEvidenceClassifier.Classify(manifest));
            SyncFile(payloadPath);
            SyncFile(sidecarPath);
            SyncDirectory(Path.GetDirectoryName(sidecarPath)!);
            try
            {
                await (_laneDefinitions.Count == 0
                    ? _journal.RecoverAsync(entry, cancellationToken)
                    : _journal.RecoverAsync(entry, _laneDefinitions, cancellationToken)).ConfigureAwait(false);
            }
            catch (RawIngressConflictException)
            {
                Defer(payloadPath, sidecarPath, "orphan-identity-conflict");
                continue;
            }
            if (!await TryEnsureCompatibilityIndexAsync(sidecar, indexedArtifacts, cancellationToken).ConfigureAwait(false))
            {
                indexProjectionFailures++;
            }
            claimedPayloads.Add(entry.PayloadRelativePath);
            recovered++;
        }

        foreach (var payloadPath in EnumerateRawFiles("*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A sidecar already due for quarantine no longer pairs with the payload beside it, as when it had been moved.
            var sibling = Path.ChangeExtension(payloadPath, ".json");
            if (claimedPayloads.Contains(Relative(payloadPath)) || File.Exists(sibling) && !pendingSidecars.Contains(sibling))
            {
                continue;
            }
            if (pending.Any(move => move.PayloadPath == payloadPath)) continue;
            inspected++;
            Defer(payloadPath, null, "payload-without-sidecar");
        }

        await _journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        var deferred = 0;
        if (planned.Count + pending.Count > 0)
        {
            using var maintenance = await enterMaintenance(cancellationToken).ConfigureAwait(false);
            if (maintenance.Held)
            {
                deferred = planned.Count + pending.Count;
            }
            else
            {
                deferred = planned.Count + pending.Count;
                foreach (var operation in planned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (maintenance.InterruptionToken.IsCancellationRequested) break;
                    await ResumeQuarantineAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    quarantineBytes += operation.ObservedBytes;
                    deferred--;
                }
                foreach (var move in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (maintenance.InterruptionToken.IsCancellationRequested) break;
                    quarantineBytes += await QuarantinePairAsync(
                        move.PayloadPath, move.SidecarPath, move.Reason, cancellationToken).ConfigureAwait(false);
                    if (move.CaptureId is { } captureId)
                    {
                        await _journal.MarkEvidenceFailureAsync(
                            captureId, "quarantined", move.Reason, cancellationToken).ConfigureAwait(false);
                    }
                    quarantined++;
                    deferred--;
                }
                await _journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return new RawIngressReconciliationSummary(
            inspected, recovered, cleaned, quarantined, missingEvidence, quarantineBytes, indexProjectionFailures,
            MaintenanceDeferred: deferred);
    }

    private IEnumerable<string> EnumerateRawFiles(string pattern)
        => EnumerateFiles(Path.Combine(_root, "frames"), pattern, recurse: true)
            .Where(static path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)), "Raw", StringComparison.Ordinal));

    private Dictionary<Guid, List<string>> ReadIndexedArtifactIds()
    {
        var artifactIds = new Dictionary<Guid, List<string>>();
        var indexRoot = Path.Combine(_root, "index");
        if (!Directory.Exists(indexRoot))
        {
            return artifactIds;
        }
        foreach (var indexPath in EnumerateFiles(indexRoot, "frames_*.jsonl", recurse: false))
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, indexPath);
            foreach (var line in File.ReadLines(indexPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("artifactId", out var artifactId) &&
                        artifactId.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(artifactId.GetString(), out var parsedId))
                    {
                        if (!artifactIds.TryGetValue(parsedId, out var entries))
                        {
                            entries = [];
                            artifactIds.Add(parsedId, entries);
                        }
                        entries.Add(line);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        return artifactIds;
    }

    private async Task EnsureCompatibilityIndexAsync(
        ReadOnlyMemory<byte> manifestJson,
        Dictionary<Guid, List<string>> indexedArtifacts,
        CancellationToken cancellationToken)
    {
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null)
        {
            throw new InvalidDataException("Committed raw manifest is invalid during index repair.");
        }
        var artifactId = manifest.Descriptor.Artifact.ArtifactId;
        if (indexedArtifacts.TryGetValue(artifactId, out var lines))
        {
            foreach (var line in lines)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (RawIngressFileStore.MatchesIndexEntry(document.RootElement, manifest.Descriptor))
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        await RawIngressFileStore.AppendCompatibilityIndexAsync(
            _root, manifestJson, cancellationToken, _fileFlushRecorder).ConfigureAwait(false);
        indexedArtifacts[artifactId] = [];
    }

    private async Task<bool> TryEnsureCompatibilityIndexAsync(
        ReadOnlyMemory<byte> manifestJson,
        Dictionary<Guid, List<string>> indexedArtifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureCompatibilityIndexAsync(manifestJson, indexedArtifacts, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> MatchesCommittedEntryAsync(
        RawIngressJournalEntry entry,
        string payloadPath,
        string sidecarPath,
        CancellationToken cancellationToken)
    {
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (!sidecar.AsSpan().SequenceEqual(entry.ManifestJson))
        {
            return false;
        }
        var parsed = CaptureContractJson.ParseManifest(sidecar);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
        {
            return false;
        }
        var descriptor = manifest.Descriptor;
        return descriptor.Capture.AgentId == entry.AgentId &&
               descriptor.Capture.CaptureSequence == entry.CaptureSequence &&
               descriptor.Capture.CaptureId == entry.CaptureId &&
               descriptor.Artifact.ArtifactId == entry.ArtifactId &&
               CaptureContractJson.ComputeDescriptorSha256(descriptor) == entry.DescriptorSha256 &&
               CaptureContractJson.ComputeManifestSha256(sidecar) == entry.ManifestSha256 &&
               descriptor.Artifact.ChecksumSha256 == entry.PayloadSha256 &&
               descriptor.Layout.ByteLength == entry.PayloadLength &&
               manifest.RelativeArtifactPath == entry.PayloadRelativePath &&
               descriptor.Timing.ExposureStartedUtc == entry.ExposureStartedUtc &&
               descriptor.Timing.DurableIngressUtc == entry.DurableIngressUtc &&
               await PayloadMatchesAsync(payloadPath, descriptor, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PayloadMatchesAsync(
        string payloadPath,
        ReconstructionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(payloadPath).Length != descriptor.Layout.ByteLength)
        {
            return false;
        }
        using var stream = new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = await PayloadChecksum.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(checksum, descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> QuarantinePairAsync(
        string? payloadPath,
        string? sidecarPath,
        string reason,
        CancellationToken cancellationToken)
    {
        var existing = new[] { payloadPath, sidecarPath }
            .Where(static path => path is not null && File.Exists(path))
            .Cast<string>()
            .ToArray();
        if (existing.Length == 0)
        {
            return 0;
        }
        long bytes = 0;
        foreach (var path in existing)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
            bytes += new FileInfo(path).Length;
        }
        var quarantineRelativePath = Path.Combine(
            "quarantine",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("N"));
        var source = Relative(existing[0]);
        var companion = existing.Length > 1 ? Relative(existing[1]) : null;
        var operation = new RawIngressPlannedQuarantine(
            $"quarantine:{Guid.NewGuid():N}",
            source,
            companion,
            quarantineRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
            reason,
            bytes);
        await _journal.PlanQuarantineAsync(operation, cancellationToken).ConfigureAwait(false);
        await ResumeQuarantineAsync(operation, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private async Task ResumeQuarantineAsync(
        RawIngressPlannedQuarantine operation,
        CancellationToken cancellationToken)
    {
        var quarantineRoot = Resolve(operation.QuarantineRelativePath);
        Directory.CreateDirectory(quarantineRoot);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, quarantineRoot);
        SyncDirectoryHierarchy(quarantineRoot);
        var sources = new[] { operation.SourceRelativePath, operation.CompanionRelativePath }
            .Where(static path => path is not null)
            .Cast<string>()
            .ToArray();
        foreach (var sourceRelativePath in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Resolve(sourceRelativePath);
            var destinationPath = Path.Combine(quarantineRoot, Path.GetFileName(sourcePath));
            if (File.Exists(sourcePath))
            {
                if (File.Exists(destinationPath))
                {
                    throw new InvalidDataException("Planned raw ingress quarantine has both source and destination evidence.");
                }
                File.Move(sourcePath, destinationPath, overwrite: false);
            }
            else if (!File.Exists(destinationPath))
            {
                throw new InvalidDataException("Planned raw ingress quarantine evidence is missing from source and destination.");
            }
        }
        SyncDirectory(quarantineRoot);
        foreach (var directory in sources.Select(Resolve).Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal))
        {
            SyncDirectory(directory!);
        }
        SyncDirectoryHierarchy(quarantineRoot);
        await _journal.CompleteReconciliationAsync(operation.EvidenceKey, cancellationToken).ConfigureAwait(false);
    }

    private void SyncFile(string path)
    {
        RawIngressFileStore.SyncFile(_root, path);
        _fileFlushRecorder?.Invoke();
    }

    private void SyncDirectory(string directory)
    {
        RawIngressFileStore.SyncDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            _directorySyncRecorder?.Invoke();
        }
    }

    private void SyncDirectoryHierarchy(string directory)
    {
        var count = RawIngressFileStore.SyncDirectoryHierarchy(_root, directory);
        if (OperatingSystem.IsLinux())
        {
            for (var index = 0; index < count; index++)
            {
                _directorySyncRecorder?.Invoke();
            }
        }
    }

    private string Resolve(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Raw ingress evidence path escapes the configured root.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, bool recurse)
        => Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        });

    private void EnsureTreeHasNoLinks(string treeRoot)
    {
        var pending = new Stack<string>();
        pending.Push(treeRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Raw ingress evidence must not traverse symbolic links.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private string Relative(string path)
        => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
