using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal sealed partial class SqliteTimeLapseStore
{
    public async ValueTask<CameraAgentTimeLapseLibraryPage> ListLibraryAsync(
        NightlyProductLibraryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.DateCount is < 1 or > NightlyProductProjectionContract.MaximumLibraryDates ||
            query.ObservingDate is not null && query.Before is not null)
            throw new ArgumentOutOfRangeException(nameof(query));
        var scan = query.State is null ? query.DateCount : NightlyProductProjectionContract.MaximumLibraryScannedDates;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Both statements see one snapshot. Dates page by the indexed report date; a status scan never reads past its cap.
        using var transaction = BeginLibraryRead(connection);
        var dates = new List<DateOnly>();
        using (var command = Command(connection, transaction, """
            SELECT DISTINCT report_date FROM jobs
            WHERE ($date IS NULL OR report_date=$date) AND ($before IS NULL OR report_date<$before)
              AND json_extract(window_json,'$.policy.kind')='SunriseDay'
            ORDER BY report_date DESC LIMIT $limit;
            """, ("$date", query.ObservingDate is { } date ? Date(date) : null),
            ("$before", query.Before is { } before ? Date(before) : null), ("$limit", scan + 1)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var truncated = dates.Count > scan;
        if (truncated) dates.RemoveAt(scan);
        if (dates.Count == 0) return new([], null, null);
        using var rows = Command(connection, transaction, """
            WITH ranked AS (
                SELECT job_id, report_date, window_json, state, updated_ticks, reason, product_id,
                    ROW_NUMBER() OVER(PARTITION BY report_date, json_extract(window_json,'$.reportingPeriod.identitySha256')
                        ORDER BY (state IN ('Queued','Working')) DESC, updated_ticks DESC, job_id DESC) AS rank
                FROM jobs WHERE report_date IN (SELECT value FROM json_each($dates))
                  AND json_extract(window_json,'$.policy.kind')='SunriseDay')
            SELECT job.job_id, job.report_date, job.window_json, job.state,
                CASE WHEN job.state='Working' OR (job.state='Queued' AND job.reason='timelapse.recovering')
                    THEN job.updated_ticks END, job.reason,
                product.product_id, product.width, product.height, product.has_gaps,
                EXISTS(SELECT 1 FROM products AS hourly
                    JOIN jobs AS hourly_job ON hourly_job.job_id=hourly.job_id AND hourly_job.state='Produced'
                    WHERE hourly.is_daily=0 AND hourly.report_date=job.report_date
                      AND hourly.period_sha256=json_extract(job.window_json,'$.reportingPeriod.identitySha256'))
            FROM ranked AS job LEFT JOIN products AS product ON product.product_id=job.product_id
            WHERE job.rank=1 ORDER BY job.report_date DESC, job.job_id LIMIT 4097;
            """, ("$dates", System.Text.Json.JsonSerializer.Serialize(dates.Select(Date))));
        var entries = new List<CameraAgentTimeLapseLibraryEntry>();
        using (var reader = await rows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var date = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var window = Deserialize<LocalAutomationSourceWindow>(reader.GetString(2));
                if (!window.IsValid() || window.ReportingPeriod.ReportDate != date || window.Policy.Kind != LocalAutomationSourceWindowKind.SunriseDay)
                    throw new InvalidDataException("Invalid retained daily video source window.");
                entries.Add(new(Guid.ParseExact(reader.GetString(0), "N"), date, window,
                    Enum.Parse<CameraAgentTimeLapseState>(reader.GetString(3)),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : new(Guid.ParseExact(reader.GetString(6), "N"), date,
                        reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9) != 0), reader.GetInt32(10) != 0)
                { GenerationEnabled = options.Value.TimeLapses.Enabled });
            }
        if (entries.Any(static entry => entry.JobState == CameraAgentTimeLapseState.Produced && entry.Product is null))
            throw new InvalidDataException("A produced daily video has no published catalog entry.");
        if (entries.Count > 4096) throw new InvalidDataException("The video library exceeds its window bound.");
        var matches = entries.Where(entry => query.State is null || entry.State == query.State).ToArray();
        var matchingDates = dates.Where(date => matches.Any(entry => entry.ReportDate == date)).ToArray();
        var shownDates = matchingDates.Take(query.DateCount).ToHashSet();
        DateOnly? next = matchingDates.Length > query.DateCount ? matchingDates[query.DateCount - 1] : truncated ? dates[^1] : null;
        return new(matches.Where(entry => shownDates.Contains(entry.ReportDate)).ToArray(), next,
            truncated && shownDates.Count < query.DateCount ? dates[^1] : null);
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "SQLite exposes deferred transactions through its synchronous provider API.")]
    private static Microsoft.Data.Sqlite.SqliteTransaction BeginLibraryRead(Microsoft.Data.Sqlite.SqliteConnection connection)
        => connection.BeginTransaction(deferred: true);
}
