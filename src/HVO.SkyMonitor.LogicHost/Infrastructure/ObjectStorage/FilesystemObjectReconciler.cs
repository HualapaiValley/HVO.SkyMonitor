using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

/// <summary>
/// Restores a bucket's physical state to one the provider's invariants describe, after a
/// crash or an interrupted publication, without inventing successful state. It never writes
/// a descriptor. It removes only what the invariants prove is unreferenced (temporaries,
/// retired data generations past a grace age), and it moves what contradicts itself
/// (descriptor without data, data not matching its descriptor) into a quarantine directory
/// the operator owns, so the store stops serving it and nothing is destroyed.
/// </summary>
/// <remarks>
/// <para>The grace age exists for open readers: a data file retired by replacement or delete
/// may still be held by a reader that opened it before the descriptor moved, and POSIX keeps
/// the bytes alive for that reader after unlink. The age bounds how long reclamation waits
/// so that readers finish naturally; it is not a lock. On Windows, where the unlink of an
/// open file is refused, a sharing violation is deferred, not treated as a fault.</para>
/// <para>Every pass is bounded by <see cref="FilesystemReconciliationOptions.MaximumEntriesPerPass"/>
/// so a large bucket is reconciled across passes rather than in one unbounded scan, and the
/// report carries what was left over so the backlog is visible.</para>
/// <para>Copy safety (deferred finding from #917): a copy reads the source descriptor and then
/// opens the source data by generation. Reclamation removes only data files older than the
/// grace age <em>and</em> not named by the current descriptor, so a data file a copy could
/// have opened after reading a fresh descriptor is never eligible in the same pass window.</para>
/// </remarks>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Reconciler logs through the LoggerMessage partials below.")]
internal sealed partial class FilesystemObjectReconciler(
    FilesystemObjectStore store,
    TimeProvider timeProvider,
    ILogger<FilesystemObjectReconciler> logger)
{
    internal const string QuarantineDirectoryName = ".quarantine";
    private readonly object _cursorLock = new();
    private readonly Dictionary<(string Bucket, FilesystemReconciliationPhase Phase), ReconciliationTraversalState> _traversals = [];
    private readonly Dictionary<string, HashSet<string>> _unresolvedFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _budgetOffsets = new(StringComparer.Ordinal);
    internal Action<string>? BeforeDirectoryEnumerationForTest { get; set; }

    /// <summary>
    /// Reconcile one bucket. Idempotent; a second pass on a clean bucket reports zeros.
    /// </summary>
    public FilesystemReconciliationReport Reconcile(string bucket, FilesystemReconciliationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = store.Root;
        var bucketPath = root.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket));
        if (!Directory.Exists(bucketPath) || new DirectoryInfo(bucketPath).LinkTarget is not null)
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, "reconcile");
        }
        var now = timeProvider.GetUtcNow();
        var report = new FilesystemReconciliationReport { Bucket = bucket, Phase = "All" };
        lock (_cursorLock)
        {
            _unresolvedFailures.TryAdd(bucket, []);
        }
        var phases = Enum.GetValues<FilesystemReconciliationPhase>();
        var baseBudget = options.MaximumEntriesPerPass / phases.Length;
        var remainder = options.MaximumEntriesPerPass % phases.Length;
        int budgetOffset;
        lock (_cursorLock)
        {
            budgetOffset = _budgetOffsets.GetValueOrDefault(bucket);
            _budgetOffsets[bucket] = (budgetOffset + Math.Max(1, remainder)) % phases.Length;
        }
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            var relativePhase = (phaseIndex - budgetOffset + phases.Length) % phases.Length;
            var budget = baseBudget + (relativePhase < remainder ? 1 : 0);
            if (budget == 0)
            {
                report.Truncated = true;
                continue;
            }
            var batch = EnumerateBatch(root, bucket, phase, bucketPath, budget);
            foreach (var path in batch.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.Verify(path, "reconcile-entry");
                switch (phase)
                {
                    case FilesystemReconciliationPhase.Descriptors:
                        ReconcileDescriptor(root, bucketPath, path, options, report, cancellationToken);
                        break;
                    case FilesystemReconciliationPhase.Data:
                        ReconcileData(root, bucketPath, path, options, report, now);
                        break;
                    case FilesystemReconciliationPhase.RetirementStamps:
                        if (!File.Exists(Path.ChangeExtension(path, FilesystemObjectLayout.DataSuffix)))
                        {
                            if (TryDelete(path, out _))
                            {
                                ResolveFailure(report, path);
                            }
                            else
                            {
                                RecordFailure(report, path);
                            }
                        }
                        break;
                    case FilesystemReconciliationPhase.Temporaries:
                        ReconcileTemporary(path, options, report, now);
                        break;
                }
            }
        }
        lock (_cursorLock)
        {
            var completed = phases.All(phase => _traversals.GetValueOrDefault((bucket, phase))?.Completed == true);
            report.Truncated = !completed;
            if (completed)
            {
                _unresolvedFailures[bucket].RemoveWhere(path => !File.Exists(path) && !Directory.Exists(path));
                foreach (var phase in phases)
                {
                    _traversals.Remove((bucket, phase));
                }
            }
        }
        report.Quarantined = CountQuarantined(bucketPath, report);
        lock (_cursorLock)
        {
            report.ReclaimFailed = _unresolvedFailures[bucket].Count;
        }

        if (report.Quarantined > 0 || report.ReclaimFailed > 0)
        {
            LogAttention(logger, bucket, report.Quarantined, report.ReclaimFailed, report.RetiredCount, report.RetiredBytes);
        }
        return report;
    }

    private int CountQuarantined(string bucketPath, FilesystemReconciliationReport report)
    {
        var quarantineDirectory = Path.Combine(bucketPath, QuarantineDirectoryName);
        try
        {
            var attributes = File.GetAttributes(quarantineDirectory);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                RecordFailure(report, quarantineDirectory);
                return 0;
            }
            // Health needs a persistent attention fact, not an unbounded exact inventory.
            var present = Directory.EnumerateFileSystemEntries(quarantineDirectory).Take(1).Any() ? 1 : 0;
            ResolveFailure(report, quarantineDirectory);
            return present;
        }
        catch (FileNotFoundException)
        {
            ResolveFailure(report, quarantineDirectory);
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            ResolveFailure(report, quarantineDirectory);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordFailure(report, quarantineDirectory);
            return 0;
        }
    }

    private void ReconcileDescriptor(
        PhysicalRoot root,
        string bucketPath,
        string descriptorPath,
        FilesystemReconciliationOptions options,
        FilesystemReconciliationReport report,
        CancellationToken cancellationToken)
    {
        var keyHash = Path.GetFileName(descriptorPath)[..^FilesystemObjectLayout.DescriptorSuffix.Length];
        var descriptorResult = ReadDescriptor(descriptorPath);
        if (descriptorResult.Status == DescriptorReadStatus.Unreadable)
        {
            RecordFailure(report, descriptorPath);
            return;
        }
        var descriptor = descriptorResult.Descriptor;
        ResolveFailure(report, descriptorPath);
        if (descriptor is null || !FilesystemObjectLayout.IsGeneration(descriptor.Generation) || descriptor.Schema != FilesystemObjectLayout.SchemaVersion
        || !string.Equals(FilesystemObjectLayout.KeyHash(descriptor.Key), keyHash, StringComparison.Ordinal))
        {
            // Malformed, or names a key whose hash is not this file's name: the descriptor
            // is not evidence of anything. Quarantine it; the key becomes MissingObject.
            Quarantine(root, bucketPath, descriptorPath, "malformed-descriptor", report);
            return;
        }
        var dataPath = Path.Combine(Path.GetDirectoryName(descriptorPath)!, keyHash + "." + descriptor.Generation + FilesystemObjectLayout.DataSuffix);
        if (!File.Exists(dataPath))
        {
            // Descriptor without its data: the commit happened but the bytes are gone.
            // The object cannot be served; quarantine the descriptor so reads say
            // MissingObject rather than CorruptState on every call.
            Quarantine(root, bucketPath, descriptorPath, "descriptor-without-data", report);
            return;
        }
        if (options.VerifyDigests)
        {
            var (length, digest) = HashFile(dataPath, cancellationToken);
            if (length != descriptor.Length || !string.Equals(digest, descriptor.Sha256, StringComparison.Ordinal))
            {
                // Bytes contradict the descriptor: local corruption. Quarantine both so the
                // pair stays together for the operator.
                Quarantine(root, bucketPath, dataPath, "data-digest-mismatch", report);
                Quarantine(root, bucketPath, descriptorPath, "data-digest-mismatch", report);
                return;
            }
            report.DigestsVerified++;
        }
        report.LiveObjects++;
    }

    private void ReconcileData(
        PhysicalRoot root,
        string bucketPath,
        string dataPath,
        FilesystemReconciliationOptions options,
        FilesystemReconciliationReport report,
        DateTimeOffset now)
    {
        var name = Path.GetFileName(dataPath)[..^FilesystemObjectLayout.DataSuffix.Length];
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot != 64 || name.Length != 64 + 1 + 32)
        {
            Quarantine(root, bucketPath, dataPath, "unrecognized-data-name", report);
            return;
        }
        var keyHash = name[..64];
        var generation = name[65..];
        var descriptorPath = Path.Combine(Path.GetDirectoryName(dataPath)!, keyHash + FilesystemObjectLayout.DescriptorSuffix);
        var descriptorResult = ReadDescriptor(descriptorPath);
        if (descriptorResult.Status == DescriptorReadStatus.Unreadable)
        {
            RecordFailure(report, descriptorPath);
            return;
        }
        if (descriptorResult.Status == DescriptorReadStatus.Invalid)
        {
            return;
        }
        var descriptor = descriptorResult.Descriptor;
        ResolveFailure(report, descriptorPath);
        if (descriptor is not null
        && descriptor.IsWellFormed(descriptor.Key)
        && string.Equals(FilesystemObjectLayout.KeyHash(descriptor.Key), keyHash, StringComparison.Ordinal)
            && string.Equals(descriptor.Generation, generation, StringComparison.Ordinal))
        {
            TryDelete(Path.ChangeExtension(dataPath, ".retired"), out _);
            ResolveFailure(report, dataPath);
            return;
        }
        var info = new FileInfo(dataPath);
        // The grace clock is the moment the file became retired, not the moment it was
        // written: an object written a week ago and replaced a second ago has a reader
        // window that opened a second ago. The store cannot know that moment on its own
        // (nothing is written when a descriptor moves on), so the first pass that finds a
        // file retired stamps it by touching its mtime, and later passes age it from there.
        // A file that was never touched is at most one cadence away from being stamped.
        var age = now - info.LastWriteTimeUtc;
        var retirementStamp = Path.ChangeExtension(dataPath, ".retired");
        if (!File.Exists(retirementStamp))
        {
            try
            {
                File.WriteAllBytes(retirementStamp, []);
                File.SetLastWriteTimeUtc(retirementStamp, now.UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RecordFailure(report, dataPath);
                return;
            }
            age = TimeSpan.Zero;
        }
        else
        {
            age = now - new FileInfo(retirementStamp).LastWriteTimeUtc;
        }
        report.RetiredBytes += info.Length;
        report.RetiredCount++;
        if (age < options.RetiredGraceAge)
        {
            report.RetiredWithinGrace++;
            report.OldestRetiredAge = Max(report.OldestRetiredAge, age);
            return;
        }
        if (TryDelete(dataPath, out var deferred))
        {
            TryDelete(retirementStamp, out _);
            ResolveFailure(report, dataPath);
            report.ReclaimedCount++;
            report.ReclaimedBytes += info.Length;
            report.RetiredBytes -= info.Length;
            report.RetiredCount--;
        }
        else if (deferred)
        {
            // Sharing violation (Windows): a reader holds it. Try next pass.
            report.ReclaimDeferred++;
            report.OldestRetiredAge = Max(report.OldestRetiredAge, age);
        }
        else
        {
            RecordFailure(report, dataPath);
        }
    }

    private void ReconcileTemporary(
        string temporaryPath,
        FilesystemReconciliationOptions options,
        FilesystemReconciliationReport report,
        DateTimeOffset now)
    {
        var info = new FileInfo(temporaryPath);
        if (now - info.LastWriteTimeUtc < options.TemporaryGraceAge)
        {
            report.TemporariesWithinGrace++;
            return;
        }
        if (TryDelete(temporaryPath, out _))
        {
            ResolveFailure(report, temporaryPath);
            report.TemporariesRemoved++;
        }
        else
        {
            RecordFailure(report, temporaryPath);
        }
    }

    private ReconciliationBatch EnumerateBatch(
        PhysicalRoot root,
        string bucket,
        FilesystemReconciliationPhase phase,
        string bucketPath,
        int maximum)
    {
        var paths = new List<string>(maximum);
        lock (_cursorLock)
        {
            var key = (bucket, phase);
            if (!_traversals.TryGetValue(key, out var state))
            {
                state = new(bucketPath);
                _traversals[key] = state;
            }
            if (state.Completed)
            {
                return new(paths, false);
            }
            var examined = 0;
            try
            {
                while (examined < maximum)
                {
                    if (state.Entries is null)
                    {
                        if (state.Directories.Count == 0)
                        {
                            state.Completed = true;
                            break;
                        }
                        examined++;
                        var directory = state.Directories.Dequeue();
                        BeforeDirectoryEnumerationForTest?.Invoke(directory);
                        root.Verify(directory, "reconcile-enumerate");
                        if (new DirectoryInfo(directory).LinkTarget is not null)
                        {
                            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "reconcile-enumerate", directory);
                        }
                        state.Entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                        if (examined >= maximum)
                        {
                            break;
                        }
                    }
                    if (!state.Entries.MoveNext())
                    {
                        state.Entries.Dispose();
                        state.Entries = null;
                        continue;
                    }
                    examined++;
                    var path = state.Entries.Current;
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!string.Equals(Path.GetFileName(path), QuarantineDirectoryName, StringComparison.Ordinal))
                        {
                            state.Directories.Enqueue(path);
                        }
                        continue;
                    }
                    if (Matches(phase, path))
                    {
                        paths.Add(path);
                    }
                }
            }
            catch
            {
                state.Dispose();
                _traversals.Remove(key);
                throw;
            }
            return new(paths, !state.Completed);
        }
    }

    private static bool Matches(FilesystemReconciliationPhase phase, string path) => phase switch
    {
        FilesystemReconciliationPhase.Descriptors => path.EndsWith(FilesystemObjectLayout.DescriptorSuffix, StringComparison.Ordinal),
        FilesystemReconciliationPhase.Data => path.EndsWith(FilesystemObjectLayout.DataSuffix, StringComparison.Ordinal),
        FilesystemReconciliationPhase.RetirementStamps => path.EndsWith(".retired", StringComparison.Ordinal),
        _ => path.EndsWith(".tmp", StringComparison.Ordinal)
    };

    private static DescriptorReadResult ReadDescriptor(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return new(DescriptorReadStatus.Invalid, null);
            }
            return new(
                DescriptorReadStatus.Valid,
                JsonSerializer.Deserialize<FilesystemObjectDescriptor>(File.ReadAllBytes(path), FilesystemObjectLayout.DescriptorJson));
        }
        catch (JsonException)
        {
            return new(DescriptorReadStatus.Invalid, null);
        }
        catch (FileNotFoundException)
        {
            return new(DescriptorReadStatus.Missing, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(DescriptorReadStatus.Unreadable, null);
        }
    }

    private void RecordFailure(FilesystemReconciliationReport report, string path)
    {
        lock (_cursorLock)
        {
            _unresolvedFailures[report.Bucket].Add(path);
        }
    }

    private void ResolveFailure(FilesystemReconciliationReport report, string path)
    {
        lock (_cursorLock)
        {
            _unresolvedFailures[report.Bucket].Remove(path);
        }
    }

    private static (long Length, string Sha256) HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private void Quarantine(PhysicalRoot root, string bucketPath, string path, string reason, FilesystemReconciliationReport report)
    {
        // Quarantine lives inside the bucket so it is on the same filesystem (rename, not copy)
        // and inside the root (containment). Names are prefixed with the reason and a stamp so
        // repeated quarantines of the same physical name never collide.
        var quarantineDirectory = Path.Combine(bucketPath, QuarantineDirectoryName);
        try
        {
            AtomicPublisher.EnsureDirectory(root, quarantineDirectory);
            var target = Path.Combine(quarantineDirectory, $"{reason}.{Guid.NewGuid():N}.{Path.GetFileName(path)}");
            root.Verify(path, "quarantine");
            File.Move(path, target, overwrite: false);
            DurableSync.Directory(Path.GetDirectoryName(path)!);
            DurableSync.Directory(quarantineDirectory);
            report.Quarantined++;
            report.QuarantinedThisPass++;
            report.QuarantineReasons[reason] = report.QuarantineReasons.GetValueOrDefault(reason) + 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileSystemFaultException)
        {
            // Could not move it aside; it stays where it is and the store keeps refusing it.
            RecordFailure(report, path);
            _ = exception;
        }
    }

    private static bool TryDelete(string path, out bool deferred)
    {
        deferred = false;
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xFFFF) == 32)
        {
            deferred = true; // ERROR_SHARING_VIOLATION: a reader holds the retired file
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    [LoggerMessage(2186, LogLevel.Warning,
        "Filesystem object store reconciliation of {Bucket} needs attention: quarantined={Quarantined} reclaimFailed={ReclaimFailed} retired={RetiredCount} retiredBytes={RetiredBytes}.")]
    private static partial void LogAttention(ILogger logger, string bucket, int quarantined, int reclaimFailed, int retiredCount, long retiredBytes);
}

/// <summary>Bounds and grace ages for one reconciliation pass.</summary>
internal sealed record FilesystemReconciliationOptions
{
    /// <summary>How long a retired data file is kept for readers that may still hold it.</summary>
    public TimeSpan RetiredGraceAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a temporary is presumed to be an in-flight write before it is garbage.</summary>
    public TimeSpan TemporaryGraceAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound on physical entries examined in one pass; the report says if it was hit.</summary>
    public int MaximumEntriesPerPass { get; init; } = 50_000;

    /// <summary>Whether to hash every live data file against its descriptor. Costs a full read of the bucket.</summary>
    public bool VerifyDigests { get; init; }
}

/// <summary>What one pass found and did. Counts only; never a key, a path, or a payload.</summary>
internal sealed class FilesystemReconciliationReport
{
    public string Bucket { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public int LiveObjects { get; set; }
    public int DigestsVerified { get; set; }
    public int Quarantined { get; set; }
    public int QuarantinedThisPass { get; set; }
    public Dictionary<string, int> QuarantineReasons { get; } = new(StringComparer.Ordinal);
    public int RetiredCount { get; set; }
    public long RetiredBytes { get; set; }
    public int RetiredWithinGrace { get; set; }
    public TimeSpan OldestRetiredAge { get; set; }
    public int ReclaimedCount { get; set; }
    public long ReclaimedBytes { get; set; }
    public int ReclaimDeferred { get; set; }
    public int ReclaimFailed { get; set; }
    public int TemporariesRemoved { get; set; }
    public int TemporariesWithinGrace { get; set; }
    public bool Truncated { get; set; }
    public string? FailureOutcome { get; set; }

    /// <summary>True when an operator must look: something was quarantined or could not be reclaimed.</summary>
    public bool NeedsAttention => Quarantined > 0 || ReclaimFailed > 0 || Truncated || FailureOutcome is not null;
}

internal enum FilesystemReconciliationPhase
{
    Descriptors,
    Data,
    RetirementStamps,
    Temporaries
}

internal sealed record ReconciliationBatch(IReadOnlyList<string> Paths, bool HasMore);

internal sealed class ReconciliationTraversalState : IDisposable
{
    public ReconciliationTraversalState(string root)
    {
        Directories.Enqueue(root);
    }

    public Queue<string> Directories { get; } = new();
    public IEnumerator<string>? Entries { get; set; }
    public bool Completed { get; set; }

    public void Dispose() => Entries?.Dispose();
}

internal enum DescriptorReadStatus
{
    Valid,
    Missing,
    Invalid,
    Unreadable
}

internal sealed record DescriptorReadResult(DescriptorReadStatus Status, FilesystemObjectDescriptor? Descriptor);
