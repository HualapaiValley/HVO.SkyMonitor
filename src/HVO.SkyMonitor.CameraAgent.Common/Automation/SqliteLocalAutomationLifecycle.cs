using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

public sealed partial class SqliteLocalAutomationStore
{
    public async ValueTask<bool> HasInstallerLifecycleIntentAsync(string runKey, CancellationToken cancellationToken) =>
        _lifecycleControls is not null && (await _lifecycleControls.ReadAsync(cancellationToken).ConfigureAwait(false)).Receipts.Any(item =>
            item.Request.Kind == CameraAgentControlKind.Restart && item.Request.AutomationRunKey == runKey);

    /// <summary>
    /// Applies a terminal installer receipt or locally cancelled unclaimed intent. The immutable retained occurrence must have this
    /// exact run key and task kind. Ordinary runs still require their process claimant and never use this route.
    /// </summary>
    public async Task CompleteInstallerLifecycleAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsTerminal || receipt.Request.AutomationRunKey is null) return;
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            await ApplyInstallerLifecycleReceiptAsync(connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static async Task ApplyInstallerLifecycleReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
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
            ("$now", ToUnixMilliseconds(receipt.Events[^1].Utc)), ("$key", receipt.Request.AutomationRunKey!)).ConfigureAwait(false);
        await RetainRunsAsync(connection, transaction, CameraAgentRestartAutomationAdapter.DefinitionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles the two durable stores after an interrupted receipt/history publication. No worker or new effect is required.</summary>
    private async Task ReconcileInstallerLifecycleAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_lifecycleControls is null) return;
        // There can be one active reserved restart run. The definition/sequence index bounds this lookup to its retained history.
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_key FROM automation_runs
            WHERE definition_id = $definition AND outcome = 'Running'
            ORDER BY sequence DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$definition", CameraAgentRestartAutomationAdapter.DefinitionId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string runKey) return;
        var receipt = (await _lifecycleControls.ReadAsync(cancellationToken).ConfigureAwait(false)).Receipts.FirstOrDefault(item =>
            item.IsTerminal && item.Request.Kind == CameraAgentControlKind.Restart && item.Request.AutomationRunKey == runKey);
        if (receipt is null) return;
        using var transaction = BeginImmediate(connection);
        await ApplyInstallerLifecycleReceiptAsync(connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
