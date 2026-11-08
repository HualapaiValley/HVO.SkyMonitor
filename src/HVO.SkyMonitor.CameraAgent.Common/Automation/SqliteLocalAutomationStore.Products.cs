using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

public sealed partial class SqliteLocalAutomationStore
{
    /// <summary>
    /// Reads retained daily still attempts for one bounded library scan. The journal, rather than recent operator
    /// history or the current definition, supplies the state, start time and immutable source window.
    /// </summary>
    public async ValueTask<IReadOnlyList<LocalAutomationRun>> ListNightlyProductRunsAsync(
        NightlyProductLibraryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.DateCount is < 1 or > NightlyProductProjectionContract.MaximumLibraryDates ||
            query.ObservingDate is not null && query.Before is not null)
            throw new ArgumentOutOfRangeException(nameof(query));
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH retained AS (
                    SELECT *, json_extract(occurrence_json, '$.sourceWindow.reportingPeriod.reportDate') AS report_date,
                        ROW_NUMBER() OVER(PARTITION BY occurrence_id ORDER BY attempt DESC, sequence DESC) AS rank
                    FROM automation_runs
                    WHERE occurrence_id IS NOT NULL
                      AND json_extract(occurrence_json, '$.sourceWindow.policy.kind') = 'SunriseDay'
                      AND json_extract(occurrence_json, '$.definition.taskKind') = 'StillImageGeneration'
                      AND ($target IS NULL OR json_extract(occurrence_json, '$.definition.taskTarget') LIKE $target)),
                pending AS (
                    SELECT * FROM retained WHERE rank = 1 AND outcome IN ('Queued','Running','Interrupted','Failed')
                      AND ($date IS NULL OR report_date = $date) AND ($before IS NULL OR report_date < $before)),
                dates AS (SELECT DISTINCT report_date FROM pending ORDER BY report_date DESC LIMIT $dates)
                SELECT sequence, definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                       started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence,
                       occurrence_json, attempt, occurrence_id, preparation_json
                FROM pending WHERE report_date IN (SELECT report_date FROM dates)
                ORDER BY sequence DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$target", query.Kind is { } kind ? NightlyProductContract.TargetFor(kind) + ":%" : (object)DBNull.Value);
            command.Parameters.AddWithValue("$date", query.ObservingDate is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$before", query.Before is { } before ? before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$dates", (query.State is null ? query.DateCount : NightlyProductProjectionContract.MaximumLibraryScannedDates) + 1);
            command.Parameters.AddWithValue("$limit", NightlyProductProjectionContract.MaximumSummarizedWindows + 1);
            var runs = new List<LocalAutomationRun>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                runs.Add(await ReadRunAsync(reader, cancellationToken).ConfigureAwait(false));
            if (runs.Count > NightlyProductProjectionContract.MaximumSummarizedWindows)
                throw new InvalidDataException("The generation projection exceeds its declared bound.");
            return runs;
        }
        finally { _gate.Release(); }
    }
}
