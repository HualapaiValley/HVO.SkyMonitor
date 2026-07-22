using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientDerivativeExecutor
{
    Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientDerivativeExecutor(
    ICentralTransientDerivativeBundleFactory bundleFactory,
    ICentralTransientDerivativeOutputWriter outputWriter,
    ICentralDerivativeJobService jobService) : ICentralTransientDerivativeExecutor
{
    public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (await outputWriter.IsCommittedAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            const string adopted = "transient-derivative.output-adopted";
            await jobService.CompleteWithoutArtifactAsync(
                lease.JobId, lease.LeaseToken, adopted, cancellationToken).ConfigureAwait(false);
            return new(ProcessingOutcomeStatus.Produced, null, adopted);
        }

        var bundle = await bundleFactory.CreateAsync(lease, cancellationToken).ConfigureAwait(false);
        await outputWriter.PersistAsync(lease, bundle, cancellationToken).ConfigureAwait(false);
        const string persisted = "transient-derivative.persisted";
        await jobService.CompleteWithoutArtifactAsync(
            lease.JobId, lease.LeaseToken, persisted, cancellationToken).ConfigureAwait(false);
        return new(ProcessingOutcomeStatus.Produced, null, persisted);
    }
}
