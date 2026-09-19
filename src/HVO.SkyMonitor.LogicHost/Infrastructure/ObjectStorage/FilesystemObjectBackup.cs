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
    private const string RestoringSuffix = ".restoring";
    private const string ReplacedSuffix = ".replaced";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<FilesystemObjectBackupInventory> BackupAsync(
        string sourceRoot, IReadOnlyList<string> buckets, string backupPath, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
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
            foreach (var descriptorPath in EnumerateDescriptors(bucketPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyHash = Path.GetFileName(descriptorPath)[..^FilesystemObjectLayout.DescriptorSuffix.Length];
                var descriptor = ReadDescriptor(descriptorPath)
                    ?? throw new InvalidOperationException($"A descriptor in bucket '{bucket}' is malformed; reconcile the store before backing it up.");
                if (!string.Equals(FilesystemObjectLayout.KeyHash(descriptor.Key), keyHash, StringComparison.Ordinal))
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
                File.Copy(descriptorPath, target.Resolve(FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash)), overwrite: false);
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
        await File.WriteAllTextAsync(Path.Combine(backupPath, InventoryChecksumFileName), Convert.ToHexStringLower(SHA256.HashData(inventoryBytes)) + "  " + InventoryFileName + "\n", cancellationToken).ConfigureAwait(false);
        DurableSync.File(inventoryPath);
        DurableSync.Directory(backupPath);
        return inventory;
    }

    public static async Task<FilesystemObjectBackupInventory> RestoreAsync(
        string backupPath, string targetRoot, IReadOnlyList<string> buckets, CancellationToken cancellationToken)
    {
        var inventory = await ReadVerifiedInventoryAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var missing = buckets.Where(b => !inventory.Buckets.Contains(b, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The backup does not contain configured bucket(s) {string.Join(", ", missing)}; refusing a restore that would leave them empty.");
        }
        Directory.CreateDirectory(targetRoot);
        var root = PhysicalRoot.Open(targetRoot);
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
                    || !string.Equals(descriptor.Sha256, entry.Sha256, StringComparison.Ordinal) || descriptor.Length != entry.Length)
                {
                    throw new InvalidOperationException($"Backup descriptor for an object in bucket '{bucket}' does not match the inventory; the backup is damaged and nothing has been restored.");
                }
                File.Copy(source.Resolve(descriptorRelative), stagedDescriptor, overwrite: false);
            }
            DurableSync.DirectoryChain(root, staging);
            staged.Add((bucket, staging, final, replaced));
        }

        // Swap. Each bucket's swap is two renames; the previous bucket is intact until the
        // second rename and removed only after the restored one is in place.
        foreach (var (bucket, staging, final, replaced) in staged)
        {
            if (Directory.Exists(final))
            {
                RefuseLink(final, bucket);
                Directory.Move(final, replaced);
            }
            Directory.Move(staging, final);
            DurableSync.Directory(targetRoot);
            if (Directory.Exists(replaced))
            {
                Directory.Delete(replaced, recursive: true);
                DurableSync.Directory(targetRoot);
            }
        }
        return inventory;
    }

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
        foreach (var entry in inventory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyHash = FilesystemObjectLayout.KeyHash(entry.Key);
            var descriptor = ReadDescriptor(physical.Resolve(FilesystemObjectLayout.DescriptorRelativePath(entry.Bucket, keyHash)));
            if (descriptor is null || !descriptor.IsWellFormed(entry.Key) || descriptor.Generation != entry.Generation || descriptor.Sha256 != entry.Sha256 || descriptor.Length != entry.Length)
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
        var inventory = JsonSerializer.Deserialize<FilesystemObjectBackupInventory>(bytes, Json)
            ?? throw new InvalidOperationException("The backup inventory is empty.");
        if (!string.Equals(inventory.Schema, InventorySchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The backup inventory schema '{inventory.Schema}' is not supported.");
        }
        if (inventory.ObjectCount != inventory.Entries.Count)
        {
            throw new InvalidOperationException("The backup inventory's object count does not match its entries.");
        }
        return inventory;
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
