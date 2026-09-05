using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal async ValueTask<ProcessingReplaySource> ReadReplaySourceAsync(
        Guid captureId,
        Guid? artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(captureId, Guid.Empty);
        if (artifactId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(artifactId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.raw_capture_row_id, r.raw_artifact_id, r.payload_length,
                   r.descriptor_sha256, r.payload_sha256, r.manifest_sha256,
                   r.manifest_json, r.payload_relative_path, r.sidecar_relative_path,
                   c.context_json, c.context_sha256
            FROM raw_captures r
            JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
            WHERE r.capture_id = $capture AND r.state = 'committed'
              AND ($artifact IS NULL OR r.raw_artifact_id = $artifact)
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId?.ToString("N") ?? (object)DBNull.Value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("The replay source capture or artifact was not found.");
        }
        var rawRowId = reader.GetInt64(0);
        var resolvedArtifactId = Guid.ParseExact(reader.GetString(1), "N");
        var payloadBytes = reader.GetInt64(2);
        var descriptorSha256 = reader.GetString(3);
        var payloadSha256 = reader.GetString(4);
        var manifestSha256 = reader.GetString(5);
        var manifestJson = await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false);
        var payloadRelativePath = reader.GetString(7);
        var sidecarRelativePath = reader.GetString(8);
        var contextJson = await reader.GetFieldValueAsync<byte[]>(9, cancellationToken).ConfigureAwait(false);
        var contextSha256 = reader.GetString(10);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The replay source capture identity is ambiguous.");
        }
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
            manifest.Descriptor.Capture.CaptureId != captureId ||
            manifest.Descriptor.Artifact.ArtifactId != resolvedArtifactId ||
            !string.Equals(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), descriptorSha256, StringComparison.Ordinal) ||
            !string.Equals(CaptureContractJson.ComputeManifestSha256(manifestJson), manifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The replay source manifest is invalid or altered.");
        }
        var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextJson, contextSha256);
        var payloadPath = ResolveReplayPath(payloadRelativePath);
        var sidecarPath = ResolveReplayPath(sidecarRelativePath);
        if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
        {
            throw new FileNotFoundException("The replay source evidence is no longer retained.");
        }
        var payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        if (payload.LongLength != payloadBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(payload)), payloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The replay source payload is invalid or altered.");
        }
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (!sidecar.AsSpan().SequenceEqual(manifestJson))
        {
            throw new InvalidDataException("The replay source sidecar differs from committed evidence.");
        }
        var stored = new StoredFrameReference(
            payloadRelativePath,
            payloadPath,
            manifest.Descriptor.Timing.ExposureStartedUtc,
            FrameArtifactRole.Raw);
        return new ProcessingReplaySource(
            rawRowId,
            payloadBytes,
            descriptorSha256,
            payloadSha256,
            envelope.Configuration,
            envelope.Submission with
            {
                Result = envelope.Submission.Result with { Frame = null, Artifacts = null }
            },
            new RawCaptureReceipt(RawIngressOutcome.Existing, manifest, stored, manifestSha256));
    }

    internal async ValueTask<ProcessingReplaySubmissionResult> InsertReplayAsync(
        ProcessingReplaySubmission submission,
        ProcessingReplaySource source,
        ProcessingGraphRevisionSnapshot revision,
        Guid executionId,
        byte[] configurationJson,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(revision);
        ValidateCommand(idempotencyKey, actor, submission.Reason);
        var commandSha256 = CommandSha256(
            "replay",
            $"{executionId:N}:{submission.CaptureId:N}:{source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId:N}:" +
            $"{revision.State.RevisionId}:{submission.TriggerKind}:{submission.TriggerReference}:{submission.Priority}",
            actor,
            submission.Reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using (var idempotencyConnection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await ReadCommandAsync(idempotencyConnection, transaction: null, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false) is { } existingCommand)
            {
                EnsureIdempotent(existingCommand, "replay", commandSha256);
                var replayed = await ReadExecutionAsync(
                    idempotencyConnection, transaction: null,
                    Guid.ParseExact(existingCommand.ResultReference, "N"), cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The replay command result is missing.");
                return new(replayed, true);
            }
        }
        var preparedInputs = await PrepareReplayInputsAsync(source, revision, cancellationToken).ConfigureAwait(false);
        var storageGate = StorageLifecycleLock.ForRoot(_root);
        await storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
            using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
            if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
            {
                EnsureIdempotent(prior, "replay", commandSha256);
                var replayed = await ReadExecutionAsync(
                    connection, transaction, Guid.ParseExact(prior.ResultReference, "N"), cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The replay command result is missing.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(replayed, true);
            }
            using (var verifyRevision = connection.CreateCommand())
            {
                verifyRevision.Transaction = transaction;
                verifyRevision.CommandText = """
                SELECT COUNT(*) FROM processing_graph_revisions
                WHERE revision_id = $revision AND lifecycle IN ('Validated', 'Active')
                  AND definition_identity_sha256 = $definition
                  AND shared_plan_identity_sha256 = $shared_plan
                  AND local_plan_identity_sha256 = $local_plan;
                """;
                verifyRevision.Parameters.AddWithValue("$revision", revision.State.RevisionId);
                verifyRevision.Parameters.AddWithValue("$definition", revision.State.DefinitionIdentitySha256);
                verifyRevision.Parameters.AddWithValue("$shared_plan", revision.State.SharedPlanIdentitySha256);
                verifyRevision.Parameters.AddWithValue("$local_plan", revision.State.LocalPlanIdentitySha256);
                if (Convert.ToInt64(
                        await verifyRevision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture) != 1)
                {
                    throw new ProcessingGraphStoreConflictException(
                        "The selected processing graph revision changed during replay submission.");
                }
            }
            using (var capacity = connection.CreateCommand())
            {
                capacity.Transaction = transaction;
                capacity.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(e.payload_bytes), 0)
                FROM processing_replay_work work
                JOIN processing_executions e ON e.execution_id = work.execution_id
                WHERE work.state IN ('Pending', 'Leased', 'RetryWait');
                """;
                using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (reader.GetInt64(0) >= _executionOptions.ReplayMaximumPendingCount)
                {
                    throw new ProcessingReplayCapacityException("The local replay queue is at capacity.");
                }
            }
            using (var verify = connection.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText = """
                SELECT COUNT(*) FROM raw_captures
                WHERE raw_capture_row_id = $raw AND capture_id = $capture AND raw_artifact_id = $artifact
                  AND descriptor_sha256 = $descriptor AND payload_sha256 = $payload AND state = 'committed';
                """;
                verify.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
                verify.Parameters.AddWithValue("$capture", submission.CaptureId.ToString("N"));
                verify.Parameters.AddWithValue("$artifact", source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"));
                verify.Parameters.AddWithValue("$descriptor", source.DescriptorSha256);
                verify.Parameters.AddWithValue("$payload", source.PayloadSha256);
                if (Convert.ToInt64(
                        await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture) != 1)
                {
                    throw new ProcessingGraphStoreConflictException("The replay source changed during submission.");
                }
            }
            var accepted = _timeProvider.GetUtcNow();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                INSERT INTO processing_executions(
                    execution_id, execution_class, status, capture_id, primary_artifact_id,
                    graph_revision_id, definition_identity_sha256, shared_plan_identity_sha256,
                    local_plan_identity_sha256, frozen_plan_json, configuration_json,
                    trigger_kind, trigger_reference, priority, payload_bytes,
                    accepted_unix_ms, available_unix_ms, deadline_unix_ms, maximum_age_unix_ms,
                    allow_automatic_publication)
                VALUES ($execution, 'Replay', 'Pending', $capture, $artifact, $revision,
                        $definition, $shared_plan, $local_plan, $frozen_plan, $configuration,
                        $trigger, $trigger_reference, $priority, $bytes,
                        $accepted, $accepted, $deadline, $maximum_age, 0);
                """;
                insert.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                insert.Parameters.AddWithValue("$capture", submission.CaptureId.ToString("N"));
                insert.Parameters.AddWithValue("$artifact", source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"));
                insert.Parameters.AddWithValue("$revision", revision.State.RevisionId);
                insert.Parameters.AddWithValue("$definition", revision.State.DefinitionIdentitySha256);
                insert.Parameters.AddWithValue("$shared_plan", revision.State.SharedPlanIdentitySha256);
                insert.Parameters.AddWithValue("$local_plan", revision.State.LocalPlanIdentitySha256);
                insert.Parameters.AddWithValue("$frozen_plan", revision.FrozenPlanJson);
                insert.Parameters.AddWithValue("$configuration", configurationJson);
                insert.Parameters.AddWithValue("$trigger", submission.TriggerKind);
                insert.Parameters.AddWithValue("$trigger_reference", submission.TriggerReference ?? executionId.ToString("N"));
                insert.Parameters.AddWithValue("$priority", submission.Priority);
                insert.Parameters.AddWithValue("$bytes", source.PayloadBytes);
                insert.Parameters.AddWithValue("$accepted", accepted.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$deadline", accepted.AddSeconds(_executionOptions.ReplayDeadlineSeconds).ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$maximum_age", accepted.AddSeconds(_executionOptions.ReplayMaximumQueueAgeSeconds).ToUnixTimeMilliseconds());
                try
                {
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    throw new ProcessingGraphStoreConflictException(
                        "The replay trigger already identifies a different execution.", exception);
                }
            }
            foreach (var node in revision.Nodes)
            {
                using var insertNode = connection.CreateCommand();
                insertNode.Transaction = transaction;
                insertNode.CommandText = """
                INSERT INTO processing_execution_nodes(
                    execution_id, node_id, required, plan_sha256, shared_plan_node_identity_sha256,
                    dependencies_json, inputs_json, outputs_json, window_json, status)
                VALUES ($execution, $node, $required, $plan, $shared_node,
                        $dependencies, $inputs, $outputs, $window, 'Pending');
                """;
                insertNode.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                insertNode.Parameters.AddWithValue("$node", node.NodeId);
                insertNode.Parameters.AddWithValue("$required", node.Required ? 1 : 0);
                insertNode.Parameters.AddWithValue("$plan", node.PlanSha256);
                insertNode.Parameters.AddWithValue("$shared_node", node.SharedPlanNodeIdentitySha256);
                insertNode.Parameters.AddWithValue("$dependencies", node.DependenciesJson);
                insertNode.Parameters.AddWithValue("$inputs", node.InputsJson);
                insertNode.Parameters.AddWithValue("$outputs", node.OutputsJson);
                insertNode.Parameters.AddWithValue("$window", (object?)node.WindowJson ?? DBNull.Value);
                await insertNode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (node.DependenciesJson.Contains("$raw", StringComparison.Ordinal))
                {
                    var frozenInputs = preparedInputs.RawInputsByNode[node.NodeId];
                    for (var ordinal = 0; ordinal < frozenInputs.Count; ordinal++)
                    {
                        var frozen = frozenInputs[ordinal];
                        using var input = connection.CreateCommand();
                        input.Transaction = transaction;
                        input.CommandText = """
                        INSERT INTO processing_execution_inputs(
                            execution_id, node_id, input_ordinal, window_position, capture_id, artifact_id,
                            descriptor_sha256, payload_sha256, selected_flag)
                        VALUES ($execution, $node, $ordinal, $position, $capture, $artifact, $descriptor, $payload, 1);
                        """;
                        input.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                        input.Parameters.AddWithValue("$node", node.NodeId);
                        input.Parameters.AddWithValue("$ordinal", ordinal);
                        input.Parameters.AddWithValue("$position", frozen.WindowPosition);
                        input.Parameters.AddWithValue("$capture", frozen.Descriptor.Capture.CaptureId.ToString("N"));
                        input.Parameters.AddWithValue("$artifact", frozen.Descriptor.Artifact.ArtifactId.ToString("N"));
                        input.Parameters.AddWithValue("$descriptor", frozen.DescriptorSha256);
                        input.Parameters.AddWithValue("$payload", frozen.PayloadSha256);
                        await input.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (preparedInputs.OutputInputsByNode.TryGetValue(node.NodeId, out var frozenOutputs))
                {
                    await InsertFrozenOutputPinsAsync(
                        connection, transaction, executionId, node.NodeId, frozenOutputs, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            foreach (var frozen in preparedInputs.PinnedRawInputs)
            {
                await EnsureFrozenRawFilesExistAsync(connection, transaction, frozen, cancellationToken)
                    .ConfigureAwait(false);
                using var pin = connection.CreateCommand();
                pin.Transaction = transaction;
                pin.CommandText = """
                INSERT INTO processing_execution_input_pins(execution_id, raw_capture_row_id, artifact_id)
                SELECT $execution, raw_capture_row_id, raw_artifact_id
                FROM raw_captures
                WHERE raw_capture_row_id = $raw AND raw_artifact_id = $artifact
                  AND descriptor_sha256 = $descriptor AND payload_sha256 = $payload
                  AND state = 'committed';
                """;
                pin.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                pin.Parameters.AddWithValue("$raw", frozen.RawCaptureRowId);
                pin.Parameters.AddWithValue("$artifact", frozen.Descriptor.Artifact.ArtifactId.ToString("N"));
                pin.Parameters.AddWithValue("$descriptor", frozen.DescriptorSha256);
                pin.Parameters.AddWithValue("$payload", frozen.PayloadSha256);
                if (await pin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new ProcessingGraphStoreConflictException("A frozen replay input changed before it could be pinned.");
                using var hold = connection.CreateCommand();
                hold.Transaction = transaction;
                hold.CommandText = "UPDATE raw_captures SET retention_hold = 1 WHERE raw_capture_row_id = $raw;";
                hold.Parameters.AddWithValue("$raw", frozen.RawCaptureRowId);
                await hold.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var capacity = connection.CreateCommand())
            {
                capacity.Transaction = transaction;
                capacity.CommandText = """
                SELECT
                    (SELECT COALESCE(SUM(raw.payload_length), 0)
                     FROM processing_execution_input_pins pin
                     JOIN raw_captures raw ON raw.raw_capture_row_id = pin.raw_capture_row_id
                     WHERE pin.execution_id = $execution),
                    (SELECT COALESCE(SUM(execution.payload_bytes), 0)
                     FROM processing_replay_work work
                     JOIN processing_executions execution ON execution.execution_id = work.execution_id
                     WHERE work.state IN ('Pending', 'Leased', 'RetryWait'));
                """;
                capacity.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var frozenBytes = checked(reader.GetInt64(0) + preparedInputs.FrozenOutputBytes.Values.Sum());
                var queuedBytes = reader.GetInt64(1);
                if (frozenBytes > _executionOptions.ReplayMaximumPendingBytes ||
                    queuedBytes > _executionOptions.ReplayMaximumPendingBytes - frozenBytes)
                {
                    throw new ProcessingReplayCapacityException("The local replay queue is at capacity.");
                }
                using var updateBytes = connection.CreateCommand();
                updateBytes.Transaction = transaction;
                updateBytes.CommandText = "UPDATE processing_executions SET payload_bytes = $bytes WHERE execution_id = $execution;";
                updateBytes.Parameters.AddWithValue("$bytes", frozenBytes);
                updateBytes.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                await updateBytes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var work = connection.CreateCommand())
            {
                work.Transaction = transaction;
                work.CommandText = """
                INSERT INTO processing_replay_work(
                    execution_id, state, priority, available_unix_ms, updated_unix_ms)
                VALUES ($execution, 'Pending', $priority, $now, $now);
                """;
                work.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                work.Parameters.AddWithValue("$priority", submission.Priority);
                work.Parameters.AddWithValue("$now", accepted.ToUnixTimeMilliseconds());
                await work.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await InsertCommandAsync(
                connection, transaction, idempotencyKey, "replay", commandSha256, actor, submission.Reason,
                executionId.ToString("N"), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var state = await ReadExecutionAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The submitted replay execution is missing.");
            return new(state, false);
        }
        finally
        {
            storageGate.Release();
        }
    }

    private async ValueTask<PreparedReplayInputs> PrepareReplayInputsAsync(
        ProcessingReplaySource source,
        ProcessingGraphRevisionSnapshot revision,
        CancellationToken cancellationToken)
    {
        var primaryInput = CreatePrimaryInput(source);
        var rawInputsByNode = new Dictionary<string, IReadOnlyList<ProcessingFrozenRawInput>>(StringComparer.Ordinal);
        var outputInputsByNode = new Dictionary<string, IReadOnlyList<ProcessingFrozenOutputInput>>(StringComparer.Ordinal);
        var pinnedRawInputs = new Dictionary<long, ProcessingFrozenRawInput>
        {
            [primaryInput.RawCaptureRowId] = primaryInput
        };
        var frozenOutputBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
        foreach (var node in revision.Nodes)
        {
            if (node.DependenciesJson.Contains("$raw", StringComparison.Ordinal))
            {
                var frozenInputs = node.WindowJson is null
                    ? [primaryInput]
                    : await ProcessingRawWindowSelector.SelectAsync(
                        connection,
                        transaction,
                        source.RawCapture.Manifest.Descriptor,
                        JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(node.WindowJson, ExecutionSerializerOptions)
                            ?? throw new InvalidDataException($"Processing graph node '{node.NodeId}' has an invalid window."),
                        _executionOptions.MaximumWindowInputs,
                        cancellationToken).ConfigureAwait(false);
                rawInputsByNode[node.NodeId] = frozenInputs;
                foreach (var frozenInput in frozenInputs)
                    pinnedRawInputs[frozenInput.RawCaptureRowId] = frozenInput;
            }
            else if (node.WindowJson is { } windowJson &&
                     ProcessingOutputWindowSelector.ReadFirstProducerId(node.DependenciesJson) is { } producerId)
            {
                var requirement = JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(
                    windowJson, ExecutionSerializerOptions)
                    ?? throw new InvalidDataException($"Processing graph node '{node.NodeId}' has an invalid window.");
                var frozenOutputs = await ProcessingOutputWindowSelector.SelectAsync(
                    connection, transaction, source.RawCapture.Manifest.Descriptor, producerId,
                    revision.State.RevisionId,
                    revision.Nodes.Single(candidate => string.Equals(
                        candidate.NodeId, producerId, StringComparison.OrdinalIgnoreCase)).PlanSha256,
                    node.InputsJson,
                    requirement, _executionOptions.MaximumWindowInputs,
                    includeUnpublishedRevisionOutputs: true,
                    cancellationToken).ConfigureAwait(false);
                outputInputsByNode[node.NodeId] = frozenOutputs;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var frozenInput in pinnedRawInputs.Values)
            await ValidateFrozenRawInputAsync(frozenInput, cancellationToken).ConfigureAwait(false);
        foreach (var frozenOutput in outputInputsByNode.Values.SelectMany(static inputs => inputs).DistinctBy(
                     static input => input.OutputIdentitySha256, StringComparer.Ordinal))
        {
            var payloadPath = ResolveReplayPath(frozenOutput.PayloadRelativePath);
            if (!File.Exists(payloadPath))
                throw new FileNotFoundException("A frozen derived replay input is no longer retained.", payloadPath);
            frozenOutputBytes[frozenOutput.OutputIdentitySha256] = new FileInfo(payloadPath).Length;
        }
        return new(rawInputsByNode, outputInputsByNode, pinnedRawInputs.Values.ToArray(), frozenOutputBytes);
    }

    private async ValueTask InsertFrozenOutputPinsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        string nodeId,
        IReadOnlyList<ProcessingFrozenOutputInput> inputs,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < inputs.Count; ordinal++)
        {
            await EnsureFrozenOutputFilesExistAsync(
                connection, transaction, inputs[ordinal].OutputIdentitySha256, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO processing_execution_output_input_pins(
                    execution_id, node_id, input_ordinal, window_position,
                    output_identity_sha256, released_flag)
                SELECT $execution, $node, $ordinal, $position, output_identity_sha256, 0
                FROM processing_outputs
                WHERE output_identity_sha256 = $output AND availability_state = 'Available';
                """;
            command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            command.Parameters.AddWithValue("$node", nodeId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$position", inputs[ordinal].WindowPosition);
            command.Parameters.AddWithValue("$output", inputs[ordinal].OutputIdentitySha256);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new ProcessingGraphStoreConflictException("A frozen processing window output changed before it could be pinned.");
        }
    }

    private async ValueTask EnsureFrozenRawFilesExistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingFrozenRawInput input,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_relative_path, sidecar_relative_path, payload_length
            FROM raw_captures
            WHERE raw_capture_row_id = $raw AND raw_artifact_id = $artifact
              AND descriptor_sha256 = $descriptor AND payload_sha256 = $payload
              AND state = 'committed';
            """;
        command.Parameters.AddWithValue("$raw", input.RawCaptureRowId);
        command.Parameters.AddWithValue("$artifact", input.Descriptor.Artifact.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$descriptor", input.DescriptorSha256);
        command.Parameters.AddWithValue("$payload", input.PayloadSha256);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new ProcessingGraphStoreConflictException("A frozen replay input changed before it could be pinned.");
        var payloadPath = ResolveReplayPath(reader.GetString(0));
        var sidecarPath = ResolveReplayPath(reader.GetString(1));
        var payloadLength = reader.GetInt64(2);
        if (!File.Exists(payloadPath) || new FileInfo(payloadPath).Length != payloadLength || !File.Exists(sidecarPath))
            throw new ProcessingGraphStoreConflictException("A frozen replay input is no longer retained.");
    }

    private async ValueTask EnsureFrozenOutputFilesExistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string outputIdentitySha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_relative_path, sidecar_relative_path
            FROM processing_outputs
            WHERE output_identity_sha256 = $output AND availability_state = 'Available';
            """;
        command.Parameters.AddWithValue("$output", outputIdentitySha256);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new ProcessingGraphStoreConflictException("A frozen processing window output changed before it could be pinned.");
        var payloadPath = ResolveReplayPath(reader.GetString(0));
        var sidecarPath = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? null
            : ResolveReplayPath(reader.GetString(1));
        if (!File.Exists(payloadPath) || sidecarPath is not null && !File.Exists(sidecarPath))
            throw new ProcessingGraphStoreConflictException("A frozen processing window output is no longer retained.");
    }

    private sealed record PreparedReplayInputs(
        IReadOnlyDictionary<string, IReadOnlyList<ProcessingFrozenRawInput>> RawInputsByNode,
        IReadOnlyDictionary<string, IReadOnlyList<ProcessingFrozenOutputInput>> OutputInputsByNode,
        IReadOnlyList<ProcessingFrozenRawInput> PinnedRawInputs,
        IReadOnlyDictionary<string, long> FrozenOutputBytes);

    private static ProcessingFrozenRawInput CreatePrimaryInput(ProcessingReplaySource source)
        => new(
            source.RawCaptureRowId,
            0,
            source.RawCapture.Manifest.Descriptor,
            source.DescriptorSha256,
            source.PayloadSha256,
            source.RawCapture.StoredFrame.RelativePath);

    internal async ValueTask<IReadOnlyList<ProcessingFrozenRawInput>> ReadFrozenExecutionRawInputsAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw.raw_capture_row_id, input.window_position, raw.manifest_json,
                   input.descriptor_sha256, input.payload_sha256, raw.payload_relative_path
            FROM processing_execution_inputs input
            JOIN raw_captures raw ON raw.capture_id = input.capture_id
                                 AND raw.raw_artifact_id = input.artifact_id
            JOIN processing_execution_input_pins pin ON pin.execution_id = input.execution_id
                                                       AND pin.raw_capture_row_id = raw.raw_capture_row_id
            WHERE input.execution_id = $execution AND input.node_id = $node
              AND input.selected_flag = 1 AND raw.state = 'committed'
            ORDER BY input.input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        var inputs = new List<ProcessingFrozenRawInput>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var manifestJson = await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false);
            var descriptorSha256 = reader.GetString(3);
            var parsed = CaptureContractJson.ParseManifest(manifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest.Descriptor is not { } descriptor ||
                !string.Equals(CaptureContractJson.ComputeDescriptorSha256(descriptor), descriptorSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A frozen replay input has invalid durable evidence.");
            }
            inputs.Add(new(
                reader.GetInt64(0),
                reader.GetInt32(1),
                descriptor,
                descriptorSha256,
                reader.GetString(4),
                reader.GetString(5)));
        }
        return inputs;
    }

    internal ValueTask<IReadOnlyList<ProcessingFrozenRawInput>> ReadFrozenReplayRawInputsAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
        => ReadFrozenExecutionRawInputsAsync(executionId, nodeId, cancellationToken);

    internal async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadFrozenExecutionOutputsAsync(
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPinnedOutputsAsync(connection, null, executionId, nodeId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadPinnedOutputsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path,
                   output.sidecar_relative_path, output.descriptor_json, output.capture_id,
                   output.agent_id, output.node_id, output.role, output.variant,
                   output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                   output.total_integration_ticks, output.capture_sequence, output.product_kind,
                   output.product_schema_version, output.content_identity_sha256,
                   output.availability_state, output.availability_reason,
                   output.frame_artifact_recipe_version
            FROM processing_execution_output_input_pins pin
            JOIN processing_outputs output ON output.output_identity_sha256 = pin.output_identity_sha256
            WHERE pin.execution_id = $execution AND pin.node_id = $node AND pin.released_flag = 0
            ORDER BY pin.input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        return (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output).ToArray();
    }

    internal async ValueTask<ProcessingReplayLease?> ClaimReplayAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var now = _timeProvider.GetUtcNow();
        using (var recover = connection.CreateCommand())
        {
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE processing_replay_work
                SET state = 'RetryWait', available_unix_ms = $now,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE state = 'Leased' AND lease_expires_unix_ms <= $now;
                """;
            recover.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ExpireReplayWorkAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        using (var live = connection.CreateCommand())
        {
            live.Transaction = transaction;
            live.CommandText = """
                SELECT COUNT(*) FROM capture_lane_work
                WHERE lane_name = 'standard' AND state IN ('pending', 'leased', 'retry_wait');
                """;
            if (Convert.ToInt64(
                    await live.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        long workId;
        Guid executionId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT work.work_id, work.execution_id
                FROM processing_replay_work work
                JOIN processing_executions execution ON execution.execution_id = work.execution_id
                WHERE work.state IN ('Pending', 'RetryWait') AND work.available_unix_ms <= $now
                  AND execution.cancellation_requested = 0
                  AND ((execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms > $now)
                       OR (execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms > $now))
                  AND execution.attempt_count < $attempts
                ORDER BY work.priority DESC, work.available_unix_ms, work.work_id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            select.Parameters.AddWithValue("$attempts", _executionOptions.ReplayMaximumAttempts);
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            workId = reader.GetInt64(0);
            executionId = Guid.ParseExact(reader.GetString(1), "N");
        }
        var token = Guid.NewGuid().ToString("N");
        var expires = now.AddSeconds(_executionOptions.ReplayLeaseSeconds);
        using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE processing_replay_work
                SET state = 'Leased', lease_token = $token, lease_owner = $owner,
                    lease_expires_unix_ms = $expires, claim_count = claim_count + 1,
                    updated_unix_ms = $now
                WHERE work_id = $work AND state IN ('Pending', 'RetryWait');
                UPDATE processing_executions
                SET status = 'Running',
                    deadline_unix_ms = CASE WHEN started_unix_ms IS NULL THEN $deadline ELSE deadline_unix_ms END,
                    started_unix_ms = COALESCE(started_unix_ms, $now),
                    attempt_count = attempt_count + 1
                WHERE execution_id = $execution;
                """;
            claim.Parameters.AddWithValue("$token", token);
            claim.Parameters.AddWithValue("$owner", owner);
            claim.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
            claim.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            claim.Parameters.AddWithValue("$deadline", now.AddSeconds(_executionOptions.ReplayDeadlineSeconds).ToUnixTimeMilliseconds());
            claim.Parameters.AddWithValue("$work", workId);
            claim.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        int claimCount;
        using (var readClaimCount = connection.CreateCommand())
        {
            readClaimCount.Transaction = transaction;
            readClaimCount.CommandText = "SELECT claim_count FROM processing_replay_work WHERE work_id = $work;";
            readClaimCount.Parameters.AddWithValue("$work", workId);
            claimCount = Convert.ToInt32(
                await readClaimCount.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        var execution = await ReadExecutionAsync(connection, transaction, executionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The claimed replay execution is missing.");
        var revision = await ReadRevisionAsync(
            connection, transaction, execution.GraphRevisionId, cancellationToken).ConfigureAwait(false);
        using var sourceCommand = connection.CreateCommand();
        sourceCommand.Transaction = transaction;
        sourceCommand.CommandText = """
            SELECT execution.configuration_json, r.manifest_json, r.manifest_sha256,
                   r.payload_relative_path, c.context_json, c.context_sha256
            FROM processing_executions execution
            JOIN raw_captures r ON r.capture_id = execution.capture_id
                              AND r.raw_artifact_id = execution.primary_artifact_id
            JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
            WHERE execution.execution_id = $execution;
            """;
        sourceCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        using var sourceReader = await sourceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The claimed replay source is missing.");
        var configurationJson = await sourceReader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false);
        var manifestJson = await sourceReader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false);
        var manifestSha = sourceReader.GetString(2);
        var payloadRelativePath = sourceReader.GetString(3);
        var contextJson = await sourceReader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
        var contextSha = sourceReader.GetString(5);
        var configuration = JsonSerializer.Deserialize<CameraModuleConfig>(configurationJson, ExecutionSerializerOptions)
            ?? throw new InvalidDataException("The replay configuration is invalid.");
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
            throw new InvalidDataException("The replay source manifest is invalid.");
        var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextJson, contextSha);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Existing,
            manifest,
            new StoredFrameReference(
                payloadRelativePath,
                ResolveReplayPath(payloadRelativePath),
                manifest.Descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            manifestSha);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            workId,
            execution,
            revision,
            configuration,
            envelope.Submission with { Result = envelope.Submission.Result with { Frame = null, Artifacts = null } },
            receipt,
            token,
            owner,
            expires,
            claimCount);
    }

    internal async ValueTask<bool> RenewReplayAsync(
        ProcessingReplayLease lease,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_replay_work
            SET lease_expires_unix_ms = $expires, updated_unix_ms = $now
            WHERE work_id = $work AND execution_id = $execution AND state = 'Leased'
              AND lease_token = $token AND lease_owner = $owner AND lease_expires_unix_ms > $now
              AND EXISTS (
                   SELECT 1 FROM processing_executions execution
                   WHERE execution.execution_id = $execution AND execution.cancellation_requested = 0
                     AND execution.deadline_unix_ms > $now);
            """;
        var now = _timeProvider.GetUtcNow();
        command.Parameters.AddWithValue("$expires", now.AddSeconds(_executionOptions.ReplayLeaseSeconds).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$work", lease.WorkId);
        command.Parameters.AddWithValue("$execution", lease.Execution.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$token", lease.LeaseToken);
        command.Parameters.AddWithValue("$owner", lease.LeaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async ValueTask ExpireReplayWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = new List<(Guid ExecutionId, string Status, string WorkState, string Reason)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT execution.execution_id,
                       CASE
                           WHEN execution.cancellation_requested = 1 THEN 'Cancelled'
                            WHEN (execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms <= $now)
                              OR (execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms <= $now) THEN 'Expired'
                           ELSE 'Failed'
                       END,
                       CASE
                           WHEN execution.cancellation_requested = 1 THEN 'Cancelled'
                            WHEN (execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms <= $now)
                              OR (execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms <= $now) THEN 'Expired'
                           ELSE 'Failed'
                       END,
                       CASE
                           WHEN execution.cancellation_requested = 1 THEN 'processing.cancelled'
                            WHEN execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms <= $now
                                THEN 'processing.replay-maximum-age'
                            WHEN execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms <= $now
                                THEN 'processing.replay-deadline'
                           ELSE 'processing.replay-attempts-exhausted'
                       END
                FROM processing_executions execution
                JOIN processing_replay_work work ON work.execution_id = execution.execution_id
                WHERE work.state IN ('Pending', 'RetryWait') AND (
                    execution.cancellation_requested = 1 OR
                    (execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms <= $now) OR
                    (execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms <= $now) OR
                    execution.attempt_count >= $attempts);
                """;
            read.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            read.Parameters.AddWithValue("$attempts", _executionOptions.ReplayMaximumAttempts);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add((
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }
        foreach (var item in expired)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_executions
                SET status = $status, completed_unix_ms = $now, failure_reason = $reason
                WHERE execution_id = $execution;
                UPDATE processing_replay_work
                SET state = $work_state, updated_unix_ms = $now
                WHERE execution_id = $execution;
                UPDATE processing_node_attempts
                SET status = 'Interrupted', completed_unix_ms = $now, reason = $reason
                WHERE execution_id = $execution AND status = 'Running';
                UPDATE processing_execution_nodes
                SET status = 'TerminalFailure', completed_unix_ms = $now, reason = $reason
                WHERE execution_id = $execution AND status IN ('Pending', 'Running', 'RetryableFailure');
                """;
            update.Parameters.AddWithValue("$status", item.Status);
            update.Parameters.AddWithValue("$work_state", item.WorkState);
            update.Parameters.AddWithValue("$reason", item.Reason);
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$execution", item.ExecutionId.ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ReleaseExecutionPinsAsync(connection, transaction, item.ExecutionId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask<ProcessingGraphExecutionState> CompleteReplayAsync(
        ProcessingReplayLease lease,
        CaptureLaneHandlerResult result,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var context = new ProcessingExecutionContext(
            lease.Execution.ExecutionId,
            ProcessingGraphExecutionClass.Replay,
            lease.Execution.GraphRevisionId,
            lease.Execution.LocalPlanIdentitySha256,
            false,
            lease.WorkId,
            lease.LeaseToken,
            lease.LeaseOwner,
            lease.Execution.DeadlineUtc);
        await EnsureExecutionLeaseAsync(connection, transaction, context, cancellationToken).ConfigureAwait(false);
        var current = await ReadExecutionAsync(connection, transaction, lease.Execution.ExecutionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The replay execution is missing.");
        var now = _timeProvider.GetUtcNow();
        var cancelled = current.CancellationRequested;
        var expired = !cancelled && current.DeadlineUtc <= now;
        var deferred = result.Outcome == CaptureLaneHandlerOutcome.Deferred;
        var retry = !cancelled && !expired &&
            (deferred || result.Outcome == CaptureLaneHandlerOutcome.RetryableFailure &&
              current.AttemptCount < _executionOptions.ReplayMaximumAttempts) &&
            current.DeadlineUtc > now;
        var executionStatus = cancelled
            ? ProcessingGraphExecutionStatus.Cancelled
            : expired
                ? ProcessingGraphExecutionStatus.Expired
            : retry
                ? ProcessingGraphExecutionStatus.Pending
                : result.Outcome == CaptureLaneHandlerOutcome.Completed
                    ? ProcessingGraphExecutionStatus.Completed
                    : ProcessingGraphExecutionStatus.Failed;
        var workState = cancelled ? "Cancelled" : expired ? "Expired" : retry ? "RetryWait" : result.Outcome == CaptureLaneHandlerOutcome.Completed ? "Completed" : "Failed";
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_replay_work
                SET state = $work_state, available_unix_ms = $available,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE work_id = $work;
                UPDATE processing_executions
                SET status = $status, failure_reason = $reason,
                    completed_unix_ms = CASE WHEN $terminal = 1 THEN $now ELSE NULL END,
                    attempt_count = CASE WHEN $deferred = 1 AND attempt_count > 0
                        THEN attempt_count - 1 ELSE attempt_count END
                WHERE execution_id = $execution;
                DELETE FROM processing_node_attempts
                WHERE execution_id = $execution AND attempt_number = $claim
                  AND status = 'Running' AND $discard_attempt = 1;
                UPDATE processing_node_attempts
                SET status = 'Interrupted', completed_unix_ms = $now,
                    reason = COALESCE($reason, 'processing.execution-terminal')
                WHERE execution_id = $execution AND status = 'Running'
                  AND (($terminal = 1 AND $status != 'Completed') OR $deferred = 1);
                UPDATE processing_execution_nodes
                SET status = 'Pending', reason = $reason,
                    attempt_count = CASE WHEN $discard_attempt = 1 THEN COALESCE((
                        SELECT MAX(attempt.attempt_number)
                        FROM processing_node_attempts attempt
                        WHERE attempt.execution_id = $execution
                          AND attempt.node_id = processing_execution_nodes.node_id), 0)
                        ELSE attempt_count END,
                    started_unix_ms = NULL, completed_unix_ms = NULL
                WHERE execution_id = $execution AND status = 'Running' AND $deferred = 1;
                UPDATE processing_execution_nodes
                SET status = 'TerminalFailure', completed_unix_ms = $now,
                    reason = COALESCE($reason, 'processing.execution-terminal')
                WHERE execution_id = $execution
                  AND status IN ('Pending', 'Running', 'RetryableFailure')
                  AND $terminal = 1 AND $status != 'Completed';
                """;
            update.Parameters.AddWithValue("$work_state", workState);
            update.Parameters.AddWithValue("$available", now.AddSeconds(Math.Min(60, 1 << Math.Min(current.AttemptCount, 6))).ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$work", lease.WorkId);
            update.Parameters.AddWithValue("$status", executionStatus.ToString());
            update.Parameters.AddWithValue("$reason", executionStatus == ProcessingGraphExecutionStatus.Completed
                ? DBNull.Value
                : cancelled
                    ? "processing.cancelled"
                    : expired
                        ? "processing.replay-deadline"
                        : result.Reason);
            update.Parameters.AddWithValue("$terminal", retry ? 0 : 1);
            update.Parameters.AddWithValue("$deferred", deferred ? 1 : 0);
            update.Parameters.AddWithValue("$claim", lease.ClaimCount);
            update.Parameters.AddWithValue("$discard_attempt", result.DiscardExecutionAttempt ? 1 : 0);
            update.Parameters.AddWithValue("$execution", lease.Execution.ExecutionId.ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!retry)
        {
            await ReleaseExecutionPinsAsync(
                connection, transaction, lease.Execution.ExecutionId, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionAsync(lease.Execution.ExecutionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The completed replay execution is missing.");
    }

    internal async ValueTask<ProcessingGraphExecutionState> CancelReplayAsync(
        Guid executionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateCommand(idempotencyKey, actor, reason);
        var commandSha256 = CommandSha256("cancel-replay", executionId.ToString("N"), actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "cancel-replay", commandSha256);
            var replayed = await ReadExecutionAsync(connection, transaction, executionId, cancellationToken)
                .ConfigureAwait(false) ?? throw new KeyNotFoundException("The replay execution was not found.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replayed;
        }
        var execution = await ReadExecutionAsync(connection, transaction, executionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("The replay execution was not found.");
        if (execution.ExecutionClass != ProcessingGraphExecutionClass.Replay)
            throw new ProcessingGraphStoreConflictException("Only replay executions can be cancelled.");
        var terminal = execution.Status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed or
            ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired;
        if (!terminal)
        {
            using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE processing_executions SET cancellation_requested = 1,
                    status = CASE WHEN EXISTS(
                        SELECT 1 FROM processing_replay_work
                        WHERE execution_id = $execution AND state = 'Leased')
                        THEN status ELSE 'Cancelled' END,
                    completed_unix_ms = CASE WHEN EXISTS(
                        SELECT 1 FROM processing_replay_work
                        WHERE execution_id = $execution AND state = 'Leased')
                        THEN completed_unix_ms ELSE $now END,
                    failure_reason = 'processing.cancelled'
                WHERE execution_id = $execution;
                UPDATE processing_replay_work
                SET state = CASE WHEN state = 'Leased' THEN state ELSE 'Cancelled' END,
                    updated_unix_ms = $now
                WHERE execution_id = $execution;
                UPDATE processing_execution_nodes
                SET status = 'TerminalFailure', completed_unix_ms = $now,
                    reason = 'processing.cancelled'
                WHERE execution_id = $execution
                  AND status IN ('Pending', 'Running', 'RetryableFailure')
                  AND NOT EXISTS (
                      SELECT 1 FROM processing_replay_work
                      WHERE execution_id = $execution AND state = 'Leased');
                UPDATE processing_node_attempts
                SET status = 'Interrupted', completed_unix_ms = $now,
                    reason = 'processing.cancelled'
                WHERE execution_id = $execution AND status = 'Running'
                  AND NOT EXISTS (
                      SELECT 1 FROM processing_replay_work
                      WHERE execution_id = $execution AND state = 'Leased');
                """;
            cancel.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            cancel.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using var leased = connection.CreateCommand();
            leased.Transaction = transaction;
            leased.CommandText = "SELECT COUNT(*) FROM processing_replay_work WHERE execution_id = $execution AND state = 'Leased';";
            leased.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            if (Convert.ToInt64(
                    await leased.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                await ReleaseExecutionPinsAsync(connection, transaction, executionId, cancellationToken).ConfigureAwait(false);
            }
        }
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "cancel-replay", commandSha256, actor, reason,
            executionId.ToString("N"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionAsync(executionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The cancelled replay execution is missing.");
    }

    private async ValueTask ValidateFrozenRawInputAsync(
        ProcessingFrozenRawInput input,
        CancellationToken cancellationToken)
    {
        var path = ResolveReplayPath(input.PayloadRelativePath);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != input.Descriptor.Layout.ByteLength)
        {
            throw new ProcessingGraphStoreConflictException(
                "An archived raw processing window payload is no longer retained.");
        }
        using var payload = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(checksum, input.PayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An archived raw processing window payload is invalid or altered.");
        }
    }

    private string ResolveReplayPath(string relativePath)
    {
        if (!IsCanonicalRelativePath(relativePath))
            throw new InvalidDataException("The replay source path is not canonical.");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The replay source path escapes the storage root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }
}
