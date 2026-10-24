using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal static class DerivativeFixtureJobs
{
    // The production trigger rejects every UPDATE of an unsealed graph job, including no-op resets.
    // Fixture cleanup must leave that unfinished expansion intact while still retiring legacy and sealed jobs.
    internal static IQueryable<CentralDerivativeJob> ExcludingUnsealedGraphs(ApplicationDbContext db)
        => db.CentralDerivativeJobs.Where(job => job.GraphExecutionId == null || job.GraphExecution!.ExpandedAtUtc != null);
}
