using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>The normalization plan frozen for one execution node: the parsed plan, its canonical bytes and identity.</summary>
internal sealed record FrozenNormalizationPlan(
    string NodeId,
    string IdentitySha256,
    NormalizationPlanV1 Plan,
    byte[] CanonicalJson);

/// <summary>
/// A normalization plan cannot be frozen or used, with a stable reason: no input is eligible, the eligible coefficients
/// exceed the ratio bound, or a master the plan names is missing, unpublished, changed or corrupt.
/// </summary>
internal sealed class NormalizationPlanException : InvalidOperationException
{
    public NormalizationPlanException()
    {
        ReasonCode = NormalizationPlanReasonCodes.InvalidContract;
    }

    public NormalizationPlanException(string reasonCode)
        : base($"The normalization plan cannot be frozen or used ({reasonCode}).")
    {
        ReasonCode = reasonCode;
    }

    public NormalizationPlanException(string reasonCode, Exception innerException)
        : base($"The normalization plan cannot be frozen or used ({reasonCode}).", innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>The frozen execution JSON member that holds each node's normalization plan.</summary>
    private const string NormalizationPlansMember = "normalizationPlans";

    /// <summary>The <c>processing_executions.frozen_plan_json</c> column bound.</summary>
    private const int MaximumFrozenExecutionPlanBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Reads the normalization plan frozen for one execution node, or null when none was frozen. Executions frozen
    /// before plans existed have none and stay readable; no plan is ever synthesized for them. A stored plan that is
    /// not canonical or does not hash to its recorded identity is corrupt and fails explicitly.
    /// </summary>
    internal async ValueTask<FrozenNormalizationPlan?> ReadNormalizationPlanAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var frozenJson = await ReadExecutionFrozenPlanJsonAsync(connection, null, executionId, cancellationToken)
            .ConfigureAwait(false);
        return FindNormalizationPlan(frozenJson, nodeId);
    }

    /// <summary>
    /// Freezes a prepared plan into the execution's frozen plan JSON, in one short immediate transaction under the
    /// execution lease, as the live output window is frozen. The plan must describe exactly the node's frozen raw
    /// window, and every master it selects must still be the published library master it was selected as. Freezing is
    /// idempotent: the same plan returns the stored plan, and a different plan for the same node is a conflict, so a
    /// retry never replaces the plan its first attempt froze. After the transaction has been disposed, every master
    /// and its bundle evidence are verified again, including on an idempotent return. Verification failure leaves
    /// the frozen plan unchanged and fails the window. Serialization and all hashing stay outside the transaction.
    /// <paramref name="forceEvidenceValidation"/> is passed to <see cref="NormalizationPlanFreezer.ConfirmAsync"/>.
    /// </summary>
    internal async ValueTask<FrozenNormalizationPlan> FreezeNormalizationPlanAsync(
        SqliteCalibrationLibraryStore calibration,
        ProcessingExecutionContext execution,
        string nodeId,
        NormalizationPlanV1 plan,
        CancellationToken cancellationToken,
        bool forceEvidenceValidation = true)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var frozen = await FreezeNormalizationPlanCoreAsync(execution, nodeId, plan, cancellationToken).ConfigureAwait(false);
        await NormalizationPlanFreezer.ConfirmAsync(calibration, frozen.Plan, cancellationToken, forceEvidenceValidation)
            .ConfigureAwait(false);
        return frozen;
    }

    private async ValueTask<FrozenNormalizationPlan> FreezeNormalizationPlanCoreAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        NormalizationPlanV1 plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var canonical = NormalizationPlanContractJson.Serialize(plan);
        var identity = Convert.ToHexString(SHA256.HashData(canonical));
        var normalized = NormalizationPlanContractJson.Parse(canonical).Value!;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureExecutionLeaseAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        var frozenJson = await ReadExecutionFrozenPlanJsonAsync(
            connection, transaction, execution.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (FindNormalizationPlan(frozenJson, nodeId) is { } existing)
        {
            return string.Equals(existing.IdentitySha256, identity, StringComparison.Ordinal)
                ? existing
                : throw new ProcessingGraphStoreConflictException(
                    "A different normalization plan is already frozen for this execution node.");
        }
        await EnsurePlanDescribesFrozenWindowAsync(
            connection, transaction, execution.ExecutionId, nodeId, normalized, cancellationToken).ConfigureAwait(false);
        await EnsurePlanMastersPublishedAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
        var updated = AddNormalizationPlan(frozenJson, nodeId, identity, canonical);
        if (updated.Length > MaximumFrozenExecutionPlanBytes)
        {
            throw new InvalidOperationException("The normalization plan does not fit the frozen execution plan bound.");
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_executions SET frozen_plan_json = $json
                WHERE execution_id = $execution AND frozen_plan_json = $previous;
                """;
            update.Parameters.AddWithValue("$json", updated);
            update.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            update.Parameters.AddWithValue("$previous", frozenJson);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProcessingGraphStoreConflictException("The frozen execution plan changed during the freeze.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FrozenNormalizationPlan(nodeId, identity, normalized, canonical);
    }

    private static async ValueTask<byte[]> ReadExecutionFrozenPlanJsonAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT frozen_plan_json FROM processing_executions WHERE execution_id = $execution;";
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new KeyNotFoundException("The processing execution was not found.");
    }

    /// <summary>
    /// The plan's inputs are the node's frozen raw window: the same ordinals, window positions, capture and artifact
    /// identities, and descriptor and payload hashes, in order.
    /// </summary>
    private static async ValueTask EnsurePlanDescribesFrozenWindowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        string nodeId,
        NormalizationPlanV1 plan,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT input_ordinal, window_position, capture_id, artifact_id, descriptor_sha256, payload_sha256
            FROM processing_execution_inputs
            WHERE execution_id = $execution AND node_id = $node AND selected_flag = 1
            ORDER BY input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        var count = 0;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (count >= plan.Inputs.Count || !FrozenInputMatches(plan.Inputs[count], reader))
            {
                throw new ProcessingGraphStoreConflictException(
                    "The normalization plan does not describe the node's frozen window.");
            }
            count++;
        }
        if (count != plan.Inputs.Count)
        {
            throw new ProcessingGraphStoreConflictException(
                "The normalization plan does not describe the node's frozen window.");
        }
    }

    private static bool FrozenInputMatches(NormalizationPlanInputV1 input, SqliteDataReader reader)
        => reader.GetInt32(0) == input.Ordinal &&
           reader.GetInt32(1) == input.WindowPosition &&
           string.Equals(reader.GetString(2), input.CaptureId.ToString("N"), StringComparison.Ordinal) &&
           string.Equals(reader.GetString(3), input.ArtifactId.ToString("N"), StringComparison.Ordinal) &&
           string.Equals(reader.GetString(4), input.DescriptorSha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(reader.GetString(5), input.PayloadSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Confirms the library snapshot the plan was prepared from: every selected master is still a master of a
    /// published bundle, of the selected kind and payload hash. The payloads were hashed before the transaction.
    /// </summary>
    private static async ValueTask EnsurePlanMastersPublishedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NormalizationPlanV1 plan,
        CancellationToken cancellationToken)
    {
        var masters = plan.Inputs
            .Where(static input => input.Masters is not null)
            .SelectMany(static input => input.Masters!)
            .DistinctBy(static master => master.ArtifactId);
        foreach (var master in masters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT artifact.reference_kind, artifact.payload_sha256, bundle.publication_state
                FROM calibration_library_artifacts artifact
                JOIN calibration_library_bundles bundle ON bundle.bundle_id = artifact.bundle_id
                WHERE artifact.artifact_id = $artifact AND artifact.role = 'master';
                """;
            command.Parameters.AddWithValue("$artifact", master.ArtifactId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var reason = !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? CalibrationFamilyReasonCodes.MasterMissing
                : !string.Equals(reader.GetString(2), "published", StringComparison.Ordinal)
                    ? CalibrationFamilyReasonCodes.MasterUnpublished
                    : !string.Equals(reader.GetString(0), master.Kind, StringComparison.Ordinal) ||
                      !string.Equals(reader.GetString(1), master.PayloadSha256, StringComparison.OrdinalIgnoreCase)
                        ? CalibrationFamilyReasonCodes.MasterMismatch
                        : null;
            if (reason is not null)
            {
                throw new NormalizationPlanException(reason);
            }
        }
    }

    /// <summary>
    /// The library master artifact IDs named by every normalization plan frozen for a pending or running execution
    /// (#1475). Retention holds these masters whatever their bundle or family state, so a superseded family's masters
    /// stay while work that froze them can still run; a terminal execution no longer holds them. A plan that is
    /// corrupt fails the read, so retention fails closed rather than releasing masters it cannot account for.
    /// </summary>
    internal async ValueTask<IReadOnlyList<Guid>> ReadNormalizationPlanMasterArtifactIdsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT frozen_plan_json FROM processing_executions
            WHERE status IN ('Pending', 'Running') AND instr(frozen_plan_json, $member) > 0
            ORDER BY execution_id;
            """;
        command.Parameters.AddWithValue("$member", Encoding.UTF8.GetBytes($"\"{NormalizationPlansMember}\""));
        var ids = new HashSet<Guid>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var frozen in ReadNormalizationPlans((byte[])reader.GetValue(0), nodeId: null))
            {
                ids.UnionWith(frozen.Plan.Inputs
                    .Where(static input => input.Masters is not null)
                    .SelectMany(static input => input.Masters!)
                    .Select(static master => master.ArtifactId));
            }
        }
        return [.. ids.Order()];
    }

    /// <summary>
    /// Finds one node's plan in the frozen execution JSON. The JSON is the frozen graph plan object, optionally
    /// followed by a <c>normalizationPlans</c> array of <c>{nodeId, planIdentitySha256, plan}</c> entries that embed
    /// each plan's canonical bytes unchanged.
    /// </summary>
    private static FrozenNormalizationPlan? FindNormalizationPlan(byte[] frozenJson, string nodeId)
        => ReadNormalizationPlans(frozenJson, nodeId).SingleOrDefault();

    /// <summary>
    /// Reads the frozen plans of one node, or of every node when <paramref name="nodeId"/> is null. Every entry's shape
    /// is checked; each plan returned must be canonical and hash to its recorded identity.
    /// </summary>
    private static List<FrozenNormalizationPlan> ReadNormalizationPlans(byte[] frozenJson, string? nodeId)
    {
        try
        {
            using var document = JsonDocument.Parse(frozenJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The frozen execution plan is not a JSON object.");
            }
            var found = new List<FrozenNormalizationPlan>();
            if (!document.RootElement.TryGetProperty(NormalizationPlansMember, out var plans))
            {
                return found;
            }
            if (plans.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The frozen normalization plans are corrupt.");
            }
            var nodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in plans.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("nodeId", out var node) || node.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("planIdentitySha256", out var identity) ||
                    identity.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object ||
                    !nodes.Add(node.GetString()!))
                {
                    throw new InvalidDataException("The frozen normalization plans are corrupt.");
                }
                if (nodeId is not null && !string.Equals(node.GetString(), nodeId, StringComparison.Ordinal))
                {
                    continue;
                }
                var canonical = Encoding.UTF8.GetBytes(plan.GetRawText());
                var parsed = NormalizationPlanContractJson.Parse(canonical);
                if (!parsed.Validation.IsValid || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(canonical)),
                        identity.GetString(),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A frozen normalization plan is corrupt.");
                }
                found.Add(new FrozenNormalizationPlan(node.GetString()!, identity.GetString()!, parsed.Value!, canonical));
            }
            return found;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The frozen execution plan is corrupt.", exception);
        }
    }

    /// <summary>
    /// Appends one plan entry. Every existing member keeps its exact bytes and order, so removing the
    /// <c>normalizationPlans</c> member restores the frozen graph plan as it was accepted.
    /// </summary>
    private static byte[] AddNormalizationPlan(byte[] frozenJson, string nodeId, string identity, byte[] canonical)
    {
        using var document = JsonDocument.Parse(frozenJson);
        var buffer = new ArrayBufferWriter<byte>(frozenJson.Length + canonical.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(member.Name, NormalizationPlansMember, StringComparison.Ordinal))
                {
                    writer.WritePropertyName(member.Name);
                    writer.WriteRawValue(member.Value.GetRawText(), skipInputValidation: true);
                }
            }
            writer.WriteStartArray(NormalizationPlansMember);
            if (document.RootElement.TryGetProperty(NormalizationPlansMember, out var existing))
            {
                foreach (var entry in existing.EnumerateArray())
                {
                    writer.WriteRawValue(entry.GetRawText(), skipInputValidation: true);
                }
            }
            writer.WriteStartObject();
            writer.WriteString("nodeId", nodeId);
            writer.WriteString("planIdentitySha256", identity);
            writer.WritePropertyName("plan");
            writer.WriteRawValue(canonical, skipInputValidation: true);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
