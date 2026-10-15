using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal sealed partial class SqliteTimeLapseStore
{
    /// <summary>Explicit, audited retry of the retained inputs. It never changes a published result or acquires new images.</summary>
    public async ValueTask<bool> RetryAsync(Guid jobId, long expectedRevision, Guid requestId, string actor,
        string reason, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty || requestId == Guid.Empty || expectedRevision <= 0 ||
            !Automation.LocalAutomationDefinitionValidator.IsText(actor, 128) ||
            !Automation.LocalAutomationDefinitionValidator.IsText(reason, 256)) throw new ArgumentException("Invalid video retry request.");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = Begin(connection);
            using (var existing = Command(connection, transaction,
                "SELECT job_id, expected_revision, actor, reason FROM retries WHERE request_id=$request;", ("$request", requestId.ToString("N"))))
            using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return reader.GetString(0) == jobId.ToString("N") && reader.GetInt64(1) == expectedRevision &&
                        reader.GetString(2) == actor && reader.GetString(3) == reason;
            CameraAgentTimeLapseJob? job;
            using (var select = Command(connection, transaction, "SELECT " + JobColumns + " FROM jobs WHERE job_id=$id;", ("$id", jobId.ToString("N"))))
            using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
            if (job is null || job.ParentJobId.HasValue || job.Revision != expectedRevision ||
                job.State is not (CameraAgentTimeLapseState.Failed or CameraAgentTimeLapseState.Unavailable)) return false;
            var attempts = Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM retries WHERE job_id=$id;", cancellationToken, ("$id", jobId.ToString("N"))).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (attempts >= 10) return false;
            var pending = Convert.ToInt64(await ScalarAsync(connection, transaction, """
                SELECT COUNT(*) FROM jobs WHERE state IN ('Queued','Working') OR
                    ((job_id=$id OR parent_job_id=$id) AND state IN ('Failed','Unavailable'));
                """, cancellationToken, ("$id", jobId.ToString("N"))).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (pending > MaximumPendingJobs) return false;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO retries(request_id,job_id,expected_revision,actor,reason,previous_state,previous_reason,requested_ticks)
                VALUES($request,$id,$expected,$actor,$reason,$state,$prior,$now);
                UPDATE jobs SET state='Queued', reason='timelapse.explicit-retry', updated_ticks=$now, revision=revision+1
                WHERE (job_id=$id OR parent_job_id=$id) AND state IN ('Failed','Unavailable');
                """, cancellationToken, ("$request", requestId.ToString("N")), ("$id", jobId.ToString("N")),
                ("$expected", expectedRevision), ("$actor", actor), ("$reason", reason), ("$state", job.State.ToString()),
                ("$prior", job.ReasonCode), ("$now", clock.GetUtcNow().UtcTicks)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _writes.Release(); }
    }
}
