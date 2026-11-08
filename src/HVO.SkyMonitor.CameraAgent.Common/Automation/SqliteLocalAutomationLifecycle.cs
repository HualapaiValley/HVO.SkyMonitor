using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

public sealed partial class SqliteLocalAutomationStore
{
    public async ValueTask<bool> HasInstallerLifecycleIntentAsync(string runKey, CancellationToken cancellationToken) =>
        _lifecycleControls is not null && (await _lifecycleControls.ReadAsync(cancellationToken).ConfigureAwait(false)).Receipts.Any(item =>
            item.Request.Kind == CameraAgentControlKind.Restart && item.Request.AutomationRunKey == runKey);

    /// <summary>
    /// Installer-authenticated completion of one delegated restart. The immutable retained occurrence must have this
    /// exact run key and task kind. Ordinary runs still require their process claimant and never use this route.
    /// </summary>
    public async Task CompleteInstallerLifecycleAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsTerminal || receipt.Request.AutomationRunKey is not { } runKey) return;
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var outcome = receipt.Phase switch
            {
                CameraAgentControlPhase.Succeeded => LocalAutomationRunOutcome.Succeeded,
                CameraAgentControlPhase.Skipped => LocalAutomationRunOutcome.Skipped,
                CameraAgentControlPhase.Cancelled => LocalAutomationRunOutcome.Interrupted,
                _ => LocalAutomationRunOutcome.Failed
            };
            await ExecuteAsync(connection, transaction, """
                UPDATE automation_runs SET outcome = $outcome, detail = $detail, completed_unix_ms = $now
                WHERE run_key = $key AND outcome = 'Running'
                  AND json_extract(occurrence_json, '$.definition.taskKind') = 'ScheduledCameraAgentRestart';
                """, cancellationToken, ("$outcome", outcome.ToString()), ("$detail", receipt.Events[^1].Code),
                ("$now", ToUnixMilliseconds(receipt.Events[^1].Utc)), ("$key", runKey)).ConfigureAwait(false);
            await RetainRunsAsync(connection, transaction, CameraAgentRestartAutomationAdapter.DefinitionId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
