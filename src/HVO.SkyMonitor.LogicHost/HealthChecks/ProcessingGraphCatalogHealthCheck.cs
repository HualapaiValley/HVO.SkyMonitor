using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class ProcessingGraphCatalogHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var revision = await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == DatabaseSeeder.BasicCentralProcessingGraphRevisionId,
                cancellationToken)
            .ConfigureAwait(false);
        var assignment = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == DatabaseSeeder.BasicCentralProcessingGraphAssignmentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (revision is null || assignment is null ||
            revision.PublishedAtUtc is null || revision.PublishedAtUtc > now || revision.RetiredAtUtc is not null ||
            revision.EdgePlanIdentitySha256 is not null || revision.CentralPlanIdentitySha256 is null ||
            assignment.RevisionId != revision.Id ||
            assignment.TargetHost != CentralProcessingGraphTargetHost.Central ||
            assignment.Scope != CentralProcessingGraphAssignmentScope.GlobalDefault ||
            assignment.ObservatoryId is not null || assignment.LogicalCameraId is not null ||
            assignment.EffectiveFromUtc > now || assignment.EffectiveUntilUtc is not null)
        {
            return HealthCheckResult.Unhealthy("The canonical processing graph catalog seed is unavailable or inconsistent.");
        }

        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(revision.DefinitionJson));
        if (!parsed.IsValid)
        {
            return HealthCheckResult.Unhealthy("The canonical processing graph definition is invalid.");
        }
        var portable = ProcessingGraphCompiler.Compile(parsed.Definition!);
        var central = ProcessingGraphCompiler.Compile(
            parsed.Definition!,
            new(ProcessingGraphHosts.LogicHost, []));
        if (!portable.IsValid || !central.IsValid ||
            !string.Equals(
                portable.Plan!.DefinitionIdentitySha256,
                revision.DefinitionIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                portable.Plan.PlanIdentitySha256,
                revision.PortablePlanIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                central.Plan!.PlanIdentitySha256,
                revision.CentralPlanIdentitySha256,
                StringComparison.Ordinal))
        {
            return HealthCheckResult.Unhealthy("The canonical processing graph identities are inconsistent.");
        }

        return HealthCheckResult.Healthy("The processing graph catalog is available and internally consistent.");
    }
}
