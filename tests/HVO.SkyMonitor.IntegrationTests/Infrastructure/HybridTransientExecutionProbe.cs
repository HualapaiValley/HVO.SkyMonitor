using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

public sealed record HybridTransientExecutionProbeResult(string Status, string? ReasonCode);

public static class HybridTransientExecutionProbe
{
    public static async Task<HybridTransientExecutionProbeResult> ExecuteAsync(
        IServiceScopeFactory scopeFactory,
        string submissionIdentitySha256,
        CancellationToken cancellationToken)
    {
        Guid jobId;
        await using (var preparationScope = scopeFactory.CreateAsyncScope())
        {
            var db = preparationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralTransientValidationJobs.AsNoTracking()
                .Where(item => item.SubmissionIdentitySha256 == submissionIdentitySha256)
                .Select(item => item.CentralDerivativeJobId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            await preparationScope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                .ResolveAsync(jobId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            await db.CentralDerivativeJobs.Where(item => item.Id != jobId &&
                    (item.Status == CentralDerivativeJobStatus.Waiting ||
                     item.Status == CentralDerivativeJobStatus.Pending ||
                     item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null), cancellationToken)
                .ConfigureAwait(false);
        }

        CentralDerivativeJobLease lease;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("hybrid-integration-probe", TimeSpan.FromMinutes(2), cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The accepted Hybrid validation job was not claimable.");
            if (lease.JobId != jobId)
            {
                throw new InvalidOperationException("The Hybrid execution probe claimed an unexpected job.");
            }
        }

        await using var executionScope = scopeFactory.CreateAsyncScope();
        var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
        return new HybridTransientExecutionProbeResult(result.Status.ToString(), result.ReasonCode);
    }
}
