using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal static class DerivativeFixtureJobs
{
    // The production trigger rejects every UPDATE of an unsealed graph job, including no-op resets.
    // Fixture cleanup must leave that unfinished expansion intact while still retiring legacy and sealed jobs.
    internal static IQueryable<CentralDerivativeJob> ExcludingUnsealedGraphs(ApplicationDbContext db)
        => db.CentralDerivativeJobs.Where(job => job.GraphExecutionId == null || job.GraphExecution!.ExpandedAtUtc != null);

    // Retire only open work. Rewriting terminal history invokes the immutable-graph
    // trigger again without releasing any additional claim or capacity.
    internal static IQueryable<CentralDerivativeJob> ActiveForReset(ApplicationDbContext db)
        => ExcludingUnsealedGraphs(db).Where(job =>
            job.Status == CentralDerivativeJobStatus.Waiting ||
            job.Status == CentralDerivativeJobStatus.Pending ||
            job.Status == CentralDerivativeJobStatus.RetryableFailure ||
            job.Status == CentralDerivativeJobStatus.Leased ||
            job.Status == CentralDerivativeJobStatus.CancelRequested)
            .TagWith("fixture-active-derivative-reset");
}
