using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

public sealed partial class SqliteOrbitalElementsStore
{
    private enum DeleteOutcome { Deleted, Absent, Refused }

    private static readonly string[] ContentDirectories =
        [RawDirectoryName, PayloadDirectoryName, ManifestDirectoryName, QuarantineDirectoryName, StagingDirectoryName];

    private PhysicalRoot RequireRoot()
        => _root ?? throw new InvalidOperationException("The orbital elements store root is not owned.");

    /// <summary>
    /// Names every file this attempt may create before any is created, so a failure or crash leaves verified
    /// orphans (named by an intent and bound to no published snapshot) rather than ambiguous files.
    /// </summary>
    private async ValueTask WriteIntentAsync(string attemptId, PreparedPublication prepared, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{IntentHeader} {attemptId}\nraw {prepared.RawSha256}\npayload {prepared.PayloadSha256}\nmanifest {prepared.ManifestSha256}\n"));
        await AtomicPublisher.PublishAsync(RequireRoot(), Path.Combine(StagingDirectoryName, attemptId + IntentExtension),
            PublishMode.CreateNew, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes one content-addressed file with CreateNew, or reuses an existing file only after proving its
    /// length and SHA-256. Accepted bytes are never edited: a mismatching file bound to a published snapshot is an
    /// integrity failure, and only a mismatching regular file bound to nothing is replaced.
    /// </summary>
    private async ValueTask EnsureContentFileAsync(
        SqliteConnection connection,
        string directory,
        string name,
        ReadOnlyMemory<byte> content,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var relative = Path.Combine(directory, name + ".json");
        var path = root.Resolve(relative);
        if (Directory.Exists(path))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "orbital-publish", relative);
        }
        if (File.Exists(path))
        {
            DurableSync.RequireRegularFile(path);
            if (await FileMatchesAsync(path, content.Length, contentSha256, cancellationToken).ConfigureAwait(false))
            {
                DurableSync.File(path);
                return;
            }
            if (await IsUsedByPublishedAsync(connection, directory, name, cancellationToken).ConfigureAwait(false))
            {
                throw new OrbitalStorageException("orbital.storage-integrity",
                    "An accepted orbital file no longer matches its identity.", null);
            }
            File.Delete(path);
            DurableSync.Directory(Path.GetDirectoryName(path)!);
        }
        await AtomicPublisher.PublishAsync(root, relative, PublishMode.CreateNew,
            (stream, token) => stream.WriteAsync(content, token).AsTask(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams the file through SHA-256 after a length check, so a wrong-size file is never hashed.</summary>
    private async ValueTask<bool> FileMatchesAsync(string path, long length, string sha256, CancellationToken cancellationToken)
    {
        RequireRoot().Verify(path, "orbital-verify");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != length)
        {
            return false;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != length)
        {
            return false;
        }
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(digest), sha256, StringComparison.Ordinal);
    }

    private static async ValueTask<bool> IsUsedByPublishedAsync(
        SqliteConnection connection,
        string directory,
        string sha256,
        CancellationToken cancellationToken)
    {
        var sql = directory switch
        {
            RawDirectoryName =>
                "SELECT EXISTS(SELECT 1 FROM orbital_snapshots WHERE raw_sha256 = $sha AND state = 'published');",
            PayloadDirectoryName =>
                "SELECT EXISTS(SELECT 1 FROM orbital_snapshots WHERE payload_sha256 = $sha AND state = 'published');",
            ManifestDirectoryName =>
                "SELECT EXISTS(SELECT 1 FROM orbital_snapshots WHERE manifest_sha256 = $sha AND state = 'published');",
            _ => throw new ArgumentOutOfRangeException(nameof(directory), directory, "Unknown orbital content directory.")
        };
        return await ScalarLongAsync(connection, null, sql, cancellationToken, ("$sha", sha256)).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<bool> IsQuarantineRetainedAsync(
        SqliteConnection connection,
        string sha256,
        CancellationToken cancellationToken)
        => await ScalarLongAsync(connection, null,
            "SELECT EXISTS(SELECT 1 FROM orbital_attempts WHERE raw_sha256 = $sha AND quarantine_retained = 1);",
            cancellationToken, ("$sha", sha256)).ConfigureAwait(false) == 1;

    /// <summary>
    /// Deletes the files an intent names that no published snapshot needs, then the intent itself. A malformed
    /// intent proves nothing, so only the intent is removed.
    /// </summary>
    private async ValueTask<int> ProcessIntentAsync(SqliteConnection connection, string intentPath, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        root.Verify(intentPath, "orbital-reconcile");
        if (!File.Exists(intentPath))
        {
            return 0;
        }
        DurableSync.RequireRegularFile(intentPath);
        var entries = await ReadIntentAsync(intentPath, cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        if (entries is null)
        {
            AmbiguousEntryLeft(_logger, StagingDirectoryName);
        }
        else
        {
            foreach (var (directory, sha256) in entries)
            {
                if (await IsUsedByPublishedAsync(connection, directory, sha256, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                switch (TryDeleteRegular(FilePath(directory, sha256)))
                {
                    case DeleteOutcome.Deleted:
                        deleted++;
                        break;
                    case DeleteOutcome.Refused:
                        AmbiguousEntryLeft(_logger, directory);
                        break;
                    default:
                        break;
                }
            }
        }
        File.Delete(intentPath);
        SyncDirectories(StagingDirectoryName, RawDirectoryName, PayloadDirectoryName, ManifestDirectoryName);
        return deleted;
    }

    private static async ValueTask<IReadOnlyList<(string Directory, string Sha256)>?> ReadIntentAsync(
        string intentPath,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(intentPath).Length;
        if (length is 0 or > MaximumIntentBytes)
        {
            return null;
        }
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(intentPath, cancellationToken).ConfigureAwait(false));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 4 || !lines[0].StartsWith(IntentHeader + " ", StringComparison.Ordinal))
        {
            return null;
        }
        var entries = new List<(string, string)>(3);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ');
            if (parts.Length != 2 || !OrbitalContract.IsSha256(parts[1]))
            {
                return null;
            }
            var directory = parts[0] switch
            {
                "raw" => RawDirectoryName,
                "payload" => PayloadDirectoryName,
                "manifest" => ManifestDirectoryName,
                _ => null
            };
            if (directory is null)
            {
                return null;
            }
            entries.Add((directory, parts[1]));
        }
        return entries;
    }

    private void TryDeleteIntent(string intentPath)
    {
        try
        {
            if (TryDeleteRegular(intentPath) == DeleteOutcome.Deleted)
            {
                DurableSync.Directory(Path.Combine(_rootPath, StagingDirectoryName));
            }
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // The activation is committed; reconcile removes the stale intent and keeps the published files.
            CleanupFailed(_logger, Path.GetFileNameWithoutExtension(intentPath), exception);
        }
    }

    /// <summary>
    /// Restart reconciliation. Intents are settled first, staging is then emptied of regular files, publisher
    /// temporaries are removed, and quarantine files no retained attempt needs are deleted. Anything that is not
    /// a regular file, or not named by an intent, is ambiguous and is left in place.
    /// </summary>
    private async ValueTask<int> ReconcileFilesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var staging = root.Resolve(StagingDirectoryName);
        var removed = 0;
        foreach (var intent in EnumerateBounded(staging, "*" + IntentExtension))
        {
            if (new FileInfo(intent).LinkTarget is not null)
            {
                AmbiguousEntryLeft(_logger, StagingDirectoryName);
                continue;
            }
            removed += await ProcessIntentAsync(connection, intent, cancellationToken).ConfigureAwait(false);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(staging).Take(MaximumReconciledEntries).ToList())
        {
            if (TryDeleteRegular(entry) == DeleteOutcome.Refused)
            {
                AmbiguousEntryLeft(_logger, StagingDirectoryName);
            }
        }
        foreach (var directory in ContentDirectories)
        {
            AtomicPublisher.CleanupTemporaries(root, root.Resolve(directory), MaximumReconciledEntries);
        }
        removed += await PruneQuarantineAsync(connection, cancellationToken).ConfigureAwait(false);
        SyncDirectories(ContentDirectories);
        return removed;
    }

    private async ValueTask<bool> RetainQuarantineFileAsync(
        SqliteConnection connection,
        string sha256,
        ReadOnlyMemory<byte> rawBytes,
        CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        var relative = Path.Combine(QuarantineDirectoryName, sha256 + ".json");
        var path = root.Resolve(relative);
        if (Directory.Exists(path))
        {
            AmbiguousEntryLeft(_logger, QuarantineDirectoryName);
            return false;
        }
        if (File.Exists(path))
        {
            DurableSync.RequireRegularFile(path);
            if (await FileMatchesAsync(path, rawBytes.Length, sha256, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            if (await IsQuarantineRetainedAsync(connection, sha256, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            File.Delete(path);
            DurableSync.Directory(Path.GetDirectoryName(path)!);
        }
        await AtomicPublisher.PublishAsync(root, relative, PublishMode.CreateNew,
            (stream, token) => stream.WriteAsync(rawBytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes quarantine evidence that no retained attempt row names; foreign names are left alone.</summary>
    private async ValueTask<int> PruneQuarantineAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var path in EnumerateBounded(Path.Combine(_rootPath, QuarantineDirectoryName), "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!OrbitalContract.IsSha256(name))
            {
                AmbiguousEntryLeft(_logger, QuarantineDirectoryName);
                continue;
            }
            if (await IsQuarantineRetainedAsync(connection, name, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            switch (TryDeleteRegular(path))
            {
                case DeleteOutcome.Deleted:
                    removed++;
                    break;
                case DeleteOutcome.Refused:
                    AmbiguousEntryLeft(_logger, QuarantineDirectoryName);
                    break;
                default:
                    break;
            }
        }
        if (removed > 0)
        {
            SyncDirectories(QuarantineDirectoryName);
        }
        return removed;
    }

    private async ValueTask PruneQuarantineBestEffortAsync(SqliteConnection connection)
    {
        try
        {
            await PruneQuarantineAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            CleanupFailed(_logger, QuarantineDirectoryName, exception);
        }
    }

    /// <summary>
    /// Records a failed attempt on a fresh connection (the failing transaction has already rolled back), refreshes
    /// the committed active identity, and removes the attempt's verified orphans. Never throws: if the outcome cannot
    /// be recorded, restart reconciliation settles the attempt and its intent.
    /// </summary>
    private async ValueTask SettleFailedAttemptAsync(
        string attemptId,
        OrbitalAttemptOutcome outcome,
        string reasonCode,
        string detail,
        PreparedPublication? prepared,
        string? intentPath = null)
    {
        try
        {
            using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using (var transaction = BeginImmediate(connection))
            {
                var current = await ScalarStringAsync(connection, transaction,
                    "SELECT outcome FROM orbital_attempts WHERE attempt_id = $attempt;", CancellationToken.None,
                    ("$attempt", attemptId)).ConfigureAwait(false);
                if (string.Equals(current, nameof(OrbitalAttemptOutcome.InFlight), StringComparison.Ordinal))
                {
                    await CompleteAttemptAsync(connection, transaction, attemptId, outcome, reasonCode, BoundText(detail),
                        ToUnixMilliseconds(Truncate(Now())), CancellationToken.None, rawSha256: prepared?.RawSha256,
                        rawLengthBytes: prepared?.Raw.Length).ConfigureAwait(false);
                    await BumpRevisionAsync(connection, transaction, CancellationToken.None).ConfigureAwait(false);
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    if (outcome == OrbitalAttemptOutcome.Interrupted)
                    {
                        AttemptInterrupted(_logger, attemptId);
                    }
                }
            }
            _activeManifestSha256 = await ScalarStringAsync(connection, null,
                "SELECT active_manifest_sha256 FROM orbital_provider_state WHERE provider = 'celestrak';",
                CancellationToken.None).ConfigureAwait(false);
            if (intentPath is not null)
            {
                try
                {
                    await ProcessIntentAsync(connection, intentPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsStorageFault(exception))
                {
                    CleanupFailed(_logger, attemptId, exception);
                }
            }
        }
        catch (Exception exception) when (IsStorageFault(exception) || exception is InvalidDataException or InvalidOperationException)
        {
            FailureNotRecorded(_logger, attemptId, exception);
        }
    }

    /// <summary>Deletes a regular file inside the root. Links, directories and other entries are refused.</summary>
    private DeleteOutcome TryDeleteRegular(string path)
    {
        var root = RequireRoot();
        try
        {
            root.Verify(path, "orbital-delete");
            if (Directory.Exists(path))
            {
                return DeleteOutcome.Refused;
            }
            if (!File.Exists(path))
            {
                return DeleteOutcome.Absent;
            }
            DurableSync.RequireRegularFile(path);
            File.Delete(path);
            return DeleteOutcome.Deleted;
        }
        catch (FileSystemFaultException fault) when (fault.Kind == FileSystemFaultKind.Containment)
        {
            return DeleteOutcome.Refused;
        }
    }

    private void SyncDirectories(params string[] directories)
    {
        foreach (var directory in directories)
        {
            DurableSync.Directory(Path.Combine(_rootPath, directory));
        }
    }

    private static List<string> EnumerateBounded(string directory, string pattern)
        => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(MaximumReconciledEntries).ToList();

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException or FileSystemFaultException { Kind: FileSystemFaultKind.Cancelled } ||
            (cancellationToken.IsCancellationRequested && exception is SqliteException);

    private static bool IsStorageFault(Exception exception)
        => exception is IOException or UnauthorizedAccessException or SqliteException;
}
