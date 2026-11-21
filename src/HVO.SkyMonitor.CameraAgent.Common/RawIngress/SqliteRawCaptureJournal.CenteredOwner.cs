using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class SqliteRawCaptureJournal
{
    internal async Task<CenteredOwnerTransition?> FenceCenteredOwnerAsync(
        string agentId, string? cameraId, string? profileIdentity, CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT camera_id, profile_identity, active_flag FROM centered_acquisition_owners WHERE agent_id = $agent;";
        command.Parameters.AddWithValue("$agent", agentId);
        string? previous = null;
        string? previousProfile = null;
        var hasPrior = false;
        var previousActive = false;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hasPrior = true;
                previous = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(0);
                previousProfile = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1);
                previousActive = reader.GetBoolean(2);
            }
        }
        // An existing row records centered ownership in this agent's history. A legacy
        // attachment (possibly to a different device) cannot clear that transport precaution.
        var needsLegacyQuiescence = cameraId is null && hasPrior;
        if (cameraId is null && !needsLegacyQuiescence) return null;
        if (previous is not null && (previous != cameraId || previousProfile != profileIdentity))
            await ExpireCenteredRunsAsync(connection, transaction, agentId, previous, "profile-replaced", false, cancellationToken)
                .ConfigureAwait(false);
        else if (previous is not null && !previousActive)
            await ExpireCenteredRunsAsync(connection, transaction, agentId, previous, "attachment-incomplete", false, cancellationToken)
                .ConfigureAwait(false);
        if (cameraId is not null)
            await ExpireCenteredRunsAsync(connection, transaction, agentId, cameraId, "target-progress-unknown", true, cancellationToken)
                .ConfigureAwait(false);

        var transition = new CenteredOwnerTransition(agentId, cameraId, Guid.NewGuid(), needsLegacyQuiescence);
        command.CommandText = """
            INSERT INTO centered_acquisition_owners(agent_id, camera_id, attachment_id, profile_identity, active_flag)
            VALUES ($agent, $camera, $attachment, $profile, 0)
            ON CONFLICT(agent_id) DO UPDATE SET camera_id = excluded.camera_id, attachment_id = excluded.attachment_id,
                profile_identity = excluded.profile_identity, device_identity = NULL, active_flag = 0;
            """;
        command.Parameters.AddWithValue("$camera", (object?)cameraId ?? DBNull.Value);
        command.Parameters.AddWithValue("$attachment", transition.AttachmentId.ToString("N"));
        command.Parameters.AddWithValue("$profile", (object?)profileIdentity ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return transition;
    }

    internal async Task FenceCenteredDeviceAsync(
        CenteredOwnerTransition transition, string deviceIdentity, CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE centered_acquisition_owners SET device_identity = $device
            WHERE agent_id = $agent AND camera_id = $camera AND attachment_id = $attachment AND active_flag = 0
              AND device_identity IS NULL;
            """;
        AddOwnerParameters(command, transition);
        command.Parameters.AddWithValue("$device", deviceIdentity);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RawIngressConflictException("The camera attachment no longer owns its setup transition.");

        command.CommandText = """
            SELECT device_identity FROM centered_exposure_runs
            WHERE agent_id = $agent AND camera_id = $camera AND closed_reason IS NULL;
            """;
        var prior = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (prior is not null && !Equals(prior, deviceIdentity))
            await ExpireCenteredRunsAsync(connection, transaction, transition.AgentId, transition.CameraId!,
                "device-changed", false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ActivateCenteredOwnerAsync(CenteredOwnerTransition transition, CancellationToken cancellationToken)
    {
        using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginJournalMetadataWrite();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE centered_acquisition_owners SET active_flag = 1
            WHERE agent_id = $agent AND camera_id IS $camera AND attachment_id = $attachment AND active_flag = 0
              AND (camera_id IS NULL OR device_identity IS NOT NULL);
            """;
        AddOwnerParameters(command, transition);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RawIngressConflictException("The camera did not complete its fenced attachment.");
    }

    private static void AddOwnerParameters(SqliteCommand command, CenteredOwnerTransition transition)
    {
        command.Parameters.AddWithValue("$agent", transition.AgentId);
        command.Parameters.AddWithValue("$camera", (object?)transition.CameraId ?? DBNull.Value);
        command.Parameters.AddWithValue("$attachment", transition.AttachmentId.ToString("N"));
    }
}
