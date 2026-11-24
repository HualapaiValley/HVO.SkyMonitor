using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentLifecycleManager
{
    // The published RC2 journal predates the release checksum field. This attested SHA256SUMS anchors its exact
    // release.json and assets when recovering that journal. Successor upgrades retain their own checksum identity.
    private const string PublishedRc2ChecksumsSha256 = "d4826ad6f732ea9fe1bdc3fb0edc76f348b6ddceb2a825ee5f75e71373a374ec";

    private static async Task<(Guid PauseCommandId, StateSnapshotManifest Checkpoint, RawCaptureProgress Capture)> VerifySignedRestoreOnlyAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationState operation,
        ImageInstallationIdentity candidate,
        AcquiredImage signedImage,
        LifecycleContinuityBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (signedImage.VerifiedRelease.ChecksumsSha256 !=
            (operation.SignedReleaseChecksumsSha256 ?? PublishedRc2ChecksumsSha256))
            throw new InstallerException("Signed restore-only requires the original release manifest and checksums.");

        // The request hash retains the config digest. The candidate record instead retains the digest Docker
        // resolved, which can be the config, the platform manifest or the image index.
        if (signedImage.ArchivedImage is not { } archivedImage ||
            signedImage.Image.ImageIds is not { } allowedIds ||
            !allowedIds.Contains(candidate.ImageId, StringComparer.Ordinal) ||
            candidate != (archivedImage with { ImageId = candidate.ImageId, ImmutableReference = candidate.ImageId }) ||
            candidate.Architecture != manifest.Image.Architecture ||
            candidate.Architecture != manifest.DockerDaemon.Architecture ||
            candidate.ImageId == manifest.Image.ImageId)
            throw new InstallerException("Signed restore-only candidate identity differs from the original release archive.");

        if (operation.Phase is not (LifecycleOperationPhase.Restoring or LifecycleOperationPhase.Restored) ||
            !operation.StateSnapshotRequired || operation.StateSnapshot is not { } snapshot ||
            snapshot.OperationId != operation.OperationId || operation.StateSnapshotRestoredUtc is not { } restored ||
            restored < snapshot.RecordedUtc || restored > operation.UpdatedUtc ||
            operation.OriginalCatalog != manifest.Catalog)
            throw new InstallerException("Signed restore-only requires the operation's already-restored checkpoint.");

        var verified = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (verified.Image != manifest.Image || verified.ProtectedFiles is null)
            throw new InstallerException("Signed restore-only requires the predecessor's paired protected-state checkpoint.");
        VerifyCompletedRestorePlan(snapshot, verified, operation.StateRestorePlan);
        CameraAgentStateSnapshot.VerifyRestoredSchemas(paths, snapshot, verified, cancellationToken);
        var checkpoint = CameraAgentStateSnapshot.PreflightView(paths, snapshot, verified);
        var pauseCommandId = await ReadSignedPauseOwnerAsync(checkpoint.RawIngressDatabasePath, operation.OperationId, boundary, cancellationToken)
            .ConfigureAwait(false);
        var capture = await ReadDurableAdmissionAsync(checkpoint.RawIngressDatabasePath, cancellationToken).ConfigureAwait(false);
        return (pauseCommandId, verified, capture.Progress);
    }

    private static void VerifyCompletedRestorePlan(
        LifecycleStateSnapshot checkpoint, StateSnapshotManifest verified, StateRestorePlan? plan)
    {
        // The producer starts its current inventory with every fixed store, even when the candidate made some unreadable.
        // Dynamic stores can also come from earlier configurations, so only the sealed target inventory is exact.
        if (plan is not { CurrentLocations: { } current, TargetLocations: { } target } ||
            plan.SnapshotId != checkpoint.SnapshotId ||
            !current.All(static location => location is not null && CameraAgentStateInventory.IsValidStoreLocation(location)) ||
            current.Distinct(StringComparer.Ordinal).Count() != current.Count ||
            !CameraAgentStateLayout.SqliteStores.All(store => current.Contains(store, StringComparer.Ordinal)) ||
            !target.SequenceEqual(verified.Databases.Select(static item => item.RelativePath).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw InstallerException.Refused("The completed restore plan does not match its sealed checkpoint and required store inventory.");
    }

    /// <summary>Checks the changed pause receipt written by RC2, whose wire command UUID differs from the upgrade UUID.</summary>
    private static async Task<Guid> ReadSignedPauseOwnerAsync(
        string rawIngressDatabase,
        Guid operationId,
        LifecycleContinuityBoundary boundary,
        CancellationToken cancellationToken)
    {
        try
        {
            using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(rawIngressDatabase);
            using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT idempotency_key, actor FROM capture_control_commands
                WHERE rowid = (SELECT MAX(rowid) FROM capture_control_commands)
                  AND target_state = 'paused' AND status = 'completed' AND result_state = 'paused'
                  AND changed = 1 AND expected_version IS NULL AND result_version = $version
                  AND reason = $reason AND completed_unix_ms BETWEEN requested_unix_ms AND $observed;
                """;
            command.Parameters.AddWithValue("$version", boundary.CaptureVersion);
            command.Parameters.AddWithValue("$reason", $"transactional lifecycle operation {operationId:D} pause");
            command.Parameters.AddWithValue("$observed", boundary.RecordedUtc.ToUnixTimeMilliseconds());
            await using var receipt = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            const string actorPrefix = "installer-lifecycle:";
            if (await receipt.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                receipt.GetString(1) is { } actor && actor.StartsWith(actorPrefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(actor.AsSpan(actorPrefix.Length), "D", out var commandId) &&
                commandId != Guid.Empty && receipt.GetString(0) == $"lifecycle:{commandId:D}:pause")
                return commandId;
        }
        catch (SqliteException exception)
        {
            throw new InstallerException("Signed restore-only could not read the durable capture pause receipt.", exception);
        }
        throw new InstallerException("Signed restore-only capture admission is no longer owned by the interrupted upgrade.");
    }
}
