using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

public sealed partial class SqliteOrbitalElementsStore
{
    private const int MaximumRetirementsPerRun = 64;

    private sealed record RetentionCandidate(string ManifestSha256, string PayloadSha256, string RawSha256,
        long PublishedUnixMs, bool IsActiveOrPrevious, bool IsReferenced);

    private sealed record RetiredFiles(string ManifestSha256, string PayloadSha256, string RawSha256);

    /// <inheritdoc />
    /// <remarks>
    /// A snapshot leaves retention only when it is outside both the newest
    /// <see cref="OrbitalElementsStoreSettings.MaximumRetainedSnapshots"/> and
    /// <see cref="OrbitalElementsStoreSettings.MinimumRetention"/>. Active, previous-active, referenced and
    /// pinned-while-loading snapshots are never retired; each one policy would otherwise retire counts as
    /// <see cref="OrbitalRetentionResult.Protected"/>. The tombstone commits before any file is deleted; a
    /// payload or raw file is deleted only when no published manifest still needs it; and an entry that is not a
    /// contained regular file is left in place and counted as <see cref="OrbitalRetentionResult.Refused"/>, with
    /// the tombstone kept pending so a later run finishes the deletion. Repeating the call is idempotent.
    /// </remarks>
    public async ValueTask<OrbitalRetentionResult> ApplyRetentionAsync(CancellationToken cancellationToken)
        => await ExecuteGatedAsync(ApplyRetentionCoreAsync, cancellationToken).ConfigureAwait(false);

    private async ValueTask<OrbitalRetentionResult> ApplyRetentionCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        int retired = 0, filesDeleted = 0, protectedCount = 0, refused = 0;
        foreach (var pending in await ReadPendingTombstonesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            var (deleted, wasRefused) = await DeleteRetiredFilesAsync(connection, pending, cancellationToken).ConfigureAwait(false);
            filesDeleted += deleted;
            refused += wasRefused ? 1 : 0;
        }
        var candidates = await ReadRetentionCandidatesAsync(connection, cancellationToken).ConfigureAwait(false);
        var now = ToUnixMilliseconds(Truncate(Now()));
        var minimumRetentionMs = (long)_settings.MinimumRetention.TotalMilliseconds;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (index < _settings.MaximumRetainedSnapshots || now - candidate.PublishedUnixMs < minimumRetentionMs)
            {
                continue;
            }
            if (candidate.IsActiveOrPrevious || candidate.IsReferenced)
            {
                protectedCount++;
                continue;
            }
            if (retired >= MaximumRetirementsPerRun)
            {
                break;
            }
            if (!IsAbsentOrRegularFile(ManifestPath(candidate.ManifestSha256)))
            {
                // An ambiguous manifest entry refuses retirement rather than tombstoning bytes it cannot remove.
                refused++;
                RetentionRefused(_logger, candidate.ManifestSha256);
                continue;
            }
            if (!await TryTombstoneAsync(connection, candidate.ManifestSha256, now, cancellationToken).ConfigureAwait(false))
            {
                protectedCount++;
                continue;
            }
            retired++;
            SnapshotRetired(_logger, candidate.ManifestSha256);
            _faults.Inject(OrbitalStorageFaultPoint.AfterTombstone);
            var (deleted, wasRefused) = await DeleteRetiredFilesAsync(connection,
                new RetiredFiles(candidate.ManifestSha256, candidate.PayloadSha256, candidate.RawSha256), cancellationToken)
                .ConfigureAwait(false);
            filesDeleted += deleted;
            refused += wasRefused ? 1 : 0;
        }
        return new OrbitalRetentionResult(retired, filesDeleted, protectedCount, refused);
    }

    /// <summary>
    /// Commits a tombstone only while no load holds a pin. Pinning waits on the same gate, so a reader either pins
    /// first and protects the snapshot or pins afterwards and observes the tombstone.
    /// </summary>
    private async ValueTask<bool> TryTombstoneAsync(
        SqliteConnection connection,
        string manifestSha256,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        await _pinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPinned(manifestSha256))
            {
                return false;
            }
            using var transaction = BeginImmediate(connection);
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE orbital_snapshots SET state = 'retired', retired_unix_ms = $now, files_pending = 1
                WHERE manifest_sha256 = $sha AND state = 'published'
                    AND NOT EXISTS(SELECT 1 FROM orbital_provider_state
                        WHERE active_manifest_sha256 = $sha OR previous_manifest_sha256 = $sha)
                    AND NOT EXISTS(SELECT 1 FROM orbital_snapshot_references WHERE manifest_sha256 = $sha);
                """, cancellationToken, ("$now", nowUnixMs), ("$sha", manifestSha256)).ConfigureAwait(false);
            if (changed != 1)
            {
                return false;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _retentionGeneration);
            return true;
        }
        finally
        {
            _pinGate.Release();
        }
    }

    /// <summary>
    /// Deletes a tombstoned snapshot's manifest, and its payload and raw files when no published manifest needs
    /// them. The tombstone stays pending until every entry is gone or proven absent.
    /// </summary>
    private async ValueTask<(int Deleted, bool Refused)> DeleteRetiredFilesAsync(
        SqliteConnection connection,
        RetiredFiles files,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        var refused = false;
        void Account(DeleteOutcome outcome)
        {
            deleted += outcome == DeleteOutcome.Deleted ? 1 : 0;
            refused |= outcome == DeleteOutcome.Refused;
        }
        Account(TryDeleteRegular(ManifestPath(files.ManifestSha256)));
        if (!await IsUsedByPublishedAsync(connection, PayloadDirectoryName, files.PayloadSha256, cancellationToken).ConfigureAwait(false))
        {
            Account(TryDeleteRegular(PayloadPath(files.PayloadSha256)));
        }
        if (!await IsUsedByPublishedAsync(connection, RawDirectoryName, files.RawSha256, cancellationToken).ConfigureAwait(false))
        {
            Account(TryDeleteRegular(RawPath(files.RawSha256)));
        }
        SyncDirectories(ManifestDirectoryName, PayloadDirectoryName, RawDirectoryName);
        if (refused)
        {
            RetentionRefused(_logger, files.ManifestSha256);
            return (deleted, true);
        }
        await ExecuteAsync(connection, null, """
            UPDATE orbital_snapshots SET files_pending = 0
            WHERE manifest_sha256 = $sha AND state = 'retired' AND files_pending = 1;
            """, cancellationToken, ("$sha", files.ManifestSha256)).ConfigureAwait(false);
        return (deleted, false);
    }

    private bool IsAbsentOrRegularFile(string path)
    {
        try
        {
            RequireRoot().Verify(path, "orbital-retention");
            if (Directory.Exists(path))
            {
                return false;
            }
            if (File.Exists(path))
            {
                DurableSync.RequireRegularFile(path);
            }
            return true;
        }
        catch (FileSystemFaultException fault) when (fault.Kind == FileSystemFaultKind.Containment)
        {
            return false;
        }
    }

    private static async ValueTask<List<RetiredFiles>> ReadPendingTombstonesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT manifest_sha256, payload_sha256, raw_sha256 FROM orbital_snapshots
            WHERE files_pending = 1 ORDER BY snapshot_sequence LIMIT $limit;
            """, ("$limit", MaximumRetirementsPerRun));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<RetiredFiles>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(new RetiredFiles(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return pending;
    }

    /// <summary>Published snapshots, newest first, with the facts that protect them.</summary>
    private static async ValueTask<List<RetentionCandidate>> ReadRetentionCandidatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, null, """
            SELECT s.manifest_sha256, s.payload_sha256, s.raw_sha256, s.published_unix_ms,
                EXISTS(SELECT 1 FROM orbital_provider_state AS p
                    WHERE p.active_manifest_sha256 = s.manifest_sha256 OR p.previous_manifest_sha256 = s.manifest_sha256),
                EXISTS(SELECT 1 FROM orbital_snapshot_references AS r WHERE r.manifest_sha256 = s.manifest_sha256)
            FROM orbital_snapshots AS s
            WHERE s.state = 'published'
            ORDER BY s.snapshot_sequence DESC;
            """);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<RetentionCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new RetentionCandidate(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4) != 0, reader.GetInt64(5) != 0));
        }
        return candidates;
    }
}
