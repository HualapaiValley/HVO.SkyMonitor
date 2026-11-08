using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>The production polling loop, independently testable with fake transport and deployment authority.</summary>
internal sealed class InstallerControlWorker(IInstallerControlClient client, InstallerControlJournal journal,
    Func<CancellationToken, Task<IDisposable>> acquireDeployment, TimeProvider clock, Action<string> report,
    InstallerControlBudgets? budgets = null)
{
    internal async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        // Never replay a restart after a worker interruption. Recover the prior known pause when no restart was issued.
        foreach (var interrupted in journal.Receipts.Where(item => !item.IsTerminal).ToArray())
        {
            try
            {
                using var recoveryLock = await acquireDeployment(cancellationToken).ConfigureAwait(false);
                await new InstallerControlVerifier(client, journal.SaveAsync, clock, budgets).SettleInterruptedAsync(interrupted).ConfigureAwait(false);
            }
            catch (InstallerException) { report("stage=lifecycle-worker recovery=installer-busy"); }
        }
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await PollAsync(cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) when (!once) { /* Durable evidence remains queued; the lease expires. */ }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !once) { }
            catch (InstallerControlRefusalException refusal)
            {
                report($"stage=lifecycle-worker heartbeat=refused status={refusal.StatusCode}");
            }
            if (once) return;
            await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken).ConfigureAwait(false);
        } while (true);
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        foreach (var receipt in journal.Unpublished.ToArray())
            await PublishAsync(receipt, cancellationToken).ConfigureAwait(false);
        // A refused historical receipt cannot prevent the independent worker's next heartbeat.
        var snapshot = await client.ExchangeAsync(null, cancellationToken).ConfigureAwait(false);
        if (snapshot.Current is not { } current) return;
        var retained = journal.Receipts.FirstOrDefault(item => item.Request.OperationId == current.Request.OperationId);
        if (journal.Refused.Any(item => item.OperationId == current.Request.OperationId)) return;
        if (retained is { IsTerminal: true })
        {
            await PublishAsync(retained, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!journal.CanRetainNew && retained is null)
        {
            report("stage=lifecycle-worker journal=full action=archive-refused-evidence");
            return;
        }
        if (current.Phase != CameraAgentControlPhase.Queued)
        {
            // A restored projection without its authoritative journal cannot authorize any effect.
            await FailAsync(current, "lifecycle.projection-orphaned", cancellationToken).ConfigureAwait(false);
            return;
        }
        IDisposable deploymentLock;
        try { deploymentLock = await acquireDeployment(cancellationToken).ConfigureAwait(false); }
        catch (InstallerException)
        {
            await FailAsync(current, "lifecycle.installer-busy", cancellationToken).ConfigureAwait(false);
            return;
        }
        using (deploymentLock)
        {
            var terminal = await new InstallerControlVerifier(client, journal.SaveAsync, clock, budgets)
                .ExecuteAsync(current, cancellationToken).ConfigureAwait(false);
            await PublishAsync(terminal, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(CameraAgentControlReceipt current, string code, CancellationToken cancellationToken)
    {
        var failed = current with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. current.Events, new(CameraAgentControlPhase.Failed, clock.GetUtcNow(), code)]
        };
        await journal.SaveAsync(failed).ConfigureAwait(false);
        await PublishAsync(failed, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            await client.ExchangeAsync(receipt, cancellationToken).ConfigureAwait(false);
            await journal.AcknowledgeAsync(receipt.Request.OperationId).ConfigureAwait(false);
        }
        catch (InstallerControlRefusalException refusal)
        {
            await journal.RefuseAsync(receipt.Request.OperationId, refusal.StatusCode, clock.GetUtcNow()).ConfigureAwait(false);
            report($"stage=lifecycle-worker publication=quarantined operation={receipt.Request.OperationId:D} status={refusal.StatusCode}");
        }
    }
}
