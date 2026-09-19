using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

/// <summary>
/// Backup inventory and destructive restore for the filesystem object store. Both run offline,
/// against the configured root, with the host stopped; neither needs SQL Server, Redis, or the
/// application container. A backup is the set of live objects (descriptor plus the data
/// generation it names) copied in the store's own layout, with every data file hashed as it
/// is copied and refused on mismatch, and an inventory that records each object's logical key,
/// content type, length, generation, SHA-256 and modified time. Retired generations, temporaries,
/// retirement stamps and quarantine are not part of a backup: they are not objects.
/// </summary>
/// <remarks>
/// Restore is destructive by contract and staged for safety: each bucket is rebuilt in full
/// under <c>&lt;bucket&gt;.restoring</c>, with every file re-hashed against the inventory, and only
/// then swapped into place through <c>&lt;bucket&gt;.replaced</c>. A failure at any point leaves
/// either the previous bucket or the restored one whole; there is no state in which a bucket
/// is a mixture of the two.
/// </remarks>
internal static class FilesystemObjectBackup
{
    internal const string InventoryFileName = "inventory.json";
    internal const string InventoryChecksumFileName = "inventory.json.sha256";
    internal const string InventorySchema = "hvo-fs-object-backup-v1";
    internal const string RestoreMarkerFileName = ".object-store-restore.json";
    internal const string RestoreLockFileName = ".object-store-runtime.lock";
    private const string RestoringSuffix = ".restoring";
    private const string ReplacedSuffix = ".replaced";
    private const string RestoreMarkerSchema = "hvo-fs-object-restore-v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<FilesystemObjectBackupInventory> BackupAsync(
        string sourceRoot, IReadOnlyList<string> buckets, string backupPath, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ValidateSeparateRoots(sourceRoot, backupPath);
        ValidateBuckets(buckets);
        var root = PhysicalRoot.Open(sourceRoot);
        if (Directory.Exists(backupPath) && Directory.EnumerateFileSystemEntries(backupPath).Any())
        {
            throw new InvalidOperationException("The backup path must be an empty or absent directory; a backup never merges into an existing one.");
        }
        Directory.CreateDirectory(backupPath);
        var target = PhysicalRoot.Open(backupPath);
        var entries = new List<FilesystemObjectBackupEntry>();
        foreach (var bucket in buckets)
        {
            var bucketPath = root.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket));
            if (!Directory.Exists(bucketPath) || new DirectoryInfo(bucketPath).LinkTarget is not null)
            {
                throw new InvalidOperationException($"The configured bucket '{bucket}' is absent from the object-store root; refusing to back up a partial set.");
            }
            AtomicPublisher.EnsureDirectory(target, target.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket)));
            foreach (var descriptorPath in EnumerateDescriptors(bucketPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyHash = Path.GetFileName(descriptorPath)[..^FilesystemObjectLayout.DescriptorSuffix.Length];
                var descriptor = ReadDescriptor(descriptorPath)
                    ?? throw new InvalidOperationException($"A descriptor in bucket '{bucket}' is malformed; reconcile the store before backing it up.");
                if (descriptor.Key is null || !descriptor.IsWellFormed(descriptor.Key)
                    || !string.Equals(FilesystemObjectLayout.KeyHash(descriptor.Key), keyHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"A descriptor in bucket '{bucket}' does not name the key it is filed under; reconcile the store before backing it up.");
                }
                var dataRelative = FilesystemObjectLayout.DataRelativePath(bucket, keyHash, descriptor.Generation);
                var dataPath = root.Resolve(dataRelative);
                var targetData = target.Resolve(dataRelative);
                AtomicPublisher.EnsureDirectory(target, Path.GetDirectoryName(targetData)!);
                var (length, sha256) = await CopyHashingAsync(dataPath, targetData, cancellationToken).ConfigureAwait(false);
                if (length != descriptor.Length || !string.Equals(sha256, descriptor.Sha256, StringComparison.Ordinal))
                {
                    File.Delete(targetData);
                    throw new InvalidOperationException($"The data for an object in bucket '{bucket}' does not match its descriptor (length or digest); the store is corrupt and must be reconciled before a backup is taken.");
                }
                var targetDescriptor = target.Resolve(FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash));
                File.Copy(descriptorPath, targetDescriptor, overwrite: false);
                DurableSync.File(targetDescriptor);
                DurableSync.DirectoryChain(target, Path.GetDirectoryName(targetDescriptor)!);
                entries.Add(new FilesystemObjectBackupEntry(bucket, descriptor.Key, descriptor.ContentType, descriptor.Length, descriptor.Sha256, descriptor.Generation, descriptor.ModifiedUtc));
            }
        }
        entries.Sort(static (a, b) =>
        {
            var byBucket = string.CompareOrdinal(a.Bucket, b.Bucket);
            return byBucket != 0 ? byBucket : string.CompareOrdinal(a.Key, b.Key);
        });
        var inventory = new FilesystemObjectBackupInventory(InventorySchema, timeProvider.GetUtcNow(), buckets.ToArray(), entries.Count, entries.Sum(static e => e.Length), entries);
        var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, Json);
        var inventoryPath = Path.Combine(backupPath, InventoryFileName);
        await File.WriteAllBytesAsync(inventoryPath, inventoryBytes, cancellationToken).ConfigureAwait(false);
        DurableSync.File(inventoryPath);
        DurableSync.Directory(backupPath);
        // Publish the completion checksum only after the inventory and object tree are durable.
        var checksumPath = Path.Combine(backupPath, InventoryChecksumFileName);
        await File.WriteAllTextAsync(checksumPath, Convert.ToHexStringLower(SHA256.HashData(inventoryBytes)) + "  " + InventoryFileName + "\n", cancellationToken).ConfigureAwait(false);
        DurableSync.File(checksumPath);
        DurableSync.Directory(backupPath);
        return inventory;
    }

    public static async Task<FilesystemObjectBackupInventory> RestoreAsync(
        string backupPath, string targetRoot, IReadOnlyList<string> buckets, CancellationToken cancellationToken)
    {
        ValidateSeparateRoots(backupPath, targetRoot);
        ValidateBuckets(buckets);
        if (!AtomicPublisher.SupportsAtomicReplace || !DurableSync.SupportsDirectorySync)
        {
            throw new InvalidOperationException("Destructive filesystem object-store restore requires Linux atomic replace and directory synchronization.");
        }
        var inventory = await ReadVerifiedInventoryAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var missing = buckets.Where(b => !inventory.Buckets.Contains(b, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The backup does not contain configured bucket(s) {string.Join(", ", missing)}; refusing a restore that would leave them empty.");
        }
        Directory.CreateDirectory(targetRoot);
        var root = PhysicalRoot.Open(targetRoot);
        await using var restoreLock = AcquireExclusiveRestoreLock(root);
        RecoverInterruptedRestore(root, buckets);
        var source = PhysicalRoot.Open(backupPath);

        // Stage every bucket completely before any swap, so a bad backup is discovered
        // before the first existing bucket is touched.
        var staged = new List<(string Bucket, string Staging, string Final, string Replaced)>();
        foreach (var bucket in buckets)
        {
            var staging = root.Resolve(bucket + RestoringSuffix);
            var replaced = root.Resolve(bucket + ReplacedSuffix);
            var final = root.Resolve(bucket);
            if (Directory.Exists(replaced))
            {
                // An earlier restore was interrupted after it had moved the previous bucket
                // aside. If the bucket itself is now absent, the interruption fell inside the
                // swap window and the previous bucket is the only copy: put it back rather
                // than refuse, so the store is whole again and the operator can retry.
                if (!Directory.Exists(final))
                {
                    RefuseLink(replaced, bucket + ReplacedSuffix);
                    Directory.Move(replaced, final);
                    DurableSync.Directory(targetRoot);
                }
                else
                {
                    throw new InvalidOperationException($"'{bucket}{ReplacedSuffix}' exists beside a live '{bucket}' from an earlier interrupted restore; the live bucket is the restored one. Inspect '{bucket}{ReplacedSuffix}' and remove it before restoring again.");
                }
            }
            if (Directory.Exists(staging))
            {
                RefuseLink(staging, bucket + RestoringSuffix);
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows())
            {
                var mode = Directory.Exists(final)
                    ? File.GetUnixFileMode(final)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
                File.SetUnixFileMode(staging, mode);
            }
            foreach (var entry in inventory.Entries.Where(e => string.Equals(e.Bucket, bucket, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyHash = FilesystemObjectLayout.KeyHash(entry.Key);
                var dataRelative = FilesystemObjectLayout.DataRelativePath(bucket, keyHash, entry.Generation);
                var descriptorRelative = FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash);
                var stagedData = Path.Combine(staging, Path.GetRelativePath(bucket, dataRelative));
                var stagedDescriptor = Path.Combine(staging, Path.GetRelativePath(bucket, descriptorRelative));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedData)!);
                var (length, sha256) = await CopyHashingAsync(source.Resolve(dataRelative), stagedData, cancellationToken).ConfigureAwait(false);
                if (length != entry.Length || !string.Equals(sha256, entry.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Backup data for an object in bucket '{bucket}' does not match the inventory; the backup is damaged and nothing has been restored.");
                }
                var descriptor = ReadDescriptor(source.Resolve(descriptorRelative));
                if (descriptor is null || !descriptor.IsWellFormed(entry.Key) || !string.Equals(descriptor.Generation, entry.Generation, StringComparison.Ordinal)
                    || !string.Equals(descriptor.Sha256, entry.Sha256, StringComparison.Ordinal) || descriptor.Length != entry.Length
                    || descriptor.ContentType != entry.ContentType || !descriptor.ModifiedUtc.EqualsExact(entry.ModifiedUtc))
                {
                    throw new InvalidOperationException($"Backup descriptor for an object in bucket '{bucket}' does not match the inventory; the backup is damaged and nothing has been restored.");
                }
                File.Copy(source.Resolve(descriptorRelative), stagedDescriptor, overwrite: false);
                DurableSync.File(stagedDescriptor);
                DurableSync.DirectoryChain(root, Path.GetDirectoryName(stagedDescriptor)!);
            }
            DurableSync.DirectoryChain(root, staging);
            staged.Add((bucket, staging, final, replaced));
        }

        var marker = new FilesystemObjectRestoreMarker(
            RestoreMarkerSchema,
            Guid.NewGuid(),
            "prepared",
            buckets.Select(bucket => new FilesystemObjectRestoreBucket(
                bucket,
                Directory.Exists(root.Resolve(bucket)))).ToArray());
        await PublishRestoreMarkerAsync(root, marker, PublishMode.CreateNew, cancellationToken).ConfigureAwait(false);

        // Swap. Each bucket's swap is two renames; the previous bucket is intact until the
        // second rename. No previous bucket is removed until every restored bucket verifies
        // and the durable marker advances to committed.
        foreach (var (bucket, staging, final, replaced) in staged)
        {
            if (Directory.Exists(final))
            {
                RefuseLink(final, bucket);
                Directory.Move(final, replaced);
                DurableSync.Directory(targetRoot);
            }
            Directory.Move(staging, final);
            DurableSync.Directory(targetRoot);
        }
        var mismatches = await VerifyAsync(backupPath, targetRoot, cancellationToken).ConfigureAwait(false);
        if (mismatches != 0)
        {
            throw new InvalidOperationException($"The restored object store has {mismatches} inventory mismatch(es); the restore remains fenced for rollback.");
        }
        await PublishRestoreMarkerAsync(root, marker with { Phase = "committed" }, PublishMode.Replace, cancellationToken).ConfigureAwait(false);
        CompleteCommittedRestore(root, marker.Buckets);
        return inventory;
    }

    internal static bool HasUnresolvedRestore(PhysicalRoot root)
    {
        var markerPath = root.Resolve(RestoreMarkerFileName);
        if (new FileInfo(markerPath).LinkTarget is not null)
        {
            throw new InvalidOperationException("The reserved object-store restore marker path is a link; runtime remains fenced.");
        }
        try
        {
            var attributes = File.GetAttributes(markerPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException("The reserved object-store restore marker path is not a regular file; runtime remains fenced.");
            }
            if (!File.Exists(markerPath))
            {
                throw new InvalidOperationException("The reserved object-store restore marker path is a non-regular filesystem entry; runtime remains fenced.");
            }
            DurableSync.RequireRegularFile(markerPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The object-store restore marker path cannot be inspected; runtime remains fenced.", exception);
        }
    }

    internal static FileStream AcquireRuntimeLock(PhysicalRoot root)
        => AcquireLock(root, FileShare.None, "Another LogicHost or destructive restore already owns the filesystem object store.");

    private static FileStream AcquireExclusiveRestoreLock(PhysicalRoot root)
        => AcquireLock(root, FileShare.None, "LogicHost is running or another destructive restore already owns the filesystem object store.");

    private static FileStream AcquireLock(PhysicalRoot root, FileShare share, string message)
    {
        var path = root.Resolve(RestoreLockFileName);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share, 1, FileOptions.WriteThrough);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(message, exception);
        }
    }

    internal static void RecoverInterruptedRestore(PhysicalRoot root, IReadOnlyList<string> configuredBuckets)
    {
        var markerPath = root.Resolve(RestoreMarkerFileName);
        if (!HasUnresolvedRestore(root))
        {
            return;
        }
        FilesystemObjectRestoreMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<FilesystemObjectRestoreMarker>(File.ReadAllBytes(markerPath), Json)
                ?? throw new InvalidOperationException("The object-store restore marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The object-store restore marker is malformed; runtime remains fenced.", exception);
        }
        if (marker.Schema != RestoreMarkerSchema || marker.Buckets is null
            || marker.Buckets.Count != configuredBuckets.Count
            || marker.Buckets.Any(bucket => bucket is null || !configuredBuckets.Contains(bucket.Name, StringComparer.Ordinal))
            || marker.Buckets.Select(bucket => bucket.Name).Distinct(StringComparer.Ordinal).Count() != marker.Buckets.Count)
        {
            throw new InvalidOperationException("The object-store restore marker does not match the configured buckets; runtime remains fenced.");
        }
        if (marker.Phase == "committed")
        {
            if (marker.Buckets.Any(bucket => !InspectDirectoryEntry(root.Resolve(bucket.Name), bucket.Name)))
            {
                throw new InvalidOperationException("A committed object-store restore is missing a live bucket; runtime remains fenced.");
            }
            if (marker.Buckets.Any(bucket => !bucket.HadPreviousBucket && InspectDirectoryEntry(root.Resolve(bucket.Name + ReplacedSuffix), bucket.Name + ReplacedSuffix)))
            {
                throw new InvalidOperationException("A committed object-store restore has a rollback bucket it did not record; runtime remains fenced.");
            }
            foreach (var bucket in marker.Buckets)
            {
                var replaced = root.Resolve(bucket.Name + ReplacedSuffix);
                var staging = root.Resolve(bucket.Name + RestoringSuffix);
                if (InspectDirectoryEntry(replaced, bucket.Name + ReplacedSuffix))
                {
                    RefuseLink(replaced, bucket.Name + ReplacedSuffix);
                }
                if (InspectDirectoryEntry(staging, bucket.Name + RestoringSuffix))
                {
                    RefuseLink(staging, bucket.Name + RestoringSuffix);
                }
            }
            CompleteCommittedRestore(root, marker.Buckets);
            return;
        }
        if (marker.Phase != "prepared")
        {
            throw new InvalidOperationException("The object-store restore marker has an unknown phase; runtime remains fenced.");
        }
        // Validate every bucket before changing any bucket, so a contradiction in a later
        // bucket cannot leave an earlier one partially recovered.
        foreach (var bucket in marker.Buckets)
        {
            var final = root.Resolve(bucket.Name);
            var replaced = root.Resolve(bucket.Name + ReplacedSuffix);
            var staging = root.Resolve(bucket.Name + RestoringSuffix);
            var hasFinal = InspectDirectoryEntry(final, bucket.Name);
            var hasReplaced = InspectDirectoryEntry(replaced, bucket.Name + ReplacedSuffix);
            if (hasFinal)
            {
                RefuseLink(final, bucket.Name);
            }
            if (hasReplaced)
            {
                RefuseLink(replaced, bucket.Name + ReplacedSuffix);
            }
            if (InspectDirectoryEntry(staging, bucket.Name + RestoringSuffix))
            {
                RefuseLink(staging, bucket.Name + RestoringSuffix);
            }
            if (hasReplaced && !bucket.HadPreviousBucket || bucket.HadPreviousBucket && !hasFinal && !hasReplaced)
            {
                throw new InvalidOperationException("The object-store restore recovery state contradicts the recorded previous buckets; runtime remains fenced.");
            }
        }
        foreach (var bucket in marker.Buckets)
        {
            var final = root.Resolve(bucket.Name);
            var replaced = root.Resolve(bucket.Name + ReplacedSuffix);
            var staging = root.Resolve(bucket.Name + RestoringSuffix);
            if (Directory.Exists(replaced))
            {
                RefuseLink(replaced, bucket.Name + ReplacedSuffix);
                if (Directory.Exists(final))
                {
                    RefuseLink(final, bucket.Name);
                    Directory.Delete(final, recursive: true);
                    DurableSync.Directory(root.Path);
                }
                Directory.Move(replaced, final);
                DurableSync.Directory(root.Path);
            }
            else if (bucket.HadPreviousBucket)
            {
                RefuseLink(final, bucket.Name);
            }
            else if (!bucket.HadPreviousBucket && Directory.Exists(final))
            {
                RefuseLink(final, bucket.Name);
                Directory.Delete(final, recursive: true);
                DurableSync.Directory(root.Path);
            }
            if (Directory.Exists(staging))
            {
                RefuseLink(staging, bucket.Name + RestoringSuffix);
                Directory.Delete(staging, recursive: true);
                DurableSync.Directory(root.Path);
            }
        }
        DeleteRestoreMarker(root, markerPath);
    }

    private static void CompleteCommittedRestore(PhysicalRoot root, IReadOnlyList<FilesystemObjectRestoreBucket> buckets)
    {
        foreach (var bucket in buckets)
        {
            var replaced = root.Resolve(bucket.Name + ReplacedSuffix);
            var staging = root.Resolve(bucket.Name + RestoringSuffix);
            foreach (var path in new[] { replaced, staging })
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }
                RefuseLink(path, Path.GetFileName(path));
                Directory.Delete(path, recursive: true);
                DurableSync.Directory(root.Path);
            }
        }
        DeleteRestoreMarker(root, root.Resolve(RestoreMarkerFileName));
    }

    private static bool InspectDirectoryEntry(string path, string name)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"'{name}' is not a regular directory; runtime remains fenced.");
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"'{name}' cannot be inspected; runtime remains fenced.", exception);
        }
    }

    private static void DeleteRestoreMarker(PhysicalRoot root, string markerPath)
    {
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            DurableSync.Directory(root.Path);
        }
    }

    private static async Task PublishRestoreMarkerAsync(
        PhysicalRoot root,
        FilesystemObjectRestoreMarker marker,
        PublishMode mode,
        CancellationToken cancellationToken)
        => _ = await AtomicPublisher.PublishAsync(
            root,
            RestoreMarkerFileName,
            mode,
            (stream, token) => JsonSerializer.SerializeAsync(stream, marker, Json, token),
            cancellationToken).ConfigureAwait(false);

    private static void RefuseLink(string path, string name)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new InvalidOperationException($"'{name}' is a link; refusing to touch it.");
        }
    }

    /// <summary>Re-hash every restored or live object against the inventory. Used after a restore and as an operator audit.</summary>
    public static async Task<int> VerifyAsync(string backupPath, string root, CancellationToken cancellationToken)
    {
        var inventory = await ReadVerifiedInventoryAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var physical = PhysicalRoot.Open(root);
        var mismatches = 0;
        var expectedDescriptors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in inventory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyHash = FilesystemObjectLayout.KeyHash(entry.Key);
            var descriptorPath = physical.Resolve(FilesystemObjectLayout.DescriptorRelativePath(entry.Bucket, keyHash));
            expectedDescriptors.Add(descriptorPath);
            var descriptor = ReadDescriptor(descriptorPath);
            if (descriptor is null || !descriptor.IsWellFormed(entry.Key) || descriptor.Generation != entry.Generation || descriptor.Sha256 != entry.Sha256 || descriptor.Length != entry.Length
                || descriptor.ContentType != entry.ContentType || !descriptor.ModifiedUtc.EqualsExact(entry.ModifiedUtc))
            {
                mismatches++;
                continue;
            }
            var dataPath = physical.Resolve(FilesystemObjectLayout.DataRelativePath(entry.Bucket, keyHash, entry.Generation));
            if (!File.Exists(dataPath))
            {
                mismatches++;
                continue;
            }
            var (length, sha256) = await HashAsync(dataPath, cancellationToken).ConfigureAwait(false);
            if (length != entry.Length || !string.Equals(sha256, entry.Sha256, StringComparison.Ordinal))
            {
                mismatches++;
            }
        }
        foreach (var bucket in inventory.Buckets)
        {
            var bucketPath = physical.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket));
            if (!Directory.Exists(bucketPath))
            {
                mismatches++;
                continue;
            }
            foreach (var descriptorPath in EnumerateDescriptors(bucketPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expectedDescriptors.Contains(descriptorPath))
                {
                    mismatches++;
                }
            }
        }
        return mismatches;
    }

    internal static async Task<FilesystemObjectBackupInventory> ReadVerifiedInventoryAsync(string backupPath, CancellationToken cancellationToken)
    {
        var inventoryPath = Path.Combine(backupPath, InventoryFileName);
        var checksumPath = Path.Combine(backupPath, InventoryChecksumFileName);
        if (!File.Exists(inventoryPath) || !File.Exists(checksumPath))
        {
            throw new InvalidOperationException("The backup has no inventory or no inventory checksum; it is not a filesystem object-store backup.");
        }
        var bytes = await File.ReadAllBytesAsync(inventoryPath, cancellationToken).ConfigureAwait(false);
        var recorded = (await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false)).Split(' ', 2)[0].Trim();
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), recorded, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup inventory does not match its checksum; the backup is damaged.");
        }
        FilesystemObjectBackupInventory inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<FilesystemObjectBackupInventory>(bytes, Json)
                ?? throw new InvalidOperationException("The backup inventory is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The backup inventory is malformed.", exception);
        }
        if (!string.Equals(inventory.Schema, InventorySchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The backup inventory schema '{inventory.Schema}' is not supported.");
        }
        ValidateBuckets(inventory.Buckets);
        if (inventory.Entries is null || inventory.ObjectCount != inventory.Entries.Count || inventory.TotalBytes < 0)
        {
            throw new InvalidOperationException("The backup inventory's object count does not match its entries.");
        }
        var identities = new HashSet<(string Bucket, string Key)>();
        long totalBytes = 0;
        foreach (var entry in inventory.Entries)
        {
            if (entry is null || entry.Bucket is null || entry.Key is null
                || !inventory.Buckets.Contains(entry.Bucket, StringComparer.Ordinal)
                || !identities.Add((entry.Bucket, entry.Key))
                || string.IsNullOrEmpty(entry.ContentType) || entry.Length < 0
                || !FilesystemObjectLayout.IsGeneration(entry.Generation)
                || entry.Sha256 is not { Length: 64 }
                || !entry.Sha256.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                || entry.Length > long.MaxValue - totalBytes)
            {
                throw new InvalidOperationException("The backup inventory contains an invalid or duplicate object entry.");
            }
            totalBytes += entry.Length;
        }
        if (totalBytes != inventory.TotalBytes)
        {
            throw new InvalidOperationException("The backup inventory's total bytes do not match its entries.");
        }
        return inventory;
    }

    private static void ValidateBuckets(IReadOnlyList<string>? buckets)
    {
        if (buckets is null || buckets.Count == 0)
        {
            throw new InvalidOperationException("At least one valid bucket is required.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            if (bucket is null || bucket.Length is < 3 or > 63
                || !char.IsAsciiLetterOrDigit(bucket[0]) || !char.IsAsciiLetterOrDigit(bucket[^1])
                || !bucket.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')
                || !names.Add(bucket))
            {
                throw new InvalidOperationException("Bucket names must be valid, distinct path segments.");
            }
        }
        foreach (var bucket in buckets)
        {
            if (names.Contains(bucket + RestoringSuffix) || names.Contains(bucket + ReplacedSuffix))
            {
                throw new InvalidOperationException("Bucket names must not overlap restore staging or recovery names.");
            }
        }
    }

    private static void ValidateSeparateRoots(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        // Refuse links in ancestors too: lexical disjointness is insufficient through an alias.
        foreach (var path in new[] { source, destination })
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            {
                if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                {
                    throw new InvalidOperationException("Backup and restore roots must not resolve through links.");
                }
            }
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison)
            || source.StartsWith(Path.EndsInDirectorySeparator(destination) ? destination : destination + Path.DirectorySeparatorChar, comparison)
            || destination.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Backup and object-store roots must not overlap.");
        }
    }

    private static IEnumerable<string> EnumerateDescriptors(string bucketPath)
    {
        var quarantine = Path.DirectorySeparatorChar + FilesystemObjectReconciler.QuarantineDirectoryName + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(bucketPath, "*" + FilesystemObjectLayout.DescriptorSuffix, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }).Where(path => !path.Contains(quarantine, StringComparison.Ordinal));
    }

    private static FilesystemObjectDescriptor? ReadDescriptor(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                return null;
            }
            return JsonSerializer.Deserialize<FilesystemObjectDescriptor>(File.ReadAllBytes(path), FilesystemObjectLayout.DescriptorJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(long Length, string Sha256)> CopyHashingAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        if (new FileInfo(sourcePath).LinkTarget is not null)
        {
            throw new InvalidOperationException("A data file is a link; refusing to follow it.");
        }
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length += read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        DurableSync.File(output.SafeFileHandle, targetPath);
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task<(long Length, string Sha256)> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

internal sealed record FilesystemObjectBackupInventory(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("buckets")] IReadOnlyList<string> Buckets,
    [property: JsonPropertyName("objectCount")] int ObjectCount,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("entries")] IReadOnlyList<FilesystemObjectBackupEntry> Entries);

internal sealed record FilesystemObjectBackupEntry(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("generation")] string Generation,
    [property: JsonPropertyName("modifiedUtc")] DateTimeOffset ModifiedUtc);

internal sealed record FilesystemObjectRestoreMarker(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("buckets")] IReadOnlyList<FilesystemObjectRestoreBucket> Buckets);

internal sealed record FilesystemObjectRestoreBucket(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("hadPreviousBucket")] bool HadPreviousBucket);
