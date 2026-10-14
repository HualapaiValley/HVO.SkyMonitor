using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal sealed partial class SqliteTimeLapseStore
{
    public async ValueTask<IReadOnlyList<CameraAgentTimeLapseDateSummary>> SummarizeAsync(
        DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken)
    {
        if (lastDate < firstDate || lastDate.DayNumber - firstDate.DayNumber > 61) throw new ArgumentOutOfRangeException(nameof(lastDate));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // The queue rows are bounded settings/window documents. Do not read source plans or packet proofs for a calendar.
        using var command = Command(connection, null, "WITH ranked AS (SELECT " + JobColumns + """
            ,ROW_NUMBER() OVER(PARTITION BY json_extract(window_json,'$.identitySha256') ORDER BY updated_ticks DESC, job_id DESC) AS rank
            FROM jobs WHERE report_date >= $first AND report_date <= $last)
            SELECT
            """ + JobColumns + " FROM ranked WHERE rank=1 LIMIT 4097;", ("$first", Date(firstDate)), ("$last", Date(lastDate)));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<CameraAgentTimeLapseJob>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) jobs.Add(ReadJob(reader));
        if (jobs.Count > 4096) throw new InvalidDataException("Video calendar exceeds its window bound.");
        return jobs.GroupBy(job => (job.Window.ReportingPeriod.ReportDate, job.Window.ReportingPeriod.IdentitySha256))
            .Select(group => new CameraAgentTimeLapseDateSummary(group.Key.ReportDate, group.Key.IdentitySha256,
                group.Where(job => job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay).MaxBy(job => job.UpdatedUtc)?.State,
                group.Count(job => job.Window.Policy.Kind == LocalAutomationSourceWindowKind.CompletedCivilHour && job.State == CameraAgentTimeLapseState.Produced),
                group.Any(job => job.State is CameraAgentTimeLapseState.Queued or CameraAgentTimeLapseState.Working))).ToArray();
    }
}
