using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawIngressReconciler(
    string root,
    SqliteRawCaptureJournal journal,
    Action? fileFlushRecorder = null,
    Action? directorySyncRecorder = null,
    IReadOnlyList<CaptureLaneDefinition>? laneDefinitions = null,
    Func<string, Stream>? openPayload = null,
    Func<ArtifactManifestV2, CancellationToken, Task<RecoveredCenteredAdmission>>? prepareCentered = null,
    IReadOnlyList<(Guid ArtifactId, RawIngressPaths Paths)>? siloIntents = null,
    int? siloMaximumStagedFiles = null)
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly SqliteRawCaptureJournal _journal = journal;
    private readonly IReadOnlyList<CaptureLaneDefinition> _laneDefinitions = laneDefinitions ?? [];
    private readonly Action? _fileFlushRecorder = fileFlushRecorder;
    private readonly Action? _directorySyncRecorder = directorySyncRecorder;
    private readonly Func<string, Stream> _openPayload = openPayload ?? OpenPayload;

    /// <summary>A quarantine move found by inspection and made only under the maintenance barrier.</summary>
    private sealed record PendingQuarantine(string? PayloadPath, string? SidecarPath, string Reason, Guid? CaptureId);

    /// <summary>
    /// Inspects the journal and the raw tree, recovers valid orphan evidence and removes temporary files, and then
    /// makes every quarantine move it found, and resumes every planned one, only while
    /// <paramref name="enterMaintenance"/> grants the capture maintenance barrier. A snapshot restore can bring back the
    /// rows that name those files, so while capture admission is not running no evidence is moved: the moves are
    /// counted as deferred and nothing is marked for them, and the next reconciliation makes them.
    /// </summary>
    /// <remarks>
    /// A committed entry's payload was flushed before its row committed, so this checks its structure only: the sidecar
    /// is the committed manifest and the payload has the committed length. Its bytes are not read, so the pass does not
    /// grow with retained payload bytes; the consumers that read a payload verify its checksum first. Evidence without a
    /// committed row, which a crash can leave, is still hashed in full, as is every committed payload whose lane work
    /// ended because processing found its evidence unusable. That outcome is durable, so every pass, a restart's
    /// included, verifies the payload again until it is quarantined or an operator releases it.
    /// </remarks>
    internal async Task<RawIngressReconciliationSummary> RunAsync(
        Func<CancellationToken, ValueTask<Capture.CaptureMaintenanceEntry>> enterMaintenance,
        CancellationToken cancellationToken,
        CancellationToken interruptionToken = default)
    {
        ArgumentNullException.ThrowIfNull(enterMaintenance);
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptionToken);
        var inspectionToken = interruption.Token;
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
        long quarantineBytes = 0;

        foreach (var cleanup in await _journal.ReadPlannedCleanupsAsync(inspectionToken).ConfigureAwait(false))
        {
            inspectionToken.ThrowIfCancellationRequested();
            var temporaryPath = Resolve(cleanup.Value);
            if (File.Exists(temporaryPath))
            {
                RetainedFileMutations.Delete(temporaryPath);
                SyncDirectory(Path.GetDirectoryName(temporaryPath)!);
            }
            await _journal.CompleteReconciliationAsync(cleanup.Key, cancellationToken).ConfigureAwait(false);
            cleaned++;
        }

        planned.AddRange(await _journal.ReadPlannedQuarantinesAsync(inspectionToken).ConfigureAwait(false));
        var plannedCaptures = planned.Where(static operation => operation.CaptureId is not null)
            .Select(static operation => operation.CaptureId!.Value).ToHashSet();
        var plannedPaths = planned.SelectMany(static operation =>
            new[] { operation.SourceRelativePath, operation.CompanionRelativePath }).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var temporaryPath in EnumerateIntentPartials())
        {
            inspectionToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, temporaryPath);
            var bytes = new FileInfo(temporaryPath).Length;
            var relative = Relative(temporaryPath);
            var evidenceKey = $"temp:{Guid.NewGuid():N}";
            await _journal.PlanCleanupAsync(
                evidenceKey, relative, bytes, cancellationToken).ConfigureAwait(false);
            RetainedFileMutations.Delete(temporaryPath);
            SyncDirectory(Path.GetDirectoryName(temporaryPath)!);
            await _journal.CompleteReconciliationAsync(evidenceKey, cancellationToken).ConfigureAwait(false);
            cleaned++;
        }

        // A durable plan still owns its paths, even if a newer companion could otherwise be recovered.
        var claimedPayloads = new HashSet<string>(plannedPaths, StringComparer.Ordinal);
        var claimedSidecars = new HashSet<string>(plannedPaths, StringComparer.Ordinal);
        var reportedCaptures = siloMaximumStagedFiles is null
            ? await _journal.ReadReportedEvidenceFailuresAsync(inspectionToken).ConfigureAwait(false)
            : null;
        await foreach (var entry in EnumerateReconciliationEntriesAsync(inspectionToken).ConfigureAwait(false))
        {
            claimedPayloads.Add(entry.PayloadRelativePath);
            claimedSidecars.Add(entry.SidecarRelativePath);
            inspectionToken.ThrowIfCancellationRequested();
            if (entry.EvidenceWrittenOff || entry.State == "quarantined" || plannedCaptures.Contains(entry.CaptureId))
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
                if (siloMaximumStagedFiles is null && !entry.RetentionHold)
                {
                    continue;
                }
                await _journal.MarkEvidenceFailureAsync(
                    entry.CaptureId, "missing_evidence", "required-file-missing", cancellationToken).ConfigureAwait(false);
                missingEvidence++;
                continue;
            }
            if (!await MatchesCommittedEntryAsync(
                    entry, payloadPath, sidecarPath, reportedCaptures?.Contains(entry.CaptureId) != false ? _openPayload : null,
                    inspectionToken).ConfigureAwait(false))
            {
                Defer(payloadPath, sidecarPath, "committed-evidence-mismatch", entry.CaptureId);
                continue;
            }
        }

        foreach (var sidecarPath in (siloIntents ?? []).Select(static intent => intent.Paths.SidecarAbsolutePath).Where(File.Exists))
        {
            inspectionToken.ThrowIfCancellationRequested();
            var sidecarRelative = Relative(sidecarPath);
            if (claimedSidecars.Contains(sidecarRelative))
            {
                continue;
            }
            inspected++;
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, inspectionToken).ConfigureAwait(false);
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
                !await PayloadMatchesAsync(payloadPath, manifest.Descriptor, _openPayload, inspectionToken).ConfigureAwait(false))
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
                EvidenceOrigin: GalleryEvidenceClassifier.Classify(manifest))
            {
                CenteredExposure = manifest.CenteredExposure
            };
            SyncFile(payloadPath);
            SyncFile(sidecarPath);
            SyncDirectory(Path.GetDirectoryName(sidecarPath)!);
            try
            {
                var centered = manifest.CenteredExposure is null ? null :
                    await (prepareCentered ?? throw new InvalidDataException(
                        "Centered orphan recovery requires the frozen graph and admission services."))
                        (manifest, cancellationToken).ConfigureAwait(false);
                await (_laneDefinitions.Count == 0 && centered is null
                    ? _journal.RecoverAsync(entry, cancellationToken)
                    : _journal.RecoverAsync(entry, _laneDefinitions, cancellationToken, centered)).ConfigureAwait(false);
            }
            catch (RawIngressConflictException)
            {
                Defer(payloadPath, sidecarPath, "orphan-identity-conflict");
                continue;
            }
            claimedPayloads.Add(entry.PayloadRelativePath);
            recovered++;
        }

        foreach (var payloadPath in (siloIntents ?? []).Select(static intent => intent.Paths.PayloadAbsolutePath).Where(File.Exists))
        {
            inspectionToken.ThrowIfCancellationRequested();
            // A sidecar due for quarantine does not protect a newer sibling: defer that sibling as its own unit.
            var sibling = Path.ChangeExtension(payloadPath, ".json");
            if (claimedPayloads.Contains(Relative(payloadPath)) ||
                File.Exists(sibling) && !pendingSidecars.Contains(sibling) && !plannedPaths.Contains(Relative(sibling)))
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
            using var maintenance = await enterMaintenance(inspectionToken).ConfigureAwait(false);
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
                    if (maintenance.InterruptionToken.IsCancellationRequested || interruptionToken.IsCancellationRequested) break;
                    await ResumeQuarantineAsync(operation, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    quarantineBytes += operation.ObservedBytes;
                    deferred--;
                }
                foreach (var move in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (maintenance.InterruptionToken.IsCancellationRequested || interruptionToken.IsCancellationRequested) break;
                    quarantineBytes += await QuarantinePairAsync(
                        move.PayloadPath, move.SidecarPath, move.Reason, move.CaptureId, cancellationToken).ConfigureAwait(false);
                    quarantined++;
                    deferred--;
                }
                await _journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return new RawIngressReconciliationSummary(
            inspected, recovered, cleaned, quarantined, missingEvidence, quarantineBytes,
            MaintenanceDeferred: deferred);
    }

    private async IAsyncEnumerable<RawIngressJournalEntry> EnumerateReconciliationEntriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (siloMaximumStagedFiles is not { } limit)
        {
            await foreach (var entry in _journal.EnumerateAsync(cancellationToken).ConfigureAwait(false)) yield return entry;
            yield break;
        }
        var staged = await _journal.ReadPendingSiloAdmissionsAsync(limit + 1, cancellationToken).ConfigureAwait(false);
        if (staged.Count > limit)
            throw new InvalidDataException("The retained Silo raw admission inventory exceeds its configured bound.");
        var known = new HashSet<Guid>();
        foreach (var entry in staged)
        {
            known.Add(entry.ArtifactId);
            yield return entry;
        }
        foreach (var (artifact, _) in siloIntents ?? [])
        {
            if (!known.Contains(artifact) && await _journal.ReadSiloAdmissionByArtifactAsync(artifact, cancellationToken).ConfigureAwait(false) is { } entry)
                yield return entry;
        }
    }

    private IEnumerable<string> EnumerateIntentPartials()
    {
        foreach (var (_, paths) in siloIntents ?? [])
        {
            var directory = Path.GetDirectoryName(paths.PayloadAbsolutePath)!;
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, directory);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in EnumerateFiles(directory, Path.GetFileNameWithoutExtension(paths.PayloadAbsolutePath) + ".*.tmp", recurse: false))
                yield return path;
        }
    }

    internal static Task<bool> MatchesCommittedEntryAsync(
        RawIngressJournalEntry entry,
        string payloadPath,
        string sidecarPath,
        CancellationToken cancellationToken)
        => MatchesCommittedEntryAsync(entry, payloadPath, sidecarPath, OpenPayload, cancellationToken);

    /// <summary>Checks a committed entry's files; with no <paramref name="openPayload"/>, the payload's length only.</summary>
    private static async Task<bool> MatchesCommittedEntryAsync(
        RawIngressJournalEntry entry,
        string payloadPath,
        string sidecarPath,
        Func<string, Stream>? openPayload,
        CancellationToken cancellationToken)
    {
        // The sidecar must equal the committed manifest, so one of another length is not read.
        if (new FileInfo(sidecarPath).Length != entry.ManifestJson.Length)
        {
            return false;
        }
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
               (openPayload is null
                   ? new FileInfo(payloadPath).Length == descriptor.Layout.ByteLength
                   : await PayloadMatchesAsync(payloadPath, descriptor, openPayload, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> PayloadMatchesAsync(
        string payloadPath,
        ReconstructionDescriptor descriptor,
        Func<string, Stream> openPayload,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(payloadPath).Length != descriptor.Layout.ByteLength)
        {
            return false;
        }
        using var stream = openPayload(payloadPath);
        var checksum = await PayloadChecksum.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(checksum, descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static Stream OpenPayload(string payloadPath)
        => new FileStream(
            payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<long> QuarantinePairAsync(
        string? payloadPath,
        string? sidecarPath,
        string reason,
        Guid? captureId,
        CancellationToken cancellationToken)
    {
        var existing = new[] { payloadPath, sidecarPath }
            .Where(static path => path is not null && File.Exists(path))
            .Cast<string>()
            .ToArray();
        if (existing.Length == 0)
        {
            if (captureId is { } missingCapture)
                await _journal.MarkEvidenceFailureAsync(missingCapture, "quarantined", reason, cancellationToken).ConfigureAwait(false);
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
            bytes,
            captureId);
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
                RetainedFileMutations.Move(sourcePath, destinationPath, overwrite: false);
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
        // Moving the evidence and changing its capture disposition are one durable unit. Keep the plan
        // replayable until the disposition is written; a pause may yield only after both are complete.
        if (operation.CaptureId is { } captureId)
            await _journal.MarkEvidenceFailureAsync(captureId, "quarantined", operation.Reason, cancellationToken).ConfigureAwait(false);
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

    private string Relative(string path)
        => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
