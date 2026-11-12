using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>
    /// Reads an execution's detail and, on the same snapshot, the detail as its run manifest carries it, without
    /// reading the execution a second time. The manifest view keeps the attempts, outputs, calibration masters and
    /// frozen contracts the detail already holds and adds only what the manifest alone needs. A failure of that
    /// evidence is reported beside the detail and never conceals the execution journal.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional manifest evidence cannot conceal the primary execution journal; its failure is returned as a fixed state.")]
    internal async ValueTask<ProcessingGraphExecutionDetailWithRunManifest?> ReadExecutionDetailWithRunManifestAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // One WAL snapshot for both views, as the manifest read keeps for itself: the manifest evidence can never
        // describe a later state of the execution than the detail beside it.
        using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "BEGIN;";
            await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var detail = await ReadExecutionDetailAsync(connection, executionId, manifestOnly: false, cancellationToken).ConfigureAwait(false);
            if (detail is null) return null;
            try
            {
                return new(detail, await ReadRunManifestEvidenceAsync(connection, detail, cancellationToken).ConfigureAwait(false));
            }
            catch (ProcessingRunManifestBoundsException)
            {
                return new(detail, null, ProcessingRunManifestEvidenceFailure.ExceedsBounds);
            }
            catch (InvalidDataException)
            {
                return new(detail, null, ProcessingRunManifestEvidenceFailure.Corrupt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new(detail, null, ProcessingRunManifestEvidenceFailure.Unavailable);
            }
        }
        finally
        {
            // SQLite ends the transaction itself when a read fails hard. A second rollback would then fail and
            // withhold a journal that was already read.
            if (SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Turns a detail just read on <paramref name="connection"/>'s snapshot into the detail a manifest-only read of
    /// that snapshot returns. Only the evidence the two reads do not share is read here, in the manifest read's own
    /// order so the same fault is the first one found: the row and byte bounds, the frozen plan's count ledgers, and
    /// each node's input bindings. Attempts, outputs, masters, dependencies and contracts are the same rows in both
    /// reads and are not read again.
    /// </summary>
    private static async ValueTask<ProcessingGraphExecutionDetail> ReadRunManifestEvidenceAsync(
        SqliteConnection connection,
        ProcessingGraphExecutionDetail detail,
        CancellationToken cancellationToken)
    {
        var executionId = detail.Execution.ExecutionId;
        await CheckRunManifestBoundsAsync(connection, executionId, cancellationToken).ConfigureAwait(false);
        var frozenPlanJson = await ReadExecutionFrozenPlanJsonAsync(connection, null, executionId, cancellationToken).ConfigureAwait(false);
        var frozenNodeCount = ReadRunManifestNodeCount(frozenPlanJson);
        var committedOutputCounts = ReadCommittedOutputCounts(frozenPlanJson);
        var frozenInputCounts = ReadFrozenOutputInputCounts(frozenPlanJson);
        // Only an execution frozen before the input ledger existed needs its windows.
        var legacy = detail.Nodes.Any(node => !frozenInputCounts.ContainsKey(node.NodeId));
        var legacyWindows = legacy ? ReadLegacyManifestWindows(frozenPlanJson) : [];
        var windowedNodes = legacy
            ? await ReadWindowedNodeIdsAsync(connection, executionId, cancellationToken).ConfigureAwait(false)
            : [];
        var nodes = new List<ProcessingGraphExecutionNodeState>(detail.Nodes.Count);
        foreach (var node in detail.Nodes)
        {
            var inputs = await ReadExecutionInputsAsync(
                connection, executionId, node.NodeId, manifestOnly: true, cancellationToken).ConfigureAwait(false);
            var bindings = await ReadRunManifestBindingsAsync(
                connection, detail.Execution, node.NodeId, windowedNodes.Contains(node.NodeId), inputs, node.CalibrationInputs,
                node.Outputs, node.Attempts.Count, node.Dependencies, frozenInputCounts, legacyWindows, cancellationToken).ConfigureAwait(false);
            nodes.Add(node with
            {
                Inputs = bindings.Inputs,
                CalibrationInputs = bindings.Masters,
                ArtifactBindingsRetained = bindings.Retained,
                InputEvidenceIncomplete = bindings.EvidenceIncomplete,
                // The detail already holds the count inferred from attempts and contracts; the ledger overrides it.
                CommittedOutputCount = committedOutputCounts.TryGetValue(node.NodeId, out var recorded) ? recorded : node.CommittedOutputCount,
                MixedGainDiagnostics = null,
                MixedGainDiagnosticsUnavailableReason = null
            });
        }
        return new(detail.Execution, nodes) { FrozenNodeCount = frozenNodeCount };
    }

    /// <summary>
    /// One node's input bindings as its run manifest carries them: whether every frozen derived input survives, whether
    /// legacy history lacks a proof of that, and the inputs completed from the node's own output lineage.
    /// </summary>
    private static async ValueTask<(IReadOnlyList<ProcessingGraphExecutionInputState> Inputs,
        IReadOnlyList<ProcessingGraphExecutionCalibrationInputState> Masters, bool Retained, bool EvidenceIncomplete)> ReadRunManifestBindingsAsync(
        SqliteConnection connection,
        ProcessingGraphExecutionState execution,
        string nodeId,
        bool hasWindow,
        IReadOnlyList<ProcessingGraphExecutionInputState> inputs,
        IReadOnlyList<ProcessingGraphExecutionCalibrationInputState> masters,
        IReadOnlyList<ProcessingGraphExecutionOutputState> outputs,
        int attemptCount,
        IReadOnlyList<ProcessingGraphDependencyDefinition> dependencies,
        Dictionary<string, int> frozenInputCounts,
        Dictionary<string, ProcessingGraphWindowRequirement> legacyWindows,
        CancellationToken cancellationToken)
    {
        var retained = true;
        var evidenceIncomplete = false;
        var pinnedCount = inputs.Count(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput);
        if (frozenInputCounts.TryGetValue(nodeId, out var originalInputs))
            retained = pinnedCount == originalInputs;
        else
            // A full immutable window can prove legacy pins complete even without
            // output lineage. A shorter surviving set cannot distinguish a valid
            // short window from a cascading retention delete.
            evidenceIncomplete = outputs.Count == 0 &&
                (pinnedCount > 0 || (hasWindow &&
                    dependencies.Any(static dependency => dependency.Kind == ProcessingGraphDependencyKind.Artifact &&
                        !string.Equals(dependency.ProducerId, "$raw", StringComparison.OrdinalIgnoreCase)) && attemptCount > 0)) &&
                !HasCompleteLegacyManifestWindow(legacyWindows.GetValueOrDefault(nodeId), inputs,
                    execution.ExecutionClass, attemptCount);
        var completed = await CompleteRunManifestInputsAsync(
            connection, execution, inputs, masters, outputs, cancellationToken).ConfigureAwait(false);
        return (completed.Inputs, completed.Masters, retained && completed.Complete, evidenceIncomplete);
    }

    private static async ValueTask<HashSet<string>> ReadWindowedNodeIdsAsync(
        SqliteConnection connection, Guid executionId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id FROM processing_execution_nodes
            WHERE execution_id = $execution AND window_json IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        var windowed = new HashSet<string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            windowed.Add(reader.GetString(0));
        return windowed;
    }
}
