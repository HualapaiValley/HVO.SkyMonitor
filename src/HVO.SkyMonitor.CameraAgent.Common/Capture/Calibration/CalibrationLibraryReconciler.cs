using System.Security.Cryptography;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed record CalibrationLibraryReconciliationSummary(
    int Inspected,
    int Adopted,
    int Quarantined,
    int Failed);

public sealed class CalibrationLibraryReconciler(
    SqliteCalibrationLibraryStore store,
    IOptions<CameraAgentHostOptions> options,
    CalibrationTelemetry? telemetry = null)
{
    private readonly SqliteCalibrationLibraryStore _store = store;
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly CalibrationTelemetry? _telemetry = telemetry;

    public async Task<CalibrationLibraryReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken)
    {
        using var activity = CalibrationTelemetry.ActivitySource.StartActivity("calibration.library.reconcile");
        _ = await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var adopted = 0;
        var quarantined = 0;
        var failed = 0;
        foreach (var planned in await _store.ReadPlannedReconciliationsAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (planned.Outcome == "quarantined")
                {
                    MovePlannedQuarantine(planned);
                }
                await _store.CompleteReconciliationAsync(
                    planned.EvidenceKey, planned.Reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedReconciliationFailure(exception))
            {
                failed++;
                _telemetry?.RecordValidationFailure("reconcile", CalibrationLibraryReasonCodes.Corrupt);
            }
        }

        string[] directories;
        try
        {
            var syntheticRoot = ResolveSafePath("calibration/synthetic");
            if (!Directory.Exists(syntheticRoot))
            {
                return await CompleteRunAsync(
                    new CalibrationLibraryReconciliationSummary(0, adopted, quarantined, failed),
                    activity,
                    cancellationToken).ConfigureAwait(false);
            }
            directories = Directory.EnumerateDirectories(syntheticRoot)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedReconciliationFailure(exception))
        {
            return await CompleteRunAsync(
                new CalibrationLibraryReconciliationSummary(0, adopted, quarantined, failed + 1),
                activity,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = Normalize(Path.GetRelativePath(_root, directory));
            string? evidenceKey = null;
            long observedBytes = 0;
            try
            {
                (evidenceKey, observedBytes) = await ComputeEvidenceIdentityAsync(
                    directory, cancellationToken).ConfigureAwait(false);
                var profilePath = Path.Combine(directory, CalibrationLibraryEvidenceNames.ProfileMarker);
                if (!File.Exists(profilePath))
                {
                    var profileRelativePath = Normalize(Path.GetRelativePath(_root, profilePath));
                    if (await _store.ContainsPublishedProfileAsync(profileRelativePath, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        var failedOperation = new CalibrationReconciliationOperation(
                            evidenceKey,
                            relativeDirectory,
                            null,
                            "failed",
                            CalibrationLibraryReasonCodes.Corrupt,
                            "planned");
                        await _store.PlanReconciliationAsync(
                            failedOperation, observedBytes, cancellationToken).ConfigureAwait(false);
                        await _store.CompleteReconciliationAsync(
                            evidenceKey, failedOperation.Reason, cancellationToken).ConfigureAwait(false);
                        _telemetry?.RecordValidationFailure("reconcile", failedOperation.Reason);
                        failed++;
                        continue;
                    }
                    var quarantineRelativePath = Normalize(Path.Combine(
                        "quarantine", "calibration", $"{Path.GetFileName(directory)}-{evidenceKey[..12]}"));
                    var quarantineOperation = new CalibrationReconciliationOperation(
                        evidenceKey,
                        relativeDirectory,
                        quarantineRelativePath,
                        "quarantined",
                        CalibrationLibraryReasonCodes.Incomplete,
                        "planned");
                    await _store.PlanReconciliationAsync(quarantineOperation, observedBytes, cancellationToken).ConfigureAwait(false);
                    MovePlannedQuarantine(quarantineOperation);
                    await _store.CompleteReconciliationAsync(
                        evidenceKey, quarantineOperation.Reason, cancellationToken).ConfigureAwait(false);
                    _telemetry?.RecordQuarantine(quarantineOperation.Reason, observedBytes);
                    quarantined++;
                    continue;
                }

                var bundle = await ReadCanonicalBundleAsync(directory, cancellationToken).ConfigureAwait(false);
                _ = await _store.AdoptPublishedBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
                var operation = new CalibrationReconciliationOperation(
                    evidenceKey,
                    relativeDirectory,
                    null,
                    "adopted",
                    "calibration.library.adopted",
                    "planned");
                await _store.PlanReconciliationAsync(operation, observedBytes, cancellationToken).ConfigureAwait(false);
                await _store.CompleteReconciliationAsync(
                    evidenceKey, operation.Reason, cancellationToken).ConfigureAwait(false);
                adopted++;
            }
            catch (Exception exception) when (IsExpectedReconciliationFailure(exception))
            {
                failed++;
                _telemetry?.RecordValidationFailure("reconcile", CalibrationLibraryReasonCodes.Corrupt);
                evidenceKey ??= CaptureContractJson.ComputeCanonicalJsonSha256(new
                {
                    Directory = relativeDirectory,
                    Failure = exception.GetType().Name
                });
                try
                {
                    var operation = new CalibrationReconciliationOperation(
                        evidenceKey,
                        relativeDirectory,
                        null,
                        "failed",
                        CalibrationLibraryReasonCodes.Corrupt,
                        "planned");
                    await _store.PlanReconciliationAsync(operation, observedBytes, cancellationToken).ConfigureAwait(false);
                    await _store.CompleteReconciliationAsync(
                        evidenceKey, operation.Reason, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistenceException) when (IsExpectedReconciliationFailure(persistenceException))
                {
                    // Raw capture remains available even when calibration failure evidence cannot be updated.
                }
            }
        }
        return await CompleteRunAsync(
            new CalibrationLibraryReconciliationSummary(directories.Length, adopted, quarantined, failed),
            activity,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CalibrationLibraryReconciliationSummary> CompleteRunAsync(
        CalibrationLibraryReconciliationSummary summary,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var result = summary.Failed == 0
            ? "calibration.library.reconciled"
            : "calibration.library.reconciliation-failed";
        await _store.RecordReconciliationResultAsync(result, cancellationToken).ConfigureAwait(false);
        _telemetry?.RecordLibraryOperation(
            "reconcile",
            summary.Failed == 0 ? "success" : "failure",
            summary.Inspected,
            summary.Adopted,
            summary.Quarantined,
            summary.Failed);
        activity?.SetTag("calibration.outcome", summary.Failed == 0 ? "success" : "failure");
        activity?.SetStatus(summary.Failed == 0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        return summary;
    }

    private async Task<CalibrationLibraryBundleV1> ReadCanonicalBundleAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, directory);
        var modelIdentity = Path.GetFileName(directory);
        if (modelIdentity.Length != 64 || !modelIdentity.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The synthetic calibration directory identity is invalid.");
        }
        var bundlePath = Path.Combine(directory, CalibrationLibraryEvidenceNames.BundleEnvelope);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, bundlePath);
        var bundleInfo = new FileInfo(bundlePath);
        if (!bundleInfo.Exists || bundleInfo.Length > CalibrationLibraryContractJson.MaximumBundleBytes)
        {
            throw new InvalidDataException("The canonical synthetic calibration bundle is missing or oversized.");
        }
        var parsed = CalibrationLibraryContractJson.Parse(
            await File.ReadAllBytesAsync(bundlePath, cancellationToken).ConfigureAwait(false));
        var bundle = parsed.Value ?? throw new InvalidDataException(
            $"The canonical synthetic calibration bundle is invalid ({parsed.Validation.FieldPath}).");
        var relativeDirectory = Normalize(Path.GetRelativePath(_root, directory));
        var expectedProfilePath = $"{relativeDirectory}/{CalibrationLibraryEvidenceNames.ProfileMarker}";
        if (!string.Equals(bundle.Source, CalibrationLibraryBundleSources.SyntheticReferencesV1, StringComparison.Ordinal) ||
            !string.Equals(bundle.BundleId, $"synthetic-{modelIdentity[..32].ToUpperInvariant()}", StringComparison.Ordinal) ||
            !string.Equals(bundle.ProfileRelativePath, expectedProfilePath, StringComparison.Ordinal) ||
            bundle.Artifacts.Any(artifact => !string.Equals(
                Normalize(Path.GetDirectoryName(artifact.ManifestRelativePath) ?? string.Empty),
                relativeDirectory,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The canonical synthetic calibration bundle conflicts with its directory identity.");
        }
        return bundle;
    }

    private void MovePlannedQuarantine(CalibrationReconciliationOperation operation)
    {
        if (operation.QuarantineRelativePath is null)
        {
            throw new InvalidDataException("A planned calibration quarantine has no destination.");
        }
        var source = ResolveSafePath(operation.SourceRelativePath);
        var destination = ResolveSafePath(operation.QuarantineRelativePath);
        if (!Directory.Exists(source))
        {
            if (Directory.Exists(destination))
            {
                return;
            }
            throw new IOException("Planned calibration quarantine evidence is missing.");
        }
        if (Directory.Exists(destination))
        {
            throw new IOException("Planned calibration quarantine destination already exists.");
        }
        var parent = Path.GetDirectoryName(destination)!;
        var parentExisted = Directory.Exists(parent);
        Directory.CreateDirectory(parent);
        if (!parentExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(_root, parent);
        }
        Directory.Move(source, destination);
        RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(source)!);
        RawIngressFileStore.SyncDirectory(parent);
    }

    private async Task<(string EvidenceKey, long ObservedBytes)> ComputeEvidenceIdentityAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var identities = new List<EvidenceFileIdentity>(files.Length);
        long observedBytes = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
            var before = new FileInfo(path);
            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string sha256;
            await using (stream.ConfigureAwait(false))
            {
                sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            var after = new FileInfo(path);
            if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                throw new IOException("Calibration evidence changed while reconciliation inspected it.");
            }
            observedBytes = checked(observedBytes + after.Length);
            identities.Add(new EvidenceFileIdentity(
                Normalize(Path.GetRelativePath(directory, path)),
                after.Length,
                sha256));
        }
        var evidenceKey = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Directory = Path.GetFileName(directory),
            Files = identities
        });
        return (evidenceKey, observedBytes);
    }

    private static bool IsExpectedReconciliationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException or CryptographicException;

    private string ResolveSafePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Calibration reconciliation path escapes CameraAgent storage.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }

    private static string Normalize(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record EvidenceFileIdentity(string Path, long Length, string Sha256);
}
