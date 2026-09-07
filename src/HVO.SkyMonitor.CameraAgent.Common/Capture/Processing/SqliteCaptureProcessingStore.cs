using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum DurableProcessingNodeStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    RetryableFailure,
    TerminalFailure
}

internal sealed record DurableProcessingOutput(
    string OutputIdentitySha256,
    Guid ArtifactId,
    string PayloadRelativePath,
    string SidecarRelativePath,
    byte[] EvidenceJson,
    ReconstructionDescriptor? Descriptor,
    IDurableProcessingProductManifest? ProductManifest,
    string RecipeIdentitySha256,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    ProcessingCompatibilityIdentity Compatibility,
    TimeSpan TotalIntegration,
    long CaptureSequence,
    ProcessingProductKind? ProductKind = null,
    string? ProductSchemaVersion = null,
    string? ContentIdentitySha256 = null,
    string AvailabilityState = "Available",
    string? AvailabilityReason = null,
    string? FrameArtifactRecipeVersion = null)
{
    internal CaptureIdentityDescriptor Capture => Descriptor?.Capture ?? ProductManifest?.Capture
        ?? throw new InvalidDataException("Durable processing output has no capture descriptor.");

    internal ArtifactDescriptor Artifact => Descriptor?.Artifact ?? ProductManifest?.Artifact
        ?? throw new InvalidDataException("Durable processing output has no artifact descriptor.");
}

internal sealed record DurableCaptureProduct(
    string OutputIdentitySha256,
    Guid ArtifactId,
    Guid CaptureId,
    string NodeId,
    FrameArtifactRole Role,
    string Variant,
    ProcessingProductKind? ProductKind,
    string? ProductSchemaVersion,
    string? ContentIdentitySha256,
    string AvailabilityState = "Available",
    string? AvailabilityReason = null);

internal sealed record DurableProcessingEvidence(
    string OutputIdentitySha256,
    Guid ArtifactId,
    Guid CaptureId,
    FrameArtifactRole Role,
    string PayloadRelativePath,
    string SidecarRelativePath,
    byte[] EvidenceJson,
    long CommittedUnixMilliseconds,
    string AvailabilityState,
    string? AvailabilityReason,
    long? UnavailableUnixMilliseconds = null,
    string? QuarantineRelativePath = null);

internal sealed record ProcessingLifecycleOperation(
    string OperationId,
    string Kind,
    string? OutputIdentitySha256,
    string? SourceRelativePath,
    string? CompanionRelativePath,
    string DestinationRelativePath,
    string Reason,
    long ObservedBytes,
    long PlannedUnixMilliseconds = 0,
    string Phase = "planned");

internal sealed record ProcessingEvidencePage(
    IReadOnlyList<DurableProcessingEvidence> Items,
    string? NextOutputIdentitySha256);

internal sealed record ProcessingExpirationPage(
    IReadOnlyList<DurableProcessingEvidence> Items,
    long? NextTimestamp,
    string? NextOutputIdentitySha256);

internal sealed record ProcessingLifecyclePage(
    IReadOnlyList<ProcessingLifecycleOperation> Items,
    string? NextOperationId);

internal sealed record ProcessingAvailabilityInventory(long MissingCount, long QuarantinedCount);
internal sealed record ProcessingDiagnostic(long DiagnosticId, string? QuarantineRelativePath, long RecordedUnixMilliseconds);
internal sealed record ProcessingDiagnosticPage(IReadOnlyList<ProcessingDiagnostic> Items, long? NextRecordedUnixMilliseconds, long? NextDiagnosticId);

internal enum UnavailableNodeResolution { None, Reexecute, Terminal }
internal sealed record UnavailableOutputTransition(bool Reactivated);

internal sealed record DurableProcessingOutputSource(
    string OutputIdentitySha256,
    int Ordinal,
    Guid ArtifactId);

internal sealed record DurableProcessingNodeInput(
    int Ordinal,
    string Kind,
    string? Name,
    Guid? ArtifactId,
    FrameArtifactRole? Role,
    string? Variant,
    string? RecipeIdentitySha256,
    string? SchemaVersion,
    string? IdentitySha256,
    bool Selected);

internal sealed record DurableProcessingNode(
    Guid CaptureId,
    string NodeId,
    bool Required,
    DurableProcessingNodeStatus Status,
    string? Reason,
    int Attempt,
    string PlanSha256,
    string? ProcessingProfileIdentitySha256,
    DateTimeOffset? StartedUtc,
    DateTimeOffset CompletedUtc,
    TimeSpan? Duration,
    ProcessingOutcomeStatus? Outcome,
    IReadOnlyList<DurableProcessingNodeInput>? Inputs,
    IReadOnlyList<DurableProcessingOutput> Outputs);

internal sealed record DurableGalleryProcessingNode(
    Guid CaptureId,
    string NodeId,
    bool Required,
    DurableProcessingNodeStatus Status,
    string? RecipeName,
    string? OutputRole,
    string? OutputVariant,
    IReadOnlyList<DurableProcessingOutput> Outputs);

internal sealed record DurableGalleryProcessingProjection(
    IReadOnlyList<DurableGalleryProcessingNode> Nodes,
    IReadOnlySet<Guid> TruncatedNodeCaptures,
    IReadOnlySet<Guid> TruncatedOutputCaptures);

internal sealed record DurableGalleryProcessingNodeDetail(
    string NodeId,
    IReadOnlyList<string> Dependencies,
    int Attempt,
    DateTimeOffset CompletedUtc,
    string? Reason,
    string? ProcessingProfileIdentitySha256,
    DateTimeOffset? StartedUtc,
    TimeSpan? Duration,
    ProcessingOutcomeStatus? Outcome,
    IReadOnlyList<DurableProcessingNodeInput>? Inputs,
    bool InputsTruncated);

internal sealed record DurableRawProcessingInput(
    ReconstructionDescriptor Descriptor,
    string PayloadRelativePath);

internal sealed record CaptureProcessingOperationalState(
    long PendingCount,
    long RetryCount,
    long TerminalCount,
    DateTimeOffset? OldestPendingUtc,
    long ReplayPendingCount,
    long ReplayRetryCount,
    long ReplayTerminalCount,
    DateTimeOffset? OldestReplayPendingUtc,
    long ReplayPendingBytes);

internal sealed partial class SqliteCaptureProcessingStore : IDisposable
{
    internal const int CurrentSchemaVersion = 7;
    internal const int MaximumGalleryInputsPerNode = 8;
    internal const int MaximumProductQueryCount = 128;
    internal const int MaximumOutputSourceCount = LayeredPresentationJson.MaximumSourceArtifactCount;
    internal const int MinimumProcessingOutputRetentionCount = 100;
    private const int MinimumProcessingRetentionHoldSafetyCount = 4096;
    private const string PendingLiveOutputWindowsSql = """
        SELECT execution.graph_revision_id, raw.agent_id, raw.capture_sequence,
               json_extract(window_node.dependencies_json, '$[0].producerId') AS producer_node_id,
               producer_node.plan_sha256
        FROM processing_executions execution
        JOIN raw_captures raw ON raw.capture_id = execution.capture_id
        JOIN processing_execution_nodes window_node
          ON window_node.execution_id = execution.execution_id
        JOIN processing_execution_nodes producer_node
          ON producer_node.execution_id = execution.execution_id
         AND producer_node.node_id = json_extract(window_node.dependencies_json, '$[0].producerId')
        WHERE execution.execution_class = 'Live'
          AND execution.status IN ('Pending', 'Running')
          AND window_node.window_json IS NOT NULL
          AND window_node.status IN ('Pending', 'Running', 'RetryableFailure')
          AND json_type(window_node.dependencies_json, '$[0].producerId') = 'text';
        """;
    private const string InsertLiveOutputWindowCandidatesSql = """
        INSERT OR IGNORE INTO temp_live_output_window_candidates(artifact_id)
        SELECT candidate_output.artifact_id
        FROM processing_outputs candidate_output
        LEFT JOIN processing_nodes legacy
          ON legacy.capture_id = candidate_output.capture_id
         AND legacy.node_id = candidate_output.node_id
        WHERE candidate_output.agent_id = $agent
          AND candidate_output.capture_sequence < $current_capture_sequence
          AND candidate_output.availability_state = 'Available'
          AND (EXISTS (
                SELECT 1
                FROM processing_execution_outputs association
                JOIN processing_executions source_execution
                  ON source_execution.execution_id = association.execution_id
                WHERE association.output_identity_sha256 = candidate_output.output_identity_sha256
                  AND (association.node_id = $producer_node_id
                       OR (candidate_output.node_id = $producer_node_id
                           AND legacy.status = 'Completed'
                           AND legacy.plan_sha256 = $producer_plan_sha256))
                  AND association.published_flag = 1
                  AND ((source_execution.graph_revision_id = $graph_revision_id
                        AND source_execution.status = 'Completed')
                       OR (legacy.status = 'Completed'
                           AND legacy.plan_sha256 = $producer_plan_sha256)))
               OR (NOT EXISTS (
                     SELECT 1
                     FROM processing_execution_outputs association
                     WHERE association.output_identity_sha256 = candidate_output.output_identity_sha256)
                   AND candidate_output.node_id = $producer_node_id
                   AND legacy.status = 'Completed'
                   AND legacy.plan_sha256 = $producer_plan_sha256))
        ORDER BY candidate_output.capture_sequence DESC, candidate_output.output_identity_sha256 DESC
        LIMIT $maximum_candidates;
        """;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string LegacySchema5Sql = CreateLegacySchema5Sql();
    private static readonly string LegacySchema6Sql = CreateLegacySchema6Sql();
    private static readonly Lazy<Dictionary<string, string>> CanonicalSchemaDefinitions =
        new(CreateCanonicalSchemaDefinitions);
    private static readonly Lazy<Dictionary<string, string>> CanonicalSchema5Definitions =
        new(CreateCanonicalSchema5Definitions);
    private static readonly Lazy<Dictionary<string, string>> CanonicalSchema6Definitions =
        new(CreateCanonicalSchema6Definitions);
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly ArtifactReadOptions _artifactRead;
    private readonly ProcessingGraphExecutionOptions _executionOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ICaptureProcessingFaultInjector _faultInjector;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    internal string StorageRoot => _root;
    /// <summary>The shared upper bound on a durable window's inputs, applied to live and replay alike.</summary>
    internal int MaximumWindowInputs => _executionOptions.MaximumWindowInputs;
    private int ProcessingOutputRetentionCount => Math.Max(
        MinimumProcessingOutputRetentionCount,
        ProcessingOutputWindowSelector.GetCandidateScanCount(_executionOptions.MaximumWindowInputs));
    private int ProcessingRetentionHoldSafetyCount => checked(
        MinimumProcessingRetentionHoldSafetyCount *
        ((ProcessingOutputRetentionCount + MinimumProcessingOutputRetentionCount - 1) /
         MinimumProcessingOutputRetentionCount));
    internal static string LegacySchema5SqlForTests => LegacySchema5Sql;
    internal static string LegacySchema6SqlForTests => LegacySchema6Sql;

    public SqliteCaptureProcessingStore(
        IOptions<CameraAgentHostOptions> options,
        TimeProvider? timeProvider = null,
        ICaptureProcessingFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = options.Value;
        _root = Path.GetFullPath(values.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = values.RawIngressSqliteBusyTimeoutSeconds;
        _artifactRead = values.ArtifactRead;
        _executionOptions = values.ProcessingGraphs;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector ?? NullCaptureProcessingFaultInjector.Instance;
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            if (!File.Exists(_databasePath))
            {
                throw new InvalidOperationException(
                    $"Raw ingress schema {SqliteRawCaptureJournal.CurrentSchemaVersion} must be initialized before capture processing schema {CurrentSchemaVersion}.");
            }
            EnsureDatabaseFilesArePhysical();
            var inspection = await InspectExistingDatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (inspection.RawIngressVersion != SqliteRawCaptureJournal.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Raw ingress schema {inspection.RawIngressVersion} is unsupported; schema {SqliteRawCaptureJournal.CurrentSchemaVersion} must initialize before capture processing.");
            }
            var initializeSchema = inspection.ProcessingObjectCount == 0;
            var migrateSchema5 = !initializeSchema && inspection.ProcessingVersion == 5;
            var migrateSchema6 = !initializeSchema && inspection.ProcessingVersion == 6;
            if (!initializeSchema && !migrateSchema5 && !migrateSchema6 &&
                inspection.ProcessingVersion != CurrentSchemaVersion)
            {
                var version = inspection.ProcessingVersion ?? 0;
                var relationship = version > CurrentSchemaVersion ? "newer than supported" : "unsupported";
                throw new InvalidOperationException(
                    $"Capture processing schema {version} is {relationship}; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.");
            }
            using var connection = await OpenUnconfiguredAsync(cancellationToken).ConfigureAwait(false);
            if (initializeSchema)
            {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
                using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                _faultInjector.Inject(CaptureProcessingFaultPoint.AfterSchemaTransactionBegan, "schema");
                var writableRawVersion = await ExecuteScalarLongAsync(
                    connection, "PRAGMA user_version;", cancellationToken, transaction).ConfigureAwait(false);
                var writableProcessingObjectCount = await CountProcessingSchemaObjectsAsync(
                    connection, cancellationToken, transaction).ConfigureAwait(false);
                if (writableRawVersion != SqliteRawCaptureJournal.CurrentSchemaVersion || writableProcessingObjectCount != 0)
                {
                    throw new InvalidOperationException(
                        "Capture processing schema changed during initialization; restart after completing an explicit state-disposition procedure.");
                }
                await SqliteRawCaptureJournal.ValidateCanonicalSchemaDefinitionsAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
                _faultInjector.Inject(CaptureProcessingFaultPoint.BeforeSchemaCommit, "schema");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (migrateSchema5)
            {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
                using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                await ValidateSchema5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
                await MigrateSchema5Async(connection, transaction, cancellationToken).ConfigureAwait(false);
                await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (migrateSchema6)
            {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
                using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                await ValidateSchema6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
                await MigrateSchema6Async(connection, transaction, cancellationToken).ConfigureAwait(false);
                await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    internal async ValueTask<DurableProcessingNode?> ReadNodeAsync(
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT required, status, reason, attempt, plan_sha256,
                   processing_profile_identity_sha256, started_unix_ms, completed_unix_ms,
                   duration_ticks, outcome, input_evidence_version
            FROM processing_nodes
            WHERE capture_id = $capture_id AND node_id = $node_id;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var required = reader.GetInt64(0) != 0;
        var status = Enum.Parse<DurableProcessingNodeStatus>(reader.GetString(1));
        var reason = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2);
        var attempt = reader.GetInt32(3);
        var planSha256 = reader.GetString(4);
        var profileIdentity = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
        DateTimeOffset? startedUtc = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
        var completedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7));
        TimeSpan? duration = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
            ? null
            : TimeSpan.FromTicks(reader.GetInt64(8));
        ProcessingOutcomeStatus? outcome = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)
            ? null
            : Enum.Parse<ProcessingOutcomeStatus>(reader.GetString(9));
        var hasInputEvidence = !await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false);
        await reader.DisposeAsync().ConfigureAwait(false);
        var outputs = await ReadOutputsAsync(connection, captureId, nodeId, cancellationToken).ConfigureAwait(false);
        var inputs = hasInputEvidence
            ? await ReadInputsAsync(connection, captureId, nodeId, cancellationToken).ConfigureAwait(false)
            : null;
        return new DurableProcessingNode(
            captureId, nodeId, required, status, reason, attempt, planSha256, profileIdentity,
            startedUtc, completedUtc, duration, outcome, inputs, outputs);
    }

    internal async ValueTask<UnavailableNodeResolution> ResolveUnavailableNodeAsync(
        Guid captureId, string nodeId, string planSha256, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var unavailable = new List<(string Output, string State, string? Reason, string? Schema, byte[] Evidence)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT output.output_identity_sha256, output.availability_state, output.availability_reason,
                       output.product_schema_version, output.descriptor_json
                FROM processing_outputs output
                JOIN processing_nodes node ON node.capture_id = output.capture_id AND node.node_id = output.node_id
                WHERE output.capture_id = $capture AND output.node_id = $node
                  AND node.plan_sha256 = $plan AND output.availability_state <> 'Available'
                ORDER BY output.output_identity_sha256;
            """;
            read.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            read.Parameters.AddWithValue("$node", nodeId);
            read.Parameters.AddWithValue("$plan", planSha256);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                unavailable.Add((reader.GetString(0), reader.GetString(1),
                    await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                    await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                    await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false)));
        }
        if (unavailable.Count == 0) return UnavailableNodeResolution.None;
        var deterministic = unavailable.All(static output =>
            string.Equals(output.Schema, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal));
        foreach (var output in unavailable)
        {
            using var history = connection.CreateCommand();
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO processing_output_diagnostics(
                    output_identity_sha256, capture_id, node_id, availability_state,
                    availability_reason, descriptor_json, recorded_unix_ms)
                SELECT $output, $capture, $node, $state, $reason, $evidence, $now
                WHERE NOT EXISTS (
                    SELECT 1 FROM processing_output_diagnostics diagnostic
                    WHERE diagnostic.output_identity_sha256 = $output
                      AND diagnostic.availability_state = $state
                      AND diagnostic.availability_reason IS $reason
                    ORDER BY diagnostic.recorded_unix_ms DESC, diagnostic.diagnostic_id DESC
                    LIMIT 1);
                DELETE FROM processing_output_diagnostics
                WHERE output_identity_sha256 = $output AND diagnostic_id NOT IN (
                    SELECT diagnostic_id FROM processing_output_diagnostics
                    WHERE output_identity_sha256 = $output
                    ORDER BY recorded_unix_ms DESC, diagnostic_id DESC LIMIT 16);
                """;
            history.Parameters.AddWithValue("$output", output.Output);
            history.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            history.Parameters.AddWithValue("$node", nodeId);
            history.Parameters.AddWithValue("$state", output.State);
            history.Parameters.AddWithValue("$reason", (object?)output.Reason ?? DBNull.Value);
            history.Parameters.AddWithValue("$evidence", output.Evidence);
            history.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            if (deterministic)
                update.CommandText = """
                    DELETE FROM processing_outputs
                    WHERE capture_id = $capture AND node_id = $node AND availability_state <> 'Available';
                    UPDATE processing_nodes SET status = 'RetryableFailure', reason = 'processing.output-unavailable'
                    WHERE capture_id = $capture AND node_id = $node;
                    """;
            else
                update.CommandText = """
                    UPDATE processing_nodes SET status = 'TerminalFailure', reason = 'processing.output-unavailable'
                    WHERE capture_id = $capture AND node_id = $node;
                    """;
            update.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            update.Parameters.AddWithValue("$node", nodeId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deterministic ? UnavailableNodeResolution.Reexecute : UnavailableNodeResolution.Terminal;
    }

    internal ValueTask WriteNodeAsync(
        Guid captureId,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        string? processingProfileIdentitySha256,
        DateTimeOffset? startedUtc,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        IReadOnlyList<DurableProcessingNodeInput> inputs,
        long workId,
        string? leaseToken,
        IReadOnlyList<DurableProcessingOutput> outputs,
        CancellationToken cancellationToken)
        => WriteNodeAsync(
            captureId, node, status, reason, attempt, processingProfileIdentitySha256,
            startedUtc, completedUtc, duration, outcome, inputs, workId, leaseToken,
            outputs, null, cancellationToken);

    internal async ValueTask WriteNodeAsync(
        Guid captureId,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        string? processingProfileIdentitySha256,
        DateTimeOffset? startedUtc,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        IReadOnlyList<DurableProcessingNodeInput> inputs,
        long workId,
        string? leaseToken,
        IReadOnlyList<DurableProcessingOutput> outputs,
        ProcessingExecutionContext? execution,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (execution is null)
        {
            await EnsureLeaseAsync(connection, transaction, workId, leaseToken, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EnsureExecutionLeaseAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        }
        var outputPublication = new bool[outputs.Count];
        for (var index = 0; index < outputs.Count; index++)
        {
            var inserted = await InsertOutputAsync(
                connection, transaction, captureId, node.Id, outputs[index], execution is not null, cancellationToken)
                .ConfigureAwait(false);
            outputPublication[index] = execution is null ||
                execution.ExecutionClass != ProcessingGraphExecutionClass.Live && execution.AllowAutomaticPublication ||
                !inserted && await IsOutputPublishedAsync(
                    connection, transaction, outputs[index].OutputIdentitySha256, cancellationToken)
                    .ConfigureAwait(false);
        }
        if (execution?.AllowAutomaticPublication != false)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO processing_nodes(
                capture_id, node_id, required, dependencies_json, recipe_name, output_role, output_variant,
                plan_sha256, status, reason, attempt, input_evidence_version,
                processing_profile_identity_sha256, started_unix_ms, completed_unix_ms, duration_ticks, outcome)
            VALUES (
                $capture_id, $node_id, $required, $dependencies_json, $recipe_name, $output_role, $output_variant,
                $plan_sha256, $status, $reason, $attempt, 1,
                $processing_profile, $started_unix_ms, $completed_unix_ms, $duration_ticks, $outcome)
            ON CONFLICT(capture_id, node_id) DO UPDATE SET
                required = excluded.required,
                dependencies_json = excluded.dependencies_json,
                recipe_name = excluded.recipe_name,
                output_role = excluded.output_role,
                output_variant = excluded.output_variant,
                plan_sha256 = excluded.plan_sha256,
                status = excluded.status,
                reason = excluded.reason,
                attempt = excluded.attempt,
                input_evidence_version = excluded.input_evidence_version,
                processing_profile_identity_sha256 = excluded.processing_profile_identity_sha256,
                started_unix_ms = excluded.started_unix_ms,
                completed_unix_ms = excluded.completed_unix_ms,
                duration_ticks = excluded.duration_ticks,
                outcome = excluded.outcome;
            """;
            command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            command.Parameters.AddWithValue("$node_id", node.Id);
            command.Parameters.AddWithValue("$required", node.Required ? 1 : 0);
            command.Parameters.AddWithValue("$dependencies_json", JsonSerializer.Serialize(node.Dependencies, SerializerOptions));
            command.Parameters.AddWithValue("$recipe_name", (object?)node.RecipeName ?? DBNull.Value);
            command.Parameters.AddWithValue("$output_role", node.OutputRole?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$output_variant", (object?)node.OutputVariant ?? DBNull.Value);
            command.Parameters.AddWithValue("$plan_sha256", node.PlanSha256);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$attempt", attempt);
            command.Parameters.AddWithValue("$processing_profile", (object?)processingProfileIdentitySha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$started_unix_ms", startedUtc is null ? DBNull.Value : startedUtc.Value.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$completed_unix_ms", completedUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$duration_ticks", duration is null ? DBNull.Value : duration.Value.Ticks);
            command.Parameters.AddWithValue("$outcome", outcome?.ToString() ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using (var deleteInputs = connection.CreateCommand())
            {
                deleteInputs.Transaction = transaction;
                deleteInputs.CommandText = "DELETE FROM processing_node_inputs WHERE capture_id = $capture_id AND node_id = $node_id;";
                deleteInputs.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
                deleteInputs.Parameters.AddWithValue("$node_id", node.Id);
                await deleteInputs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var input in inputs)
            {
                await InsertInputAsync(connection, transaction, captureId, node.Id, input, cancellationToken).ConfigureAwait(false);
            }
        }
        if (execution is not null)
        {
            await CompleteExecutionNodeCoreAsync(
                connection,
                transaction,
                execution,
                node,
                status,
                reason,
                attempt,
                completedUtc,
                duration,
                outcome,
                outputs,
                outputPublication,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The selected query is one of two fixed internal statements and all values remain parameterized.")]
    internal async ValueTask<IReadOnlyList<DurableCaptureProduct>> ReadCaptureProductsAsync(
        Guid captureId,
        string? productSchemaVersion,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(captureId, Guid.Empty);
        if (maximumCount is < 1 or > MaximumProductQueryCount) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (productSchemaVersion is { Length: < 1 or > 128 }) throw new ArgumentOutOfRangeException(nameof(productSchemaVersion));

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = productSchemaVersion is null ? """
             SELECT output_identity_sha256, artifact_id, capture_id, node_id, role, variant,
                    product_kind, product_schema_version, content_identity_sha256,
                    availability_state, availability_reason
            FROM processing_outputs AS output
            WHERE capture_id = $capture_id
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
            ORDER BY output_identity_sha256
            LIMIT $limit;
            """ : """
             SELECT output_identity_sha256, artifact_id, capture_id, node_id, role, variant,
                    product_kind, product_schema_version, content_identity_sha256,
                    availability_state, availability_reason
            FROM processing_outputs AS output
            WHERE capture_id = $capture_id AND product_schema_version = $schema
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
            ORDER BY output_identity_sha256
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$schema", (object?)productSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var products = new List<DurableCaptureProduct>(maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            products.Add(new DurableCaptureProduct(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                reader.GetString(3),
                Enum.Parse<FrameArtifactRole>(reader.GetString(4)),
                reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                    ? null : Enum.Parse<ProcessingProductKind>(reader.GetString(6)),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
                 await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(8),
                 reader.GetString(9),
                 await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(10)));
        }
        return products;
    }

    internal async ValueTask<DurableProcessingOutput?> ReadOutputByArtifactIdAsync(
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(artifactId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                   algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                   product_kind, product_schema_version, content_identity_sha256,
                   availability_state, availability_reason, frame_artifact_recipe_version
            FROM processing_outputs
            WHERE artifact_id = $artifact_id
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
        var rows = await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0].Output,
            _ => throw new InvalidDataException("A durable artifact identity is ambiguous.")
        };
    }

    internal async ValueTask<IReadOnlyList<DurableProcessingOutputSource>> ReadOutputSourcesAsync(
        string outputIdentitySha256,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (outputIdentitySha256 is not { Length: 64 }) throw new ArgumentOutOfRangeException(nameof(outputIdentitySha256));
        if (maximumCount is < 1 or > MaximumOutputSourceCount) throw new ArgumentOutOfRangeException(nameof(maximumCount));

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, source_ordinal, source_artifact_id
            FROM processing_output_sources
            WHERE output_identity_sha256 = $output
            ORDER BY source_ordinal
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$output", outputIdentitySha256);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var sources = new List<DurableProcessingOutputSource>(maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new(reader.GetString(0), reader.GetInt32(1), Guid.ParseExact(reader.GetString(2), "N")));
        }
        return sources;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated placeholders contain only bounded integer ordinals and capture identities remain parameterized.")]
    internal async ValueTask<IReadOnlySet<Guid>> ReadCanonicalSceneCapturesAsync(
        IReadOnlyList<Guid> captureIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureIds);
        if (captureIds.Count > 101) throw new ArgumentOutOfRangeException(nameof(captureIds));
        if (captureIds.Count == 0) return new HashSet<Guid>();

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var placeholders = string.Join(", ", Enumerable.Range(0, captureIds.Count).Select(static index => $"$capture{index}"));
        command.CommandText = $"""
            SELECT DISTINCT capture_id
            FROM processing_outputs INDEXED BY ix_processing_outputs_product
            WHERE capture_id IN ({placeholders})
              AND product_schema_version = 'projected-scene-v1'
              AND product_kind = 'Metadata'
              AND content_identity_sha256 IS NOT NULL
              AND availability_state = 'Available'
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = processing_outputs.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = processing_outputs.output_identity_sha256
                                AND association.published_flag = 1))
            LIMIT 101;
            """;
        AddCaptureParameters(command, captureIds);
        var values = new HashSet<Guid>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return values;
    }

    internal async ValueTask<ProcessingEvidencePage> ReadProcessingEvidencePageAsync(
        string? afterOutputIdentitySha256,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, capture_id, role, payload_relative_path,
                   sidecar_relative_path, descriptor_json, committed_unix_ms,
                   availability_state, availability_reason, unavailable_unix_ms,
                   quarantine_relative_path
            FROM processing_outputs
            WHERE $after IS NULL OR output_identity_sha256 > $after
            ORDER BY output_identity_sha256
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", (object?)afterOutputIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var values = new List<DurableProcessingEvidence>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(
                reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"), Enum.Parse<FrameArtifactRole>(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5),
                await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false),
                reader.GetInt64(7), reader.GetString(8),
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9),
                await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(10),
                await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11)));
        }
        return new(values, values.Count == maximumCount ? values[^1].OutputIdentitySha256 : null);
    }

    internal async ValueTask<string?> ReadReconciliationCursorAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT output_identity_sha256 FROM processing_reconciliation_state WHERE state_key = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async ValueTask SetReconciliationCursorAsync(string? cursor, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_reconciliation_state(state_key, output_identity_sha256)
            VALUES(1, $cursor)
            ON CONFLICT(state_key) DO UPDATE SET output_identity_sha256 = excluded.output_identity_sha256;
            """;
        command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<string?> ReadFileCursorAsync(string column, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (column == "modern") command.CommandText = "SELECT modern_sidecar_relative_path FROM processing_reconciliation_state WHERE state_key = 1;";
        else if (column == "payload") command.CommandText = "SELECT payload_relative_path FROM processing_reconciliation_state WHERE state_key = 1;";
        else throw new ArgumentOutOfRangeException(nameof(column));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async ValueTask SetFileCursorAsync(string column, string? cursor, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (column == "modern") command.CommandText = "UPDATE processing_reconciliation_state SET modern_sidecar_relative_path = $cursor WHERE state_key = 1;";
        else if (column == "payload") command.CommandText = "UPDATE processing_reconciliation_state SET payload_relative_path = $cursor WHERE state_key = 1;";
        else throw new ArgumentOutOfRangeException(nameof(column));
        command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await SetReconciliationCursorAsync(null, cancellationToken).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<ProcessingExpirationPage> ReadAvailableExpirationPageAsync(
        DateTimeOffset cutoffUtc,
        long? afterTimestamp,
        string? afterOutputIdentitySha256,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, capture_id, role, payload_relative_path,
                   sidecar_relative_path, descriptor_json, committed_unix_ms,
                   availability_state, availability_reason, unavailable_unix_ms,
                   quarantine_relative_path,
                   committed_unix_ms AS expiration_unix_ms
            FROM processing_outputs INDEXED BY ix_processing_outputs_retention_available
            WHERE availability_state = 'Available' AND committed_unix_ms < $cutoff
              AND ($after_timestamp IS NULL OR
                   committed_unix_ms > $after_timestamp OR
                   (committed_unix_ms = $after_timestamp AND output_identity_sha256 > $after_output))
            ORDER BY committed_unix_ms, output_identity_sha256
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$after_timestamp", afterTimestamp.HasValue ? afterTimestamp.Value : DBNull.Value);
        command.Parameters.AddWithValue("$after_output", (object?)afterOutputIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var values = new List<DurableProcessingEvidence>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"), Enum.Parse<FrameArtifactRole>(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5),
                await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false),
                reader.GetInt64(7), reader.GetString(8),
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9),
                await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(10),
                await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11)));
        var last = values.LastOrDefault();
        return new(values,
            values.Count == maximumCount ? last!.CommittedUnixMilliseconds : null,
            values.Count == maximumCount ? last!.OutputIdentitySha256 : null);
    }

    internal async ValueTask<ProcessingExpirationPage> ReadUnavailableExpirationPageAsync(
        DateTimeOffset cutoffUtc, long? afterTimestamp, string? afterOutputIdentitySha256,
        int maximumCount, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, capture_id, role, payload_relative_path,
                   sidecar_relative_path, descriptor_json, committed_unix_ms,
                   availability_state, availability_reason, unavailable_unix_ms,
                   quarantine_relative_path
            FROM processing_outputs INDEXED BY ix_processing_outputs_retention_unavailable
            WHERE availability_state <> 'Available' AND unavailable_unix_ms < $cutoff
              AND ($after_timestamp IS NULL OR unavailable_unix_ms > $after_timestamp OR
                   (unavailable_unix_ms = $after_timestamp AND output_identity_sha256 > $after_output))
            ORDER BY unavailable_unix_ms, output_identity_sha256 LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$after_timestamp", afterTimestamp.HasValue ? afterTimestamp.Value : DBNull.Value);
        command.Parameters.AddWithValue("$after_output", (object?)afterOutputIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var values = new List<DurableProcessingEvidence>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"), Guid.ParseExact(reader.GetString(2), "N"),
                Enum.Parse<FrameArtifactRole>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false),
                reader.GetInt64(7), reader.GetString(8),
                await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9),
                reader.GetInt64(10), await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11)));
        var last = values.LastOrDefault();
        return new(values, values.Count == maximumCount ? last!.UnavailableUnixMilliseconds : null,
            values.Count == maximumCount ? last!.OutputIdentitySha256 : null);
    }

    internal async ValueTask SetOutputAvailabilityAsync(
        string outputIdentitySha256,
        string state,
        string? reason,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE processing_outputs
            SET availability_state = $state, availability_reason = $reason,
                unavailable_unix_ms = CASE WHEN $state = 'Available' THEN NULL ELSE COALESCE(unavailable_unix_ms, $now) END,
                quarantine_relative_path = CASE WHEN $state = 'Available' THEN NULL ELSE quarantine_relative_path END
            WHERE output_identity_sha256 = $output;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", outputIdentitySha256);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Processing output availability target is missing.");
    }

    internal async ValueTask<bool> RestoreMissingOutputAvailableAsync(
        string outputIdentitySha256, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        string? captureId = null;
        string? nodeId = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT capture_id, node_id FROM processing_outputs
                WHERE output_identity_sha256 = $output AND availability_state = 'Missing';
                """;
            read.Parameters.AddWithValue("$output", outputIdentitySha256);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                captureId = reader.GetString(0);
                nodeId = reader.GetString(1);
            }
        }
        if (captureId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        using (var output = connection.CreateCommand())
        {
            output.Transaction = transaction;
            output.CommandText = """
                UPDATE processing_outputs
                SET availability_state = 'Available', availability_reason = NULL,
                    unavailable_unix_ms = NULL, quarantine_relative_path = NULL
                WHERE output_identity_sha256 = $output AND availability_state = 'Missing';
                """;
            output.Parameters.AddWithValue("$output", outputIdentitySha256);
            await output.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var completed = false;
        using (var node = connection.CreateCommand())
        {
            node.Transaction = transaction;
            node.CommandText = """
                UPDATE processing_nodes
                SET status = 'Completed', reason = NULL, outcome = 'Produced'
                WHERE capture_id = $capture AND node_id = $node
                  AND status = 'TerminalFailure' AND reason = 'processing.output-unavailable'
                  AND NOT EXISTS (
                      SELECT 1 FROM processing_outputs output
                      WHERE output.capture_id = $capture AND output.node_id = $node
                        AND output.availability_state <> 'Available');
                """;
            node.Parameters.AddWithValue("$capture", captureId);
            node.Parameters.AddWithValue("$node", nodeId);
            completed = await node.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    internal async ValueTask<UnavailableOutputTransition> TransitionOutputUnavailableAsync(
        string outputIdentitySha256,
        string state,
        string reason,
        string? quarantineRelativePath,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        string? captureId = null;
        string? nodeId = null;
        string? productSchema = null;
        string? currentState = null;
        string? currentReason = null;
        byte[]? evidence = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT capture_id, node_id, product_schema_version, descriptor_json,
                       availability_state, availability_reason, unavailable_unix_ms
                FROM processing_outputs WHERE output_identity_sha256 = $output;
                """;
            read.Parameters.AddWithValue("$output", outputIdentitySha256);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                captureId = reader.GetString(0);
                nodeId = reader.GetString(1);
                productSchema = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2);
                evidence = await reader.GetFieldValueAsync<byte[]>(3, cancellationToken).ConfigureAwait(false);
                currentState = reader.GetString(4);
                currentReason = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
            }
        }
        if (captureId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(false);
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var unchanged = string.Equals(currentState, state, StringComparison.Ordinal) &&
            string.Equals(currentReason, reason, StringComparison.Ordinal);
        if (!unchanged)
        {
            using (var diagnostic = connection.CreateCommand())
            {
                diagnostic.Transaction = transaction;
                diagnostic.CommandText = """
                    INSERT INTO processing_output_diagnostics(
                        output_identity_sha256, capture_id, node_id, availability_state,
                        availability_reason, descriptor_json, quarantine_relative_path,
                        recorded_unix_ms)
                    VALUES($output, $capture, $node, $state, $reason, $evidence, $quarantine, $now);
                    DELETE FROM processing_output_diagnostics
                    WHERE output_identity_sha256 = $output AND diagnostic_id NOT IN (
                        SELECT diagnostic_id FROM processing_output_diagnostics
                        WHERE output_identity_sha256 = $output
                        ORDER BY recorded_unix_ms DESC, diagnostic_id DESC LIMIT 16);
                    """;
                diagnostic.Parameters.AddWithValue("$output", outputIdentitySha256);
                diagnostic.Parameters.AddWithValue("$capture", captureId);
                diagnostic.Parameters.AddWithValue("$node", nodeId);
                diagnostic.Parameters.AddWithValue("$state", state);
                diagnostic.Parameters.AddWithValue("$reason", reason);
                diagnostic.Parameters.AddWithValue("$evidence", evidence!);
                diagnostic.Parameters.AddWithValue("$quarantine", (object?)quarantineRelativePath ?? DBNull.Value);
                diagnostic.Parameters.AddWithValue("$now", now);
                await diagnostic.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        var deterministic = string.Equals(productSchema, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            if (deterministic)
            {
                update.CommandText = """
                    DELETE FROM processing_outputs WHERE output_identity_sha256 = $output;
                    UPDATE processing_nodes SET status = 'RetryableFailure', reason = 'processing.output-unavailable'
                    WHERE capture_id = $capture AND node_id = $node;
                    UPDATE capture_lane_work
                    SET state = 'pending', available_unix_ms = $now,
                        lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                        completion_token = NULL, completed_unix_ms = NULL,
                        failure_reason = 'processing-output-recovery', updated_unix_ms = $now
                    WHERE lane_name = 'standard' AND raw_capture_row_id = (
                        SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture)
                      AND state IN ('completed', 'quarantined', 'abandoned');
                    UPDATE raw_captures SET retention_hold = 1 WHERE capture_id = $capture;
                    """;
            }
            else
            {
                update.CommandText = """
                    UPDATE processing_outputs
                    SET availability_state = $state, availability_reason = $reason,
                        unavailable_unix_ms = COALESCE(unavailable_unix_ms, $now),
                        quarantine_relative_path = $quarantine
                    WHERE output_identity_sha256 = $output;
                    UPDATE processing_nodes SET status = 'TerminalFailure', reason = 'processing.output-unavailable'
                    WHERE capture_id = $capture AND node_id = $node;
                    """;
            }
            update.Parameters.AddWithValue("$output", outputIdentitySha256);
            update.Parameters.AddWithValue("$capture", captureId);
            update.Parameters.AddWithValue("$node", nodeId);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$quarantine", (object?)quarantineRelativePath ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(deterministic);
    }

    internal async ValueTask<ProcessingAvailabilityInventory> ReadAvailabilityInventoryAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(CASE WHEN availability_state = 'Missing' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN availability_state = 'Quarantined' THEN 1 ELSE 0 END)
            FROM processing_outputs;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(
            await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(1));
    }

    internal async ValueTask<ProcessingDiagnosticPage> ReadDiagnosticExpirationPageAsync(
        DateTimeOffset cutoffUtc, long? afterRecorded, long? afterId, int maximumCount, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT diagnostic_id, quarantine_relative_path, recorded_unix_ms
            FROM processing_output_diagnostics
            WHERE recorded_unix_ms < $cutoff AND
                  ($after IS NULL OR recorded_unix_ms > $after OR
                   (recorded_unix_ms = $after AND diagnostic_id > $id))
            ORDER BY recorded_unix_ms, diagnostic_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$after", afterRecorded.HasValue ? afterRecorded.Value : DBNull.Value);
        command.Parameters.AddWithValue("$id", afterId.HasValue ? afterId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var values = new List<ProcessingDiagnostic>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(reader.GetInt64(0), await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1), reader.GetInt64(2)));
        var last = values.LastOrDefault();
        return new(values, values.Count == maximumCount ? last!.RecordedUnixMilliseconds : null,
            values.Count == maximumCount ? last!.DiagnosticId : null);
    }

    internal async ValueTask DeleteDiagnosticAsync(long diagnosticId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processing_output_diagnostics WHERE diagnostic_id = $id;";
        command.Parameters.AddWithValue("$id", diagnosticId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool> MatchesRawSourceAsync(
        Guid captureId,
        Guid artifactId,
        string descriptorIdentitySha256,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM raw_captures
                WHERE capture_id = $capture AND raw_artifact_id = $artifact
                  AND descriptor_sha256 = $descriptor);
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$descriptor", descriptorIdentitySha256);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    internal async ValueTask<bool> IsProcessingPathClaimedAsync(string relativePath, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM processing_outputs
                WHERE payload_relative_path = $path OR sidecar_relative_path = $path);
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    internal ValueTask<ProcessingLifecyclePage> ReadActionableLifecyclePageAsync(
        string? afterOperationId, int maximumCount, CancellationToken cancellationToken)
        => ReadLifecyclePageAsync(orphan: false, afterOperationId, maximumCount, cancellationToken);

    internal ValueTask<ProcessingLifecyclePage> ReadOrphanLifecyclePageAsync(
        string? afterOperationId, int maximumCount, CancellationToken cancellationToken)
        => ReadLifecyclePageAsync(orphan: true, afterOperationId, maximumCount, cancellationToken);

    private async ValueTask<ProcessingLifecyclePage> ReadLifecyclePageAsync(
        bool orphan, string? afterOperationId, int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, kind, output_identity_sha256, source_relative_path,
                   companion_relative_path, destination_relative_path, reason, observed_bytes,
                   planned_unix_ms, phase
            FROM processing_lifecycle_operations
            WHERE (($orphan = 1 AND kind = 'orphan') OR ($orphan = 0 AND kind <> 'orphan'))
              AND ($after IS NULL OR operation_id > $after)
            ORDER BY operation_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$orphan", orphan ? 1 : 0);
        command.Parameters.AddWithValue("$after", (object?)afterOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var values = new List<ProcessingLifecycleOperation>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(reader.GetString(0), reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9)));
        return new(values, values.Count == maximumCount ? values[^1].OperationId : null);
    }

    internal async ValueTask PlanLifecycleOperationAsync(
        ProcessingLifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_lifecycle_operations(
                operation_id, kind, output_identity_sha256, source_relative_path,
                companion_relative_path, destination_relative_path, reason, observed_bytes, planned_unix_ms, phase)
            VALUES($id, $kind, $output, $source, $companion, $destination, $reason, $bytes, $now, $phase)
            ON CONFLICT(operation_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", operation.OperationId);
        command.Parameters.AddWithValue("$kind", operation.Kind);
        command.Parameters.AddWithValue("$output", (object?)operation.OutputIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)operation.SourceRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$companion", (object?)operation.CompanionRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination", operation.DestinationRelativePath);
        command.Parameters.AddWithValue("$reason", operation.Reason);
        command.Parameters.AddWithValue("$bytes", operation.ObservedBytes);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$phase", operation.Phase);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask SetLifecyclePhaseAsync(string operationId, string phase, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE processing_lifecycle_operations SET phase = $phase WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddWithValue("$id", operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Processing lifecycle operation is missing.");
    }

    internal async ValueTask CompleteLifecycleOperationAsync(
        ProcessingLifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (operation.OutputIdentitySha256 is { } output)
        {
            string? owningCapture = null;
            string? owningNode = null;
            if (operation.Kind == "delete")
            {
                using var owner = connection.CreateCommand();
                owner.Transaction = transaction;
                owner.CommandText = "SELECT capture_id, node_id FROM processing_outputs WHERE output_identity_sha256 = $output;";
                owner.Parameters.AddWithValue("$output", output);
                using var ownerReader = await owner.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await ownerReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    owningCapture = ownerReader.GetString(0);
                    owningNode = ownerReader.GetString(1);
                }
            }
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            if (operation.Kind == "delete")
            {
                update.CommandText = "DELETE FROM processing_outputs WHERE output_identity_sha256 = $output;";
            }
            else
            {
                update.CommandText = """
                    UPDATE processing_outputs
                    SET availability_state = 'Quarantined', availability_reason = $reason,
                        unavailable_unix_ms = COALESCE(unavailable_unix_ms, $now),
                        quarantine_relative_path = $destination
                    WHERE output_identity_sha256 = $output;
                    """;
            }
            update.Parameters.AddWithValue("$output", output);
            update.Parameters.AddWithValue("$reason", operation.Reason);
            update.Parameters.AddWithValue("$now", operation.PlannedUnixMilliseconds == 0
                ? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() : operation.PlannedUnixMilliseconds);
            update.Parameters.AddWithValue("$destination", operation.DestinationRelativePath);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (operation.Kind == "delete" && owningCapture is not null)
            {
                using var deleteNode = connection.CreateCommand();
                deleteNode.Transaction = transaction;
                deleteNode.CommandText = """
                    DELETE FROM processing_nodes
                    WHERE capture_id = $capture AND node_id = $node
                      AND NOT EXISTS (
                          SELECT 1 FROM processing_outputs
                          WHERE capture_id = $capture AND node_id = $node)
                      AND NOT EXISTS (
                          SELECT 1 FROM capture_lane_work work
                          JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                          WHERE raw.capture_id = $capture AND work.lane_name = 'standard'
                            AND work.state IN ('pending', 'leased', 'retry_wait'));
                    """;
                deleteNode.Parameters.AddWithValue("$capture", owningCapture);
                deleteNode.Parameters.AddWithValue("$node", owningNode!);
                await deleteNode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (operation.Kind == "delete")
            command.CommandText = "UPDATE processing_lifecycle_operations SET phase = 'database-completed' WHERE operation_id = $id;";
        else
            command.CommandText = "DELETE FROM processing_lifecycle_operations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operation.OperationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask DeleteLifecycleOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processing_lifecycle_operations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<int> DeleteClaimedOrphanOperationsAsync(
        string sidecarRelativePath, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM processing_lifecycle_operations
            WHERE kind = 'orphan' AND companion_relative_path = $sidecar
              AND EXISTS (
                  SELECT 1 FROM processing_outputs output
                  WHERE output.payload_relative_path = processing_lifecycle_operations.source_relative_path
                    AND output.sidecar_relative_path = $sidecar);
            """;
        command.Parameters.AddWithValue("$sidecar", sidecarRelativePath);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    internal async ValueTask<int> DeleteReclaimedOrphanPageAsync(
        string? afterOperationId, int maximumCount, CancellationToken cancellationToken)
    {
        var page = await ReadOrphanLifecyclePageAsync(afterOperationId, maximumCount, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0) return 0;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var deleted = 0;
        foreach (var operation in page.Items)
        {
            if (operation.SourceRelativePath is null || operation.CompanionRelativePath is null) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM processing_lifecycle_operations
                WHERE operation_id = $id AND kind = 'orphan' AND EXISTS (
                    SELECT 1 FROM processing_outputs output
                    WHERE output.payload_relative_path = $payload
                      AND output.sidecar_relative_path = $sidecar);
                """;
            command.Parameters.AddWithValue("$id", operation.OperationId);
            command.Parameters.AddWithValue("$payload", operation.SourceRelativePath);
            command.Parameters.AddWithValue("$sidecar", operation.CompanionRelativePath);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    internal async ValueTask DeleteOutputlessNodeAsync(
        Guid captureId,
        string nodeId,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureLeaseAsync(connection, transaction, workId, leaseToken, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM processing_nodes
            WHERE capture_id = $capture_id AND node_id = $node_id
              AND NOT EXISTS (
                  SELECT 1 FROM processing_outputs
                  WHERE capture_id = $capture_id AND node_id = $node_id);
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM processing_nodes
                WHERE capture_id = $capture_id AND node_id = $node_id);
            """;
        verify.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        verify.Parameters.AddWithValue("$node_id", nodeId);
        if (Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("A memory-only processing node has durable outputs and cannot be cleared.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertInputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        string nodeId,
        DurableProcessingNodeInput input,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_node_inputs(
                capture_id, node_id, input_ordinal, kind, name, artifact_id, role, variant,
                recipe_identity_sha256, schema_version, identity_sha256, selected_flag)
            VALUES(
                $capture_id, $node_id, $ordinal, $kind, $name, $artifact_id, $role, $variant,
                $recipe_identity, $schema_version, $identity, $selected);
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$ordinal", input.Ordinal);
        command.Parameters.AddWithValue("$kind", input.Kind);
        command.Parameters.AddWithValue("$name", (object?)input.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact_id", input.ArtifactId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$role", input.Role?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$variant", (object?)input.Variant ?? DBNull.Value);
        command.Parameters.AddWithValue("$recipe_identity", (object?)input.RecipeIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$schema_version", (object?)input.SchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", (object?)input.IdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$selected", input.Selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingNodeInput>> ReadInputsAsync(
        SqliteConnection connection,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_ordinal, kind, name, artifact_id, role, variant,
                   recipe_identity_sha256, schema_version, identity_sha256, selected_flag
            FROM processing_node_inputs
            WHERE capture_id = $capture_id AND node_id = $node_id
            ORDER BY input_ordinal
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        var values = new List<DurableProcessingNodeInput>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new DurableProcessingNodeInput(
                reader.GetInt32(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : Guid.ParseExact(reader.GetString(3), "N"),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : Enum.Parse<FrameArtifactRole>(reader.GetString(4)),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(8),
                reader.GetBoolean(9)));
        }
        if (values.Count > 128)
        {
            throw new InvalidDataException("Processing node input evidence exceeds its durable bound.");
        }
        return values;
    }

    private async ValueTask EnsureLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long workId,
        string? leaseToken,
        CancellationToken cancellationToken)
    {
        if (workId == 0)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(leaseToken))
        {
            throw new InvalidOperationException("Durable processing requires a lane lease token.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM capture_lane_work
            WHERE work_id = $work_id AND state = 'leased' AND lease_token = $lease_token
              AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$work_id", workId);
        command.Parameters.AddWithValue("$lease_token", leaseToken);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("Capture processing lease is stale and cannot commit durable state.");
        }
    }

    internal async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadRecentOutputsAsync(
        string agentId,
        long currentCaptureSequence,
        string nodeId,
        FrameArtifactRole role,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path, output.sidecar_relative_path,
                   output.descriptor_json, output.capture_id, output.agent_id, output.node_id, output.role, output.variant,
                   output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json, output.total_integration_ticks,
                      output.capture_sequence, output.product_kind,
                      output.product_schema_version, output.content_identity_sha256,
                      output.availability_state, output.availability_reason,
                      output.frame_artifact_recipe_version
            FROM processing_outputs AS output
            WHERE output.agent_id = $agent_id
              AND output.availability_state = 'Available'
              AND output.capture_sequence <= $current_capture_sequence
              AND output.node_id = $node_id AND output.role = $role
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
            ORDER BY output.capture_sequence DESC, output.output_identity_sha256 DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$current_capture_sequence", currentCaptureSequence);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var values = (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .ToList();
        values.Reverse();
        return values;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The generated placeholders contain only bounded integer ordinals; capture identities remain parameterized.")]
    internal async ValueTask<DurableGalleryProcessingProjection> ReadGalleryNodesAsync(
        IReadOnlyList<Guid> captureIds,
        int maximumNodesPerCapture,
        int maximumOutputsPerCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureIds);
        if (captureIds.Count == 0)
        {
            return new([], new HashSet<Guid>(), new HashSet<Guid>());
        }
        if (captureIds.Count > 101)
        {
            throw new ArgumentOutOfRangeException(nameof(captureIds));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<byte[], string, string, int>(
            "gallery_preview_rank",
            GalleryPreviewRank,
            isDeterministic: true);
        var placeholders = string.Join(", ", Enumerable.Range(0, captureIds.Count).Select(static index => $"$capture{index}"));
        var nodes = new List<(Guid CaptureId, string NodeId, bool Required, DurableProcessingNodeStatus Status, string? RecipeName, string? OutputRole, string? OutputVariant)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH node_candidates AS (
                    SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant,
                           CASE WHEN EXISTS (
                               SELECT 1
                               FROM processing_outputs AS candidate
                                WHERE candidate.capture_id = processing_nodes.capture_id
                                  AND candidate.node_id = processing_nodes.node_id
                                  AND candidate.availability_state = 'Available'
                                  AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                   WHERE association.output_identity_sha256 = candidate.output_identity_sha256)
                                       OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                  WHERE association.output_identity_sha256 = candidate.output_identity_sha256
                                                    AND association.published_flag = 1))
                           ) THEN 0 ELSE 1 END AS availability_rank,
                           CASE
                                WHEN output_role IN ('Preview', 'AnnotatedPreview', 'Combined', 'Calibrated') AND EXISTS (
                                   SELECT 1
                                   FROM processing_outputs AS candidate
                                   WHERE candidate.capture_id = processing_nodes.capture_id
                                     AND candidate.node_id = processing_nodes.node_id
                                      AND candidate.role = processing_nodes.output_role
                                      AND candidate.availability_state = 'Available'
                                      AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                       WHERE association.output_identity_sha256 = candidate.output_identity_sha256)
                                           OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                      WHERE association.output_identity_sha256 = candidate.output_identity_sha256
                                                        AND association.published_flag = 1))
                                     AND gallery_preview_rank(
                                         candidate.descriptor_json,
                                         candidate.availability_state,
                                         candidate.role) = 0
                               ) THEN 0
                                WHEN output_role IN ('Preview', 'AnnotatedPreview', 'Combined', 'Calibrated') THEN 1
                               ELSE 0
                           END AS preview_rank
                    FROM processing_nodes
                    WHERE capture_id IN ({placeholders})
                ),
                role_ranked_nodes AS (
                    SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant,
                           availability_rank, preview_rank,
                           ROW_NUMBER() OVER (
                               PARTITION BY capture_id, COALESCE(output_role, '') ORDER BY
                                    availability_rank,
                                    preview_rank,
                                    node_id) AS role_rank
                    FROM node_candidates
                ),
                ranked_nodes AS (
                    SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant,
                           availability_rank, preview_rank, role_rank,
                           ROW_NUMBER() OVER (PARTITION BY capture_id ORDER BY
                               availability_rank,
                               role_rank,
                               CASE output_role
                                   WHEN 'AnnotatedPreview' THEN 0
                                   WHEN 'Preview' THEN 1
                                   WHEN 'Combined' THEN 2
                                   WHEN 'Calibrated' THEN 3
                                   ELSE 4
                               END,
                               node_id) AS gallery_rank
                    FROM role_ranked_nodes
                )
                SELECT capture_id, node_id, required, status, recipe_name, output_role, output_variant
                FROM ranked_nodes
                WHERE gallery_rank <= $maximum_nodes
                ORDER BY capture_id,
                    availability_rank,
                    role_rank,
                    CASE output_role
                        WHEN 'AnnotatedPreview' THEN 0
                        WHEN 'Preview' THEN 1
                        WHEN 'Combined' THEN 2
                        WHEN 'Calibrated' THEN 3
                        ELSE 4
                    END,
                    node_id;
                """;
            AddCaptureParameters(command, captureIds);
            command.Parameters.AddWithValue("$maximum_nodes", maximumNodesPerCapture + 1);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add((
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    Enum.Parse<DurableProcessingNodeStatus>(reader.GetString(3)),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6)));
            }
        }

        List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)> outputRows;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH role_ranked_outputs AS (
                    SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                           descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                           algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                           product_kind, product_schema_version, content_identity_sha256,
                               availability_state, availability_reason, frame_artifact_recipe_version,
                            ROW_NUMBER() OVER (
                                PARTITION BY capture_id, role ORDER BY
                                    CASE availability_state WHEN 'Available' THEN 0 ELSE 1 END,
                                    gallery_preview_rank(descriptor_json, availability_state, role),
                                    node_id,
                                    output_identity_sha256) AS role_rank
                    FROM processing_outputs AS output
                    WHERE capture_id IN ({placeholders})
                      AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                       WHERE association.output_identity_sha256 = output.output_identity_sha256)
                           OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                      WHERE association.output_identity_sha256 = output.output_identity_sha256
                                        AND association.published_flag = 1))
                ),
                ranked_outputs AS (
                    SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                           descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                           algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                           product_kind, product_schema_version, content_identity_sha256,
                           availability_state, availability_reason, frame_artifact_recipe_version, role_rank,
                            ROW_NUMBER() OVER (
                                 PARTITION BY capture_id ORDER BY
                                     CASE availability_state WHEN 'Available' THEN 0 ELSE 1 END,
                                     role_rank,
                                     CASE role
                                         WHEN 'AnnotatedPreview' THEN 0
                                        WHEN 'Preview' THEN 1
                                        WHEN 'Combined' THEN 2
                                        WHEN 'Calibrated' THEN 3
                                        ELSE 4
                                    END,
                                     node_id,
                                     output_identity_sha256) AS gallery_rank
                    FROM role_ranked_outputs
                )
                SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                       descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                         algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                         product_kind, product_schema_version, content_identity_sha256,
                         availability_state, availability_reason, frame_artifact_recipe_version
                FROM ranked_outputs
                WHERE gallery_rank <= $maximum_outputs
                ORDER BY capture_id,
                    CASE availability_state WHEN 'Available' THEN 0 ELSE 1 END,
                    role_rank,
                    CASE role
                        WHEN 'AnnotatedPreview' THEN 0
                        WHEN 'Preview' THEN 1
                        WHEN 'Combined' THEN 2
                        WHEN 'Calibrated' THEN 3
                        ELSE 4
                    END,
                    node_id,
                    output_identity_sha256;
                """;
            AddCaptureParameters(command, captureIds);
            command.Parameters.AddWithValue("$maximum_outputs", maximumOutputsPerCapture + 1);
            outputRows = await ReadOutputRowsAsync(command, cancellationToken, skipInvalid: true).ConfigureAwait(false);
        }

        var truncatedNodes = nodes.GroupBy(static node => node.CaptureId)
            .Where(group => group.Count() > maximumNodesPerCapture)
            .Select(static group => group.Key)
            .ToHashSet();
        var retainedNodes = nodes.GroupBy(static node => node.CaptureId)
            .SelectMany(group => group.Take(maximumNodesPerCapture))
            .ToArray();
        var retainedNodeKeys = retainedNodes
            .Select(static node => (node.CaptureId, node.NodeId))
            .ToHashSet();
        var truncatedOutputs = outputRows.GroupBy(static row => row.CaptureId)
            .Where(group => group.Count() > maximumOutputsPerCapture)
            .Select(static group => group.Key)
            .ToHashSet();
        truncatedOutputs.UnionWith(outputRows
            .Where(row => !retainedNodeKeys.Contains((row.CaptureId, row.NodeId)))
            .Select(static row => row.CaptureId));
        outputRows = outputRows.GroupBy(static row => row.CaptureId)
            .SelectMany(group => group.Take(maximumOutputsPerCapture))
            .ToList();
        var outputs = outputRows.ToLookup(static row => (row.CaptureId, row.NodeId), static row => row.Output);
        var projected = retainedNodes.Select(node => new DurableGalleryProcessingNode(
            node.CaptureId,
            node.NodeId,
            node.Required,
            node.Status,
            node.RecipeName,
            node.OutputRole,
            node.OutputVariant,
            outputs[(node.CaptureId, node.NodeId)].ToArray())).ToArray();
        return new(projected, truncatedNodes, truncatedOutputs);
    }

    private int GalleryPreviewRank(byte[] descriptorJson, string availability, string roleValue)
    {
        if (!string.Equals(availability, "Available", StringComparison.Ordinal) ||
            !Enum.TryParse<FrameArtifactRole>(roleValue, out var role) ||
            role is not (FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview or
                FrameArtifactRole.Combined or FrameArtifactRole.Calibrated))
        {
            return 1;
        }
        try
        {
            using var evidence = JsonDocument.Parse(descriptorJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object ||
                !evidence.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return 1;
            }
            ReconstructionDescriptor? descriptor = null;
            IDurableProcessingProductManifest? productManifest = null;
            if (schema.GetString() is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                DurableEncodedProductManifestV2.CurrentSchemaVersion or
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion)
            {
                productManifest = DurableProcessingProductManifestJson.Parse(descriptorJson);
            }
            else
            {
                var parsed = CaptureContractJson.ParseManifest(descriptorJson);
                descriptor = parsed.Document?.Manifest?.Descriptor;
                if (!parsed.IsValid || descriptor is null)
                {
                    return 1;
                }
            }
            var artifact = descriptor?.Artifact ?? productManifest!.Artifact;
            if (artifact.Role != role)
            {
                return 1;
            }
            var encoded = productManifest as DurableEncodedProductManifestV2;
            var layout = descriptor?.Layout;
            return CameraAgentPreviewEligibilityPolicy.Evaluate(
                role,
                artifact.MediaType,
                layout?.ByteLength ?? productManifest?.ByteLength,
                layout?.PixelFormat ?? encoded?.EncodedPixelFormat,
                layout is not null && CameraAgentPreviewEligibilityPolicy.IsSupportedLayout(layout),
                encoded?.EncodedWidth,
                encoded?.EncodedHeight,
                _artifactRead) == CameraAgentPreviewEligibility.Available
                    ? 0
                    : 1;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or
                                          InvalidOperationException or OverflowException)
        {
            return 1;
        }
    }

    internal async ValueTask<IReadOnlyList<DurableGalleryProcessingNodeDetail>> ReadGalleryNodeDetailsAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, dependencies_json, attempt, completed_unix_ms, reason,
                   processing_profile_identity_sha256, started_unix_ms, duration_ticks, outcome,
                   input_evidence_version
            FROM processing_nodes
            WHERE capture_id = $capture_id
            ORDER BY node_id
            LIMIT 65;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        var rows = new List<(string NodeId, string[] Dependencies, int Attempt, DateTimeOffset CompletedUtc,
            string? Reason, string? Profile, DateTimeOffset? StartedUtc, TimeSpan? Duration,
            ProcessingOutcomeStatus? Outcome, bool HasInputs)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dependencies = JsonSerializer.Deserialize<string[]>(reader.GetString(1), SerializerOptions)
                ?? throw new InvalidDataException("Processing dependencies are invalid.");
            if (dependencies.Length > 64 || dependencies.Any(static dependency =>
                    string.IsNullOrWhiteSpace(dependency) || dependency.Length > 128))
            {
                throw new InvalidDataException("Processing dependencies exceed gallery bounds.");
            }
            rows.Add((
                reader.GetString(0),
                dependencies,
                reader.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                    ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                    ? null : TimeSpan.FromTicks(reader.GetInt64(7)),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                    ? null : Enum.Parse<ProcessingOutcomeStatus>(reader.GetString(8)),
                !await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        var details = new List<DurableGalleryProcessingNodeDetail>(Math.Min(64, rows.Count));
        foreach (var row in rows.Take(64))
        {
            var inputs = row.HasInputs
                ? await ReadInputsAsync(connection, captureId, row.NodeId, cancellationToken).ConfigureAwait(false)
                : null;
            details.Add(new DurableGalleryProcessingNodeDetail(
                row.NodeId, row.Dependencies, row.Attempt, row.CompletedUtc, row.Reason, row.Profile,
                row.StartedUtc, row.Duration, row.Outcome,
                inputs?.Take(MaximumGalleryInputsPerNode).ToArray(),
                inputs is not null && inputs.Count > MaximumGalleryInputsPerNode));
        }
        return details;
    }

    private async ValueTask PopulateLiveOutputWindowCandidateHoldsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS temp_live_output_window_candidates(
                    artifact_id TEXT PRIMARY KEY CHECK(length(artifact_id) = 32)
                ) WITHOUT ROWID;
                DELETE FROM temp_live_output_window_candidates;
                """;
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var windows = new List<(string RevisionId, string AgentId, long CaptureSequence,
            string ProducerNodeId, string ProducerPlanSha256)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = PendingLiveOutputWindowsSql;
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                windows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        var maximumCandidates = ProcessingOutputWindowSelector.GetCandidateScanCount(
            _executionOptions.MaximumWindowInputs);
        foreach (var window in windows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = InsertLiveOutputWindowCandidatesSql;
            insert.Parameters.AddWithValue("$agent", window.AgentId);
            insert.Parameters.AddWithValue("$current_capture_sequence", window.CaptureSequence);
            insert.Parameters.AddWithValue("$producer_node_id", window.ProducerNodeId);
            insert.Parameters.AddWithValue("$producer_plan_sha256", window.ProducerPlanSha256);
            insert.Parameters.AddWithValue("$graph_revision_id", window.RevisionId);
            insert.Parameters.AddWithValue("$maximum_candidates", maximumCandidates);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<IReadOnlyDictionary<Guid, bool>> ReadGalleryRetentionStatesAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await PopulateLiveOutputWindowCandidateHoldsAsync(connection, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.artifact_id,
                   CASE WHEN (
                       SELECT COUNT(*)
                       FROM processing_outputs newer
                       WHERE newer.agent_id = output.agent_id AND newer.node_id = output.node_id
                         AND (newer.capture_sequence > output.capture_sequence OR
                              (newer.capture_sequence = output.capture_sequence AND
                               newer.output_identity_sha256 > output.output_identity_sha256))) < $maximum_outputs
                     OR EXISTS (
                       SELECT 1 FROM temp_live_output_window_candidates candidate
                       WHERE candidate.artifact_id = output.artifact_id)
                     OR EXISTS (
                       SELECT 1
                       FROM raw_captures raw
                       JOIN capture_lane_work work ON work.raw_capture_row_id = raw.raw_capture_row_id
                       WHERE raw.capture_id = output.capture_id AND work.lane_name = 'standard'
                         AND work.state NOT IN ('completed', 'abandoned'))
                   THEN 1 ELSE 0 END
            FROM processing_outputs output
            WHERE output.capture_id = $capture_id
            LIMIT 129;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$maximum_outputs", ProcessingOutputRetentionCount);
        var values = new Dictionary<Guid, bool>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (values.Count == 128)
            {
                break;
            }
            if (Guid.TryParseExact(reader.GetString(0), "N", out var artifactId))
            {
                values[artifactId] = reader.GetBoolean(1);
            }
        }
        return values;
    }

    private static void AddCaptureParameters(SqliteCommand command, IReadOnlyList<Guid> captureIds)
    {
        for (var index = 0; index < captureIds.Count; index++)
        {
            command.Parameters.AddWithValue($"$capture{index}", captureIds[index].ToString("N"));
        }
    }

    internal async ValueTask<IReadOnlyList<ProcessingRetentionHold>> ReadRetentionHoldsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var safety = connection.CreateCommand())
        {
            safety.CommandText = """
                WITH RECURSIVE walk(root_artifact_id, artifact_id, path, depth, cycle) AS (
                    SELECT artifact_id, artifact_id, '/' || artifact_id || '/', 0, 0
                    FROM processing_outputs
                    WHERE availability_state = 'Available'
                    UNION ALL
                    SELECT walk.root_artifact_id, source.source_artifact_id,
                           walk.path || source.source_artifact_id || '/', walk.depth + 1,
                           instr(walk.path, '/' || source.source_artifact_id || '/') > 0
                    FROM walk
                    JOIN processing_outputs output ON output.artifact_id = walk.artifact_id
                    JOIN processing_output_sources source
                      ON source.output_identity_sha256 = output.output_identity_sha256
                    WHERE walk.depth < 512 AND walk.cycle = 0
                )
                SELECT COALESCE(MAX(cycle), 0), COALESCE(MAX(depth), 0) FROM walk;
                """;
            using var safetyReader = await safety.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await safetyReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (safetyReader.GetInt64(0) != 0 || safetyReader.GetInt64(1) >= 512)
                throw new InvalidDataException("Processing retention lineage contains a cycle or exceeds its traversal bound.");
        }
        await PopulateLiveOutputWindowCandidateHoldsAsync(connection, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE ranked AS (
                SELECT artifact_id, capture_id, availability_state,
                       ROW_NUMBER() OVER (PARTITION BY agent_id, node_id ORDER BY capture_sequence DESC, output_identity_sha256 DESC) AS rank
                FROM processing_outputs
                WHERE availability_state = 'Available'
            ), raw_ranked AS (
                SELECT raw_artifact_id,
                       ROW_NUMBER() OVER (PARTITION BY agent_id ORDER BY capture_sequence DESC) AS rank
                FROM raw_captures
                WHERE state = 'committed'
            ), roots(artifact_id) AS (
                SELECT artifact_id FROM ranked
                WHERE rank <= $maximum_outputs OR EXISTS (
                    SELECT 1 FROM raw_captures raw
                    JOIN capture_lane_work work ON work.raw_capture_row_id = raw.raw_capture_row_id
                    WHERE raw.capture_id = ranked.capture_id AND work.lane_name = 'standard'
                      AND work.state NOT IN ('completed', 'abandoned'))
                UNION
                SELECT artifact_id FROM temp_live_output_window_candidates
                UNION
                SELECT raw_artifact_id FROM raw_ranked WHERE rank <= 100
                UNION
                SELECT output.artifact_id
                FROM processing_execution_output_input_pins pin
                JOIN processing_outputs output ON output.output_identity_sha256 = pin.output_identity_sha256
                WHERE pin.released_flag = 0
            ), held(artifact_id) AS (
                SELECT artifact_id FROM roots
                UNION
                SELECT source.source_artifact_id
                FROM held
                JOIN processing_outputs output ON output.artifact_id = held.artifact_id
                JOIN processing_output_sources source
                  ON source.output_identity_sha256 = output.output_identity_sha256
            )
            SELECT artifact_id, payload_relative_path, sidecar_relative_path
            FROM processing_outputs WHERE artifact_id IN held AND availability_state = 'Available'
            UNION
            SELECT raw_artifact_id, payload_relative_path, sidecar_relative_path
            FROM raw_captures WHERE raw_artifact_id IN held
            LIMIT $maximum_holds_plus_one;
            """;
        command.Parameters.AddWithValue("$maximum_outputs", ProcessingOutputRetentionCount);
        command.Parameters.AddWithValue("$maximum_holds_plus_one", ProcessingRetentionHoldSafetyCount + 1);
        var holds = new List<ProcessingRetentionHold>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holds.Add(new ProcessingRetentionHold(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        if (holds.Count > ProcessingRetentionHoldSafetyCount)
            throw new InvalidDataException("Processing retention lineage exceeds its safety bound.");
        return holds;
    }

    internal async ValueTask<IReadOnlyList<DurableRawProcessingInput>> ReadRecentRawInputsAsync(
        string agentId,
        long currentCaptureSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_capture_row_id, manifest_json, manifest_sha256, payload_relative_path
            FROM raw_captures
            WHERE state = 'committed' AND agent_id = $agent_id
              AND capture_sequence <= $current_capture_sequence
            ORDER BY capture_sequence DESC
            LIMIT $maximum_count;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$current_capture_sequence", currentCaptureSequence);
        command.Parameters.AddWithValue("$maximum_count", maximumCount);
        var candidates = new List<(long RawRowId, byte[] ManifestJson, string ManifestSha256, string PayloadPath)>(maximumCount);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetInt64(0),
                    await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var entries = new List<DurableRawProcessingInput>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!string.Equals(
                    CaptureContractJson.ComputeManifestSha256(candidate.ManifestJson),
                    candidate.ManifestSha256,
                    StringComparison.Ordinal))
            {
                await QuarantineRawInputAsync(connection, candidate.RawRowId, cancellationToken).ConfigureAwait(false);
                throw new InvalidDataException("Durable raw rolling input manifest differs from its committed bytes.");
            }
            var parsed = CaptureContractJson.ParseManifest(candidate.ManifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest?.Descriptor is not { } descriptor)
            {
                await QuarantineRawInputAsync(connection, candidate.RawRowId, cancellationToken).ConfigureAwait(false);
                throw new InvalidDataException("Durable raw rolling input manifest is invalid.");
            }
            entries.Add(new DurableRawProcessingInput(descriptor, candidate.PayloadPath));
        }
        return entries;
    }

    private async Task QuarantineRawInputAsync(
        SqliteConnection connection,
        long rawRowId,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE capture_lane_work
                SET state = 'quarantined', failure_reason = 'evidence-invalid',
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE raw_capture_row_id = $raw AND lane_name = 'standard';
                """;
            command.Parameters.AddWithValue("$raw", rawRowId);
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE raw_captures SET retention_hold = 1 WHERE raw_capture_row_id = $raw;";
            command.Parameters.AddWithValue("$raw", rawRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<CaptureProcessingOperationalState> ReadOperationalStateAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN work.state IN ('pending', 'leased', 'retry_wait') THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state = 'retry_wait' THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state = 'quarantined' THEN 1 ELSE 0 END),
                MIN(CASE WHEN work.state IN ('pending', 'leased', 'retry_wait') THEN raw.exposure_started_unix_ms END)
            FROM capture_lane_work AS work
            JOIN raw_captures AS raw ON raw.raw_capture_row_id = work.raw_capture_row_id
            WHERE work.lane_name = 'standard';
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var pending = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(0);
        var retry = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(1);
        var terminal = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt64(2);
        var oldest = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        await reader.DisposeAsync().ConfigureAwait(false);
        using var replay = connection.CreateCommand();
        replay.CommandText = """
            SELECT
                SUM(CASE WHEN state IN ('Pending', 'Leased', 'RetryWait') THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 'RetryWait' THEN 1 ELSE 0 END),
                SUM(CASE WHEN state IN ('Failed', 'Expired') THEN 1 ELSE 0 END),
                MIN(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.accepted_unix_ms END),
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.payload_bytes ELSE 0 END)
            FROM processing_replay_work work
            JOIN processing_executions execution ON execution.execution_id = work.execution_id;
            """;
        using var replayReader = await replay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await replayReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var replayPending = await replayReader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? 0 : replayReader.GetInt64(0);
        var replayRetry = await replayReader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? 0 : replayReader.GetInt64(1);
        var replayTerminal = await replayReader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? 0 : replayReader.GetInt64(2);
        var replayOldest = await replayReader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(replayReader.GetInt64(3));
        var replayPendingBytes = await replayReader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
            ? 0
            : replayReader.GetInt64(4);
        return new CaptureProcessingOperationalState(
            pending, retry, terminal, oldest, replayPending, replayRetry, replayTerminal, replayOldest,
            replayPendingBytes);
    }

    private async ValueTask<bool> InsertOutputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        string nodeId,
        DurableProcessingOutput output,
        bool allowProducerAlias,
        CancellationToken cancellationToken)
    {
        var descriptorJson = output.EvidenceJson;
        var algorithmsJson = JsonSerializer.SerializeToUtf8Bytes(output.Algorithms, SerializerOptions);
        var compatibilityJson = JsonSerializer.SerializeToUtf8Bytes(output.Compatibility, SerializerOptions);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_outputs(
                output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                committed_unix_ms, product_kind, product_schema_version,
                content_identity_sha256, frame_artifact_recipe_version)
            VALUES (
                $output_identity_sha256, $capture_id, $agent_id, $node_id, $artifact_id, $role, $variant,
                $payload_relative_path, $sidecar_relative_path, $descriptor_json, $recipe_identity_sha256,
                $algorithms_json, $compatibility_json, $total_integration_ticks, $capture_sequence,
                $committed_unix_ms, $product_kind, $product_schema_version,
                $content_identity_sha256, $frame_artifact_recipe_version)
            ON CONFLICT(output_identity_sha256) DO NOTHING;
            """;
        AddOutputParameters(
            command,
            captureId,
            nodeId,
            output,
            descriptorJson,
            algorithmsJson,
            compatibilityJson,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT capture_id, node_id, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, recipe_identity_sha256, algorithms_json, compatibility_json,
                   total_integration_ticks, capture_sequence, product_kind,
                   product_schema_version, content_identity_sha256,
                   availability_state, availability_reason, frame_artifact_recipe_version
            FROM processing_outputs WHERE output_identity_sha256 = $output_identity_sha256;
            """;
        verify.Parameters.AddWithValue("$output_identity_sha256", output.OutputIdentitySha256);
        using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("A processing output identity was not committed.");
        }
        var existingDescriptorJson = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
        var existingAlgorithmsJson = await reader.GetFieldValueAsync<byte[]>(7, cancellationToken).ConfigureAwait(false);
        var existingCompatibilityJson = await reader.GetFieldValueAsync<byte[]>(8, cancellationToken).ConfigureAwait(false);
        var existingProductKind = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(11);
        var existingProductSchemaVersion = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(12);
        var existingContentIdentity = await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(13);
        var existingFrameArtifactRecipeVersion = await reader.IsDBNullAsync(16, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(16);
        if (!string.Equals(reader.GetString(0), captureId.ToString("N"), StringComparison.Ordinal) ||
            (!allowProducerAlias && !string.Equals(reader.GetString(1), nodeId, StringComparison.Ordinal)) ||
            !string.Equals(reader.GetString(2), output.ArtifactId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), output.PayloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), output.SidecarRelativePath, StringComparison.Ordinal) ||
            !OutputEvidenceMatches(existingDescriptorJson, descriptorJson, allowProducerAlias) ||
            !string.Equals(reader.GetString(6), output.RecipeIdentitySha256, StringComparison.Ordinal) ||
            !existingAlgorithmsJson.AsSpan().SequenceEqual(algorithmsJson) ||
            !existingCompatibilityJson.AsSpan().SequenceEqual(compatibilityJson) ||
            reader.GetInt64(9) != output.TotalIntegration.Ticks ||
            reader.GetInt64(10) != output.CaptureSequence ||
            !string.Equals(existingProductKind, output.ProductKind?.ToString(), StringComparison.Ordinal) ||
            !string.Equals(existingProductSchemaVersion, output.ProductSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(existingContentIdentity, output.ContentIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(existingFrameArtifactRecipeVersion, output.FrameArtifactRecipeVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A processing output identity conflicts with committed immutable facts.");
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        var sourceIds = output.Artifact.SourceArtifactIds;
        if (inserted)
        {
            for (var ordinal = 0; ordinal < sourceIds.Count; ordinal++)
            {
                using var source = connection.CreateCommand();
                source.Transaction = transaction;
                source.CommandText = """
                    INSERT INTO processing_output_sources(output_identity_sha256, source_ordinal, source_artifact_id)
                    VALUES($output, $ordinal, $artifact);
                    """;
                source.Parameters.AddWithValue("$output", output.OutputIdentitySha256);
                source.Parameters.AddWithValue("$ordinal", ordinal);
                source.Parameters.AddWithValue("$artifact", sourceIds[ordinal].ToString("N"));
                await source.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        using var verifySources = connection.CreateCommand();
        verifySources.Transaction = transaction;
        verifySources.CommandText = """
            SELECT source_ordinal, source_artifact_id
            FROM processing_output_sources
            WHERE output_identity_sha256 = $output
            ORDER BY source_ordinal;
            """;
        verifySources.Parameters.AddWithValue("$output", output.OutputIdentitySha256);
        var existingSources = new List<Guid>();
        using var sourceReader = await verifySources.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (sourceReader.GetInt32(0) != existingSources.Count)
                throw new InvalidDataException("A processing output identity has non-contiguous source lineage.");
            existingSources.Add(Guid.ParseExact(sourceReader.GetString(1), "N"));
        }
        if (!existingSources.SequenceEqual(sourceIds))
            throw new InvalidDataException("A processing output identity conflicts with committed source lineage.");
        return inserted;
    }

    private static bool OutputEvidenceMatches(
        byte[] existing,
        byte[] requested,
        bool allowProducerAlias)
    {
        if (existing.AsSpan().SequenceEqual(requested)) return true;
        if (!allowProducerAlias) return false;
        var existingManifest = CaptureContractJson.ParseManifest(existing).Document?.Manifest;
        var requestedManifest = CaptureContractJson.ParseManifest(requested).Document?.Manifest;
        return existingManifest is not null && requestedManifest is not null &&
            CaptureContractJson.Serialize(existingManifest with { ProducerStepId = null }).AsSpan().SequenceEqual(
                CaptureContractJson.Serialize(requestedManifest with { ProducerStepId = null }));
    }

    private static async ValueTask<bool> IsOutputPublishedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string outputIdentitySha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN
                EXISTS (SELECT 1 FROM processing_execution_outputs
                        WHERE output_identity_sha256 = $output AND published_flag = 1)
                OR (NOT EXISTS (SELECT 1 FROM processing_execution_outputs
                                WHERE output_identity_sha256 = $output)
                    AND EXISTS (
                        SELECT 1
                        FROM processing_outputs output
                        JOIN processing_nodes node ON node.capture_id = output.capture_id
                                                  AND node.node_id = output.node_id
                        WHERE output.output_identity_sha256 = $output
                          AND node.status = 'Completed'))
                THEN 1 ELSE 0 END;
            """;
        command.Parameters.AddWithValue("$output", outputIdentitySha256);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void AddOutputParameters(
        SqliteCommand command,
        Guid captureId,
        string nodeId,
        DurableProcessingOutput output,
        byte[] descriptorJson,
        byte[] algorithmsJson,
        byte[] compatibilityJson,
        long committedUnixMilliseconds)
    {
        command.Parameters.AddWithValue("$output_identity_sha256", output.OutputIdentitySha256);
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$agent_id", output.Capture.AgentId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$artifact_id", output.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$role", output.Artifact.Role.ToString());
        command.Parameters.AddWithValue("$variant", output.Artifact.Variant);
        command.Parameters.AddWithValue("$payload_relative_path", output.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar_relative_path", output.SidecarRelativePath);
        command.Parameters.AddWithValue("$descriptor_json", descriptorJson);
        command.Parameters.AddWithValue("$recipe_identity_sha256", output.RecipeIdentitySha256);
        command.Parameters.AddWithValue("$algorithms_json", algorithmsJson);
        command.Parameters.AddWithValue("$compatibility_json", compatibilityJson);
        command.Parameters.AddWithValue("$total_integration_ticks", output.TotalIntegration.Ticks);
        command.Parameters.AddWithValue("$capture_sequence", output.CaptureSequence);
        command.Parameters.AddWithValue("$committed_unix_ms", committedUnixMilliseconds);
        command.Parameters.AddWithValue("$product_kind", output.ProductKind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$product_schema_version", (object?)output.ProductSchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$content_identity_sha256", (object?)output.ContentIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$frame_artifact_recipe_version", (object?)output.FrameArtifactRecipeVersion ?? DBNull.Value);
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadOutputsAsync(
        SqliteConnection connection,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                     algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                     product_kind, product_schema_version, content_identity_sha256,
                     availability_state, availability_reason, frame_artifact_recipe_version
            FROM processing_outputs AS output
            WHERE capture_id = $capture_id AND node_id = $node_id
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
            ORDER BY output_identity_sha256;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", nodeId);
        return (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output)
            .ToArray();
    }

    internal static async ValueTask<List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)>> ReadOutputRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken,
        bool skipInvalid = false)
    {
        var outputs = new List<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                outputs.Add(await ReadOutputRowAsync(reader, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (skipInvalid && exception is InvalidDataException or FormatException or ArgumentException)
            {
            }
        }
        return outputs;
    }

    private static async ValueTask<(Guid CaptureId, string NodeId, DurableProcessingOutput Output)> ReadOutputRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var descriptorJson = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
        var algorithmsJson = await reader.GetFieldValueAsync<byte[]>(11, cancellationToken).ConfigureAwait(false);
        var compatibilityJson = await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false);
        ReconstructionDescriptor? descriptor = null;
        IDurableProcessingProductManifest? productManifest = null;
        try
        {
            using var evidence = JsonDocument.Parse(descriptorJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object ||
                !evidence.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Committed processing output descriptor is invalid.");
            }
            if (schema.GetString() is DurableProcessingProductManifestV1.CurrentSchemaVersion or
                DurableEncodedProductManifestV2.CurrentSchemaVersion or
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion)
            {
                productManifest = DurableProcessingProductManifestJson.Parse(descriptorJson);
            }
            else
            {
                var parsed = CaptureContractJson.ParseManifest(descriptorJson);
                descriptor = parsed.Document?.Manifest?.Descriptor;
                if (!parsed.IsValid || descriptor is null)
                {
                    throw new InvalidDataException("Committed processing output descriptor is invalid.");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Committed processing output descriptor is invalid.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Committed processing output descriptor is invalid.", exception);
        }
        var algorithms = JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(algorithmsJson, SerializerOptions) ?? [];
        var compatibility = JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(compatibilityJson, SerializerOptions)
            ?? throw new InvalidDataException("Committed processing compatibility is invalid.");
        var artifact = descriptor?.Artifact ?? productManifest!.Artifact;
        var capture = descriptor?.Capture ?? productManifest!.Capture;
        var outputIdentity = reader.GetString(0);
        var artifactId = Guid.ParseExact(reader.GetString(1), "N");
        var payloadRelativePath = reader.GetString(2);
        var recipeIdentity = reader.GetString(10);
        var totalIntegration = TimeSpan.FromTicks(reader.GetInt64(13));
        var captureSequence = reader.GetInt64(14);
        ProcessingProductKind? productKind = await reader.IsDBNullAsync(15, cancellationToken).ConfigureAwait(false)
            ? null : Enum.Parse<ProcessingProductKind>(reader.GetString(15));
        var productSchemaVersion = await reader.IsDBNullAsync(16, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(16);
        var contentIdentity = await reader.IsDBNullAsync(17, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(17);
        var availabilityState = reader.GetString(18);
        var availabilityReason = await reader.IsDBNullAsync(19, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(19);
        var frameArtifactRecipeVersion = await reader.IsDBNullAsync(20, cancellationToken).ConfigureAwait(false)
            ? null : reader.GetString(20);
        var typedManifest = productManifest as DurableTypedMetadataProductManifestV3;
        var sourceMatchesManifest = productManifest is null ||
            (productManifest.ProducerStepId is { } producerStepId
                ? string.Equals(reader.GetString(7), producerStepId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(reader.GetString(7), productManifest.Artifact.SourceId, StringComparison.Ordinal));
        if (!string.Equals(reader.GetString(5), capture.CaptureId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(6), capture.AgentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reader.GetString(7)) ||
            !sourceMatchesManifest ||
            !string.Equals(reader.GetString(8), artifact.Role.ToString(), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(9), artifact.Variant, StringComparison.Ordinal) ||
            artifactId != artifact.ArtifactId ||
            !string.Equals(payloadRelativePath, productManifest?.RelativeArtifactPath ?? payloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(outputIdentity, productManifest?.OutputIdentitySha256 ?? outputIdentity, StringComparison.Ordinal) ||
            !string.Equals(recipeIdentity, ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256, StringComparison.Ordinal) ||
            !algorithms.SequenceEqual(productManifest?.Algorithms ?? algorithms) ||
            compatibility != (productManifest?.Compatibility ?? compatibility) ||
            totalIntegration.Ticks != (productManifest?.TotalIntegrationTicks ?? totalIntegration.Ticks) ||
            captureSequence != capture.CaptureSequence ||
            productKind != typedManifest?.Kind ||
            !string.Equals(productSchemaVersion, typedManifest?.ProductSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(contentIdentity, typedManifest?.ContentIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Committed processing output columns conflict with its descriptor.");
        }
        return (
            capture.CaptureId,
            reader.GetString(7),
            new DurableProcessingOutput(
                outputIdentity,
                artifactId,
                payloadRelativePath,
                reader.GetString(3),
                descriptorJson,
                descriptor,
                productManifest,
                recipeIdentity,
                algorithms,
                compatibility,
                totalIntegration,
                captureSequence,
                productKind,
                productSchemaVersion,
                 contentIdentity,
                 availabilityState,
                 availabilityReason,
                 frameArtifactRecipeVersion));
    }

    private sealed record ProcessingSchemaInspection(
        long RawIngressVersion,
        long ProcessingObjectCount,
        int? ProcessingVersion);

    private async ValueTask<ProcessingSchemaInspection> InspectExistingDatabaseAsync(
        CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = _busyTimeoutSeconds
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const int sqliteDbConfigNoCheckpointOnClose = 1006;
            var configurationResult = SQLitePCL.raw.sqlite3_db_config(
                connection.Handle,
                sqliteDbConfigNoCheckpointOnClose,
                1,
                out var checkpointDisabled);
            if (configurationResult != SQLitePCL.raw.SQLITE_OK || checkpointDisabled != 1)
            {
                throw new InvalidOperationException(
                    "Capture processing SQLite inspection could not disable checkpoint-on-close.");
            }
            EnsureDatabaseFilesArePhysical();
            using (var begin = connection.CreateCommand())
            {
                begin.CommandText = "BEGIN DEFERRED;";
                await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var rawVersion = await ExecuteScalarLongAsync(
                connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
            if (rawVersion == SqliteRawCaptureJournal.CurrentSchemaVersion)
            {
                await SqliteRawCaptureJournal.ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            var processingObjectCount = await CountProcessingSchemaObjectsAsync(
                connection, cancellationToken).ConfigureAwait(false);
            var processingVersion = processingObjectCount == 0
                ? null
                : await ReadProcessingVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (processingVersion == CurrentSchemaVersion)
            {
                await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            return new(rawVersion, processingObjectCount, processingVersion);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("Capture processing SQLite schema inspection failed.", exception);
        }
    }

    private static async ValueTask ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var rawVersion = await ExecuteScalarLongAsync(
            connection, "PRAGMA user_version;", cancellationToken, transaction).ConfigureAwait(false);
        if (rawVersion != SqliteRawCaptureJournal.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Capture processing SQLite does not share canonical raw ingress schema {SqliteRawCaptureJournal.CurrentSchemaVersion}.");
        }
        var integrity = await ExecuteScalarStringAsync(
            connection, "PRAGMA integrity_check;", cancellationToken, transaction).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capture processing SQLite integrity check failed.");
        }
        if (await ExecuteScalarLongAsync(
                connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken, transaction).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Capture processing SQLite foreign-key validation failed.");
        }
        if (await ExecuteScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM capture_processing_schema WHERE schema_key = 1 AND version = {CurrentSchemaVersion};",
                cancellationToken,
                transaction).ConfigureAwait(false) != 1 ||
            await ExecuteScalarLongAsync(
                connection, "SELECT COUNT(*) FROM capture_processing_schema;", cancellationToken, transaction).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Capture processing SQLite schema marker is invalid.");
        }
        var actual = await ReadSchemaDefinitionsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        if (CanonicalSchemaDefinitions.Value.Any(expected =>
                !actual.TryGetValue(expected.Key, out var definition) ||
                !string.Equals(definition, expected.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !CanonicalSchemaDefinitions.Value.ContainsKey(name)))
        {
            throw new InvalidDataException("Capture processing SQLite schema is not the canonical schema 7 definition.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the internal constant schema statement is executed.")]
    private static Dictionary<string, string> CreateCanonicalSchemaDefinitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        return ReadSchemaDefinitions(connection);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only schema statements generated from internal constants are executed.")]
    private static Dictionary<string, string> CreateCanonicalSchema5Definitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = LegacySchema5Sql;
        command.ExecuteNonQuery();
        return ReadSchemaDefinitions(connection);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only schema statements generated from internal constants are executed.")]
    private static Dictionary<string, string> CreateCanonicalSchema6Definitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = LegacySchema6Sql;
        command.ExecuteNonQuery();
        return ReadSchemaDefinitions(connection);
    }

    private static string CreateLegacySchema6Sql()
    {
        var statements = SchemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static statement =>
                !statement.StartsWith("CREATE TABLE processing_graph_delivery_", StringComparison.Ordinal) &&
                !statement.StartsWith("CREATE INDEX ix_processing_graph_delivery_", StringComparison.Ordinal) &&
                !statement.StartsWith("CREATE UNIQUE INDEX ix_processing_graph_delivery_", StringComparison.Ordinal))
            .Select(static statement => statement.StartsWith("CREATE TABLE capture_processing_schema(", StringComparison.Ordinal) ||
                    statement.StartsWith("INSERT INTO capture_processing_schema(", StringComparison.Ordinal)
                ? statement.Replace("version = 7", "version = 6", StringComparison.Ordinal)
                    .Replace("VALUES (1, 7)", "VALUES (1, 6)", StringComparison.Ordinal)
                : statement);
        return string.Join(";\n", statements) + ";";
    }

    private static string CreateLegacySchema5Sql()
    {
        var selectedPrefixes = new[]
        {
            "CREATE TABLE capture_processing_schema(",
            "INSERT INTO capture_processing_schema(",
            "CREATE TABLE processing_nodes(",
            "CREATE TABLE processing_node_inputs(",
            "CREATE TABLE processing_outputs(",
            "CREATE TABLE processing_output_sources(",
            "CREATE TABLE processing_lifecycle_operations(",
            "CREATE TABLE processing_reconciliation_state(",
            "CREATE TABLE processing_output_diagnostics(",
            "CREATE INDEX ix_processing_outputs_",
            "CREATE INDEX ix_processing_nodes_",
            "CREATE INDEX ix_processing_output_sources_artifact",
            "CREATE INDEX ix_processing_node_inputs_artifact"
        };
        var statements = SchemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => selectedPrefixes.Any(prefix => statement.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(statement =>
            {
                if (statement.StartsWith("CREATE TABLE capture_processing_schema(", StringComparison.Ordinal) ||
                    statement.StartsWith("INSERT INTO capture_processing_schema(", StringComparison.Ordinal))
                {
                    return statement.Replace("version = 7", "version = 5", StringComparison.Ordinal)
                        .Replace("VALUES (1, 7)", "VALUES (1, 5)", StringComparison.Ordinal);
                }
                if (statement.StartsWith("CREATE TABLE processing_outputs(", StringComparison.Ordinal))
                {
                    return statement.Replace(
                        "\n        ) STRICT",
                        """
                        ,
                            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                                DEFERRABLE INITIALLY DEFERRED
                        ) STRICT
                        """,
                        StringComparison.Ordinal);
                }
                return statement;
            });
        return string.Join(";\n", statements) + ";";
    }

    private static async ValueTask ValidateSchema5Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var actual = await ReadSchemaDefinitionsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        if (CanonicalSchema5Definitions.Value.Any(expected =>
                !actual.TryGetValue(expected.Key, out var definition) ||
                !string.Equals(definition, expected.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !CanonicalSchema5Definitions.Value.ContainsKey(name)))
        {
            throw new InvalidDataException("Capture processing SQLite schema is not the canonical schema 5 definition.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only schema statements selected from an internal constant are executed.")]
    private static async ValueTask MigrateSchema5Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (var preserve = connection.CreateCommand())
        {
            preserve.Transaction = transaction;
            preserve.CommandText = """
                CREATE TEMP TABLE migration_processing_outputs AS SELECT * FROM processing_outputs;
                CREATE TEMP TABLE migration_processing_output_sources AS SELECT * FROM processing_output_sources;
                DROP TABLE processing_output_sources;
                DROP TABLE processing_outputs;
                DROP TABLE capture_processing_schema;
                """;
            await preserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var selectedPrefixes = new[]
        {
            "CREATE TABLE capture_processing_schema(",
            "INSERT INTO capture_processing_schema(",
            "CREATE TABLE processing_graph_revisions(",
            "CREATE UNIQUE INDEX ix_processing_graph_revisions_active",
            "CREATE TABLE processing_graph_registry_state(",
            "CREATE TABLE processing_graph_commands(",
            "CREATE TABLE processing_graph_delivery_proposals(",
            "CREATE TABLE processing_graph_delivery_facts(",
            "CREATE TABLE processing_executions(",
            "CREATE TABLE processing_execution_nodes(",
            "CREATE TABLE processing_node_attempts(",
            "CREATE TABLE processing_execution_inputs(",
            "CREATE TABLE processing_execution_input_pins(",
            "CREATE TABLE processing_execution_output_input_pins(",
            "CREATE TABLE processing_execution_outputs(",
            "CREATE TABLE processing_replay_work(",
            "CREATE TABLE processing_outputs(",
            "CREATE TABLE processing_output_sources(",
            "CREATE INDEX ix_processing_outputs_",
            "CREATE INDEX ix_processing_output_sources_artifact",
            "CREATE INDEX ix_processing_graph_revisions_name",
            "CREATE UNIQUE INDEX ix_processing_executions_live_capture",
            "CREATE INDEX ix_processing_executions_status",
            "CREATE INDEX ix_processing_replay_work_claim",
            "CREATE INDEX ix_processing_node_attempts_history",
            "CREATE INDEX ix_processing_execution_inputs_window",
            "CREATE INDEX ix_processing_execution_pins_active",
            "CREATE INDEX ix_processing_execution_output_pins_active",
            "CREATE INDEX ix_processing_execution_outputs_publication",
            "CREATE INDEX ix_processing_graph_delivery_proposals_lookup",
            "CREATE INDEX ix_processing_graph_delivery_facts_pending",
            "CREATE INDEX ix_processing_graph_delivery_facts_lifecycle",
            "CREATE UNIQUE INDEX ix_processing_graph_delivery_facts_settlement"
        };
        foreach (var statement in SchemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(statement => selectedPrefixes.Any(prefix => statement.StartsWith(prefix, StringComparison.Ordinal))))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement + ";";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var restore = connection.CreateCommand();
        restore.Transaction = transaction;
        restore.CommandText = """
            INSERT INTO processing_outputs SELECT * FROM migration_processing_outputs;
            INSERT INTO processing_output_sources SELECT * FROM migration_processing_output_sources;
            DROP TABLE migration_processing_output_sources;
            DROP TABLE migration_processing_outputs;
            """;
        await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ValidateSchema6Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var actual = await ReadSchemaDefinitionsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        if (CanonicalSchema6Definitions.Value.Any(expected =>
                !actual.TryGetValue(expected.Key, out var definition) ||
                !string.Equals(definition, expected.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !CanonicalSchema6Definitions.Value.ContainsKey(name)))
        {
            throw new InvalidDataException("Capture processing SQLite schema is not the canonical schema 6 definition.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only schema statements selected from an internal constant are executed.")]
    private static async ValueTask MigrateSchema6Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (var dropMarker = connection.CreateCommand())
        {
            dropMarker.Transaction = transaction;
            dropMarker.CommandText = "DROP TABLE capture_processing_schema;";
            await dropMarker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var statement in SchemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(static statement =>
                         statement.StartsWith("CREATE TABLE capture_processing_schema(", StringComparison.Ordinal) ||
                          statement.StartsWith("INSERT INTO capture_processing_schema(", StringComparison.Ordinal) ||
                          statement.StartsWith("CREATE TABLE processing_graph_delivery_", StringComparison.Ordinal) ||
                          statement.StartsWith("CREATE INDEX ix_processing_graph_delivery_", StringComparison.Ordinal) ||
                          statement.StartsWith("CREATE UNIQUE INDEX ix_processing_graph_delivery_", StringComparison.Ordinal)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement + ";";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<Dictionary<string, string>> ReadSchemaDefinitionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = CreateSchemaDefinitionCommand(connection, transaction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinitionRow(reader));
        }
        return definitions;
    }

    private static Dictionary<string, string> ReadSchemaDefinitions(SqliteConnection connection)
    {
        using var command = CreateSchemaDefinitionCommand(connection, null);
        using var reader = command.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinitionRow(reader));
        }
        return definitions;
    }

    private static SqliteCommand CreateSchemaDefinitionCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL AND (
                   name = 'capture_processing_schema'
                OR name LIKE 'processing_%'
                OR name LIKE 'ix_processing_%'
                OR tbl_name IN (
                    'capture_processing_schema', 'processing_nodes', 'processing_node_inputs',
                    'processing_outputs', 'processing_output_sources', 'processing_lifecycle_operations',
                    'processing_reconciliation_state', 'processing_output_diagnostics'))
            ORDER BY type, name;
            """;
        return command;
    }

    private static string ReadSchemaDefinitionRow(SqliteDataReader reader)
    {
        var definition = new System.Text.StringBuilder();
        foreach (var ordinal in new[] { 0, 2 })
        {
            var value = reader.GetString(ordinal);
            definition.Append(value.Length).Append(':').Append(value);
        }
        var sql = SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3));
        definition.Append(sql.Length).Append(':').Append(sql);
        return definition.ToString();
    }

    private static async ValueTask<long> CountProcessingSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
        => await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name = 'capture_processing_schema'
               OR name LIKE 'processing_%'
               OR name LIKE 'ix_processing_%';
            """, cancellationToken, transaction).ConfigureAwait(false);

    private static async ValueTask<int?> ReadProcessingVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await ExecuteScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'capture_processing_schema';",
                cancellationToken).ConfigureAwait(false) != 1)
        {
            return null;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM capture_processing_schema WHERE schema_key = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? 0
            : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
        => Convert.ToInt64(
            await ExecuteScalarAsync(connection, sql, cancellationToken, transaction).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async ValueTask<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
        => Convert.ToString(
            await ExecuteScalarAsync(connection, sql, cancellationToken, transaction).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant SQL and PRAGMA statements are passed to this helper.")]
    private static async ValueTask<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsCanonicalRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(path) || path.Contains('\0', StringComparison.Ordinal)) return false;
        var parts = path.Split('/');
        return parts.All(static part => part.Length > 0 && part is not "." and not "..");
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenUnconfiguredAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async ValueTask<SqliteConnection> OpenUnconfiguredAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var journalMode = await ExecuteScalarStringAsync(
            connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Capture processing SQLite journal could not enter WAL mode.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureDatabaseFilesArePhysical()
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-shm"));
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-journal"));
    }

    public void Dispose()
    {
        _initializeGate.Dispose();
    }

    private const string SchemaSql = """
        CREATE TABLE capture_processing_schema(
            schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
            version INTEGER NOT NULL CHECK(version = 7)
        ) STRICT;
        INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, 7);
        CREATE TABLE processing_graph_revisions(
            revision_id TEXT PRIMARY KEY CHECK(length(revision_id) = 64),
            graph_name TEXT NOT NULL CHECK(length(graph_name) BETWEEN 1 AND 128),
            revision_name TEXT NOT NULL CHECK(length(revision_name) BETWEEN 1 AND 128),
            lifecycle TEXT NOT NULL CHECK(lifecycle IN ('Draft', 'Validated', 'Active', 'Retired')),
            definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
            shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
            local_plan_identity_sha256 TEXT NOT NULL CHECK(length(local_plan_identity_sha256) = 64),
            pipeline_json BLOB NOT NULL CHECK(length(pipeline_json) BETWEEN 2 AND 2097152),
            definition_json BLOB NOT NULL CHECK(length(definition_json) BETWEEN 2 AND 2097152),
            frozen_plan_json BLOB NOT NULL CHECK(length(frozen_plan_json) BETWEEN 2 AND 2097152),
            nodes_json BLOB NOT NULL CHECK(length(nodes_json) BETWEEN 2 AND 2097152),
            created_unix_ms INTEGER NOT NULL,
            validated_unix_ms INTEGER NULL,
            activated_unix_ms INTEGER NULL,
            retired_unix_ms INTEGER NULL,
            UNIQUE(graph_name, revision_name)
        ) STRICT;
        CREATE UNIQUE INDEX ix_processing_graph_revisions_active
            ON processing_graph_revisions(lifecycle) WHERE lifecycle = 'Active';
        CREATE TABLE processing_graph_registry_state(
            state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
            selection_mode TEXT NOT NULL CHECK(selection_mode IN ('ConfiguredBasic', 'Named')),
            active_revision_id TEXT NOT NULL CHECK(length(active_revision_id) = 64),
            configured_basic_revision_id TEXT NOT NULL CHECK(length(configured_basic_revision_id) = 64),
            state_version INTEGER NOT NULL CHECK(state_version > 0),
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(active_revision_id) REFERENCES processing_graph_revisions(revision_id),
            FOREIGN KEY(configured_basic_revision_id) REFERENCES processing_graph_revisions(revision_id)
        ) STRICT;
        CREATE TABLE processing_graph_commands(
            idempotency_key TEXT PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK(length(command_kind) BETWEEN 1 AND 64),
            command_sha256 TEXT NOT NULL CHECK(length(command_sha256) = 64),
            actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
            reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 256),
            result_reference TEXT NOT NULL CHECK(length(result_reference) BETWEEN 1 AND 128),
            completed_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE processing_graph_delivery_proposals(
            proposal_id TEXT PRIMARY KEY CHECK(length(proposal_id) = 32),
            catalog_revision_id TEXT NOT NULL CHECK(length(catalog_revision_id) = 32),
            assignment_id TEXT NOT NULL CHECK(length(assignment_id) = 32),
            registration_id TEXT NOT NULL CHECK(length(registration_id) = 32),
            installation_id TEXT NOT NULL CHECK(length(installation_id) = 32),
            installation_public_id TEXT NOT NULL CHECK(length(installation_public_id) = 32),
            expected_active_revision_id TEXT NULL CHECK(expected_active_revision_id IS NULL OR length(expected_active_revision_id) = 64),
            capability_snapshot_sha256 TEXT NOT NULL CHECK(length(capability_snapshot_sha256) = 64),
            definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
            shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
            definition_json BLOB NOT NULL CHECK(length(definition_json) BETWEEN 2 AND 2097152),
            issued_unix_ms INTEGER NOT NULL,
            expires_unix_ms INTEGER NOT NULL CHECK(expires_unix_ms > issued_unix_ms),
            disposition TEXT NOT NULL CHECK(disposition IN ('Pending', 'Accepted', 'Rejected', 'Expired', 'Superseded')),
            disposition_reason TEXT NULL CHECK(disposition_reason IS NULL OR length(disposition_reason) BETWEEN 1 AND 128),
            local_revision_id TEXT NULL CHECK(local_revision_id IS NULL OR length(local_revision_id) = 64),
            local_plan_identity_sha256 TEXT NULL CHECK(local_plan_identity_sha256 IS NULL OR length(local_plan_identity_sha256) = 64),
            received_unix_ms INTEGER NOT NULL,
            settled_unix_ms INTEGER NULL,
            FOREIGN KEY(local_revision_id) REFERENCES processing_graph_revisions(revision_id)
        ) STRICT;
        CREATE TABLE processing_graph_delivery_facts(
            fact_id TEXT PRIMARY KEY CHECK(length(fact_id) = 32),
            proposal_id TEXT NOT NULL CHECK(length(proposal_id) = 32),
            fact_kind TEXT NOT NULL CHECK(fact_kind IN ('Accepted', 'Rejected', 'Activated', 'RolledBack', 'Expired')),
            occurred_unix_ms INTEGER NOT NULL,
            local_revision_id TEXT NULL CHECK(local_revision_id IS NULL OR length(local_revision_id) = 64),
            definition_identity_sha256 TEXT NULL CHECK(definition_identity_sha256 IS NULL OR length(definition_identity_sha256) = 64),
            shared_plan_identity_sha256 TEXT NULL CHECK(shared_plan_identity_sha256 IS NULL OR length(shared_plan_identity_sha256) = 64),
            local_plan_identity_sha256 TEXT NULL CHECK(local_plan_identity_sha256 IS NULL OR length(local_plan_identity_sha256) = 64),
            reason_code TEXT NULL CHECK(reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
            delivery_state TEXT NOT NULL CHECK(delivery_state IN ('Pending', 'Acknowledged', 'Rejected')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            acknowledged_unix_ms INTEGER NULL,
            last_reason_code TEXT NULL CHECK(last_reason_code IS NULL OR length(last_reason_code) BETWEEN 1 AND 128),
            FOREIGN KEY(proposal_id) REFERENCES processing_graph_delivery_proposals(proposal_id)
        ) STRICT;
        CREATE TABLE processing_executions(
            execution_id TEXT PRIMARY KEY CHECK(length(execution_id) = 32),
            execution_class TEXT NOT NULL CHECK(execution_class IN ('Live', 'Replay')),
            status TEXT NOT NULL CHECK(status IN ('Pending', 'Running', 'Completed', 'Failed', 'Cancelled', 'Expired')),
            capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
            primary_artifact_id TEXT NOT NULL CHECK(length(primary_artifact_id) = 32),
            graph_revision_id TEXT NOT NULL CHECK(length(graph_revision_id) = 64),
            definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
            shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
            local_plan_identity_sha256 TEXT NOT NULL CHECK(length(local_plan_identity_sha256) = 64),
            frozen_plan_json BLOB NOT NULL CHECK(length(frozen_plan_json) BETWEEN 2 AND 2097152),
            configuration_json BLOB NOT NULL CHECK(length(configuration_json) BETWEEN 2 AND 2097152),
            trigger_kind TEXT NOT NULL CHECK(length(trigger_kind) BETWEEN 1 AND 64),
            trigger_reference TEXT NULL CHECK(trigger_reference IS NULL OR length(trigger_reference) BETWEEN 1 AND 128),
            priority INTEGER NOT NULL CHECK(priority BETWEEN -1000 AND 1000),
            payload_bytes INTEGER NOT NULL CHECK(payload_bytes >= 0),
            accepted_unix_ms INTEGER NOT NULL,
            available_unix_ms INTEGER NOT NULL,
            deadline_unix_ms INTEGER NOT NULL,
            maximum_age_unix_ms INTEGER NOT NULL,
            started_unix_ms INTEGER NULL,
            completed_unix_ms INTEGER NULL,
            failure_reason TEXT NULL CHECK(failure_reason IS NULL OR length(failure_reason) BETWEEN 1 AND 128),
            cancellation_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancellation_requested IN (0, 1)),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            allow_automatic_publication INTEGER NOT NULL CHECK(allow_automatic_publication IN (0, 1)),
            FOREIGN KEY(graph_revision_id) REFERENCES processing_graph_revisions(revision_id),
            UNIQUE(execution_class, capture_id, graph_revision_id, trigger_kind, trigger_reference)
        ) STRICT;
        CREATE TABLE processing_execution_nodes(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
            required INTEGER NOT NULL CHECK(required IN (0, 1)),
            plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
            shared_plan_node_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_node_identity_sha256) = 64),
            dependencies_json TEXT NOT NULL,
            inputs_json TEXT NOT NULL,
            outputs_json TEXT NOT NULL,
            window_json TEXT NULL,
            status TEXT NOT NULL CHECK(status IN ('Pending', 'Running', 'Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            started_unix_ms INTEGER NULL,
            completed_unix_ms INTEGER NULL,
            PRIMARY KEY(execution_id, node_id),
            FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_node_attempts(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
            attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
            lease_owner TEXT NOT NULL CHECK(length(lease_owner) BETWEEN 1 AND 128),
            lease_token TEXT NOT NULL CHECK(length(lease_token) BETWEEN 1 AND 128),
            started_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER NULL,
            status TEXT NOT NULL CHECK(status IN ('Running', 'Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure', 'Interrupted')),
            outcome TEXT NULL CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
            duration_ticks INTEGER NULL CHECK(duration_ticks IS NULL OR duration_ticks >= 0),
            PRIMARY KEY(execution_id, node_id, attempt_number),
            FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_execution_inputs(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
            input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 512),
            window_position INTEGER NOT NULL,
            capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
            artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
            descriptor_sha256 TEXT NOT NULL CHECK(length(descriptor_sha256) = 64),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
            PRIMARY KEY(execution_id, node_id, input_ordinal),
            FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_execution_input_pins(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            raw_capture_row_id INTEGER NOT NULL,
            artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
            released_flag INTEGER NOT NULL DEFAULT 0 CHECK(released_flag IN (0, 1)),
            released_unix_ms INTEGER NULL,
            PRIMARY KEY(execution_id, artifact_id),
            FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE,
            FOREIGN KEY(raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
        ) STRICT;
        CREATE TABLE processing_execution_output_input_pins(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
            input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 512),
            window_position INTEGER NOT NULL,
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            released_flag INTEGER NOT NULL DEFAULT 0 CHECK(released_flag IN (0, 1)),
            released_unix_ms INTEGER NULL,
            PRIMARY KEY(execution_id, node_id, input_ordinal),
            UNIQUE(execution_id, node_id, output_identity_sha256),
            FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE,
            FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_execution_outputs(
            execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
            node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
            output_ordinal INTEGER NOT NULL CHECK(output_ordinal >= 0 AND output_ordinal < 128),
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            published_flag INTEGER NOT NULL CHECK(published_flag IN (0, 1)),
            PRIMARY KEY(execution_id, node_id, output_ordinal),
            UNIQUE(execution_id, output_identity_sha256),
            FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE,
            FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256)
                ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
        ) STRICT;
        CREATE TABLE processing_replay_work(
            work_id INTEGER PRIMARY KEY AUTOINCREMENT,
            execution_id TEXT NOT NULL UNIQUE CHECK(length(execution_id) = 32),
            state TEXT NOT NULL CHECK(state IN ('Pending', 'Leased', 'RetryWait', 'Completed', 'Failed', 'Cancelled', 'Expired')),
            priority INTEGER NOT NULL CHECK(priority BETWEEN -1000 AND 1000),
            available_unix_ms INTEGER NOT NULL,
            lease_token TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            claim_count INTEGER NOT NULL DEFAULT 0 CHECK(claim_count >= 0),
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_nodes(
            capture_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            required INTEGER NOT NULL CHECK(required IN (0, 1)),
            dependencies_json TEXT NOT NULL,
            recipe_name TEXT NULL,
            output_role TEXT NULL,
            output_variant TEXT NULL,
            plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
            status TEXT NOT NULL CHECK(status IN ('Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            reason TEXT NULL,
            attempt INTEGER NOT NULL,
            input_evidence_version INTEGER NULL CHECK(input_evidence_version IS NULL OR input_evidence_version = 1),
            processing_profile_identity_sha256 TEXT NULL CHECK(processing_profile_identity_sha256 IS NULL OR length(processing_profile_identity_sha256) = 64),
            started_unix_ms INTEGER NULL,
            completed_unix_ms INTEGER NOT NULL,
            duration_ticks INTEGER NULL CHECK(duration_ticks IS NULL OR duration_ticks >= 0),
            outcome TEXT NULL CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
            PRIMARY KEY(capture_id, node_id)
        ) STRICT;
        CREATE TABLE processing_node_inputs(
            capture_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 128),
            kind TEXT NOT NULL CHECK(kind IN ('Artifact', 'AuxiliaryArtifact', 'CanonicalContext')),
            name TEXT NULL CHECK(name IS NULL OR length(name) BETWEEN 1 AND 64),
            artifact_id TEXT NULL CHECK(artifact_id IS NULL OR length(artifact_id) = 32),
            role TEXT NULL,
            variant TEXT NULL CHECK(variant IS NULL OR length(variant) BETWEEN 1 AND 128),
            recipe_identity_sha256 TEXT NULL CHECK(recipe_identity_sha256 IS NULL OR length(recipe_identity_sha256) = 64),
            schema_version TEXT NULL CHECK(schema_version IS NULL OR length(schema_version) BETWEEN 1 AND 128),
            identity_sha256 TEXT NULL CHECK(identity_sha256 IS NULL OR length(identity_sha256) = 64),
            selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
            PRIMARY KEY(capture_id, node_id, input_ordinal),
            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
        ) STRICT;
        CREATE TABLE processing_outputs(
            output_identity_sha256 TEXT PRIMARY KEY CHECK(length(output_identity_sha256) = 64),
            capture_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL UNIQUE CHECK(length(artifact_id) = 32),
            role TEXT NOT NULL,
            variant TEXT NOT NULL,
            payload_relative_path TEXT NOT NULL,
            sidecar_relative_path TEXT NOT NULL,
            descriptor_json BLOB NOT NULL,
            recipe_identity_sha256 TEXT NOT NULL CHECK(length(recipe_identity_sha256) = 64),
            frame_artifact_recipe_version TEXT NULL,
            algorithms_json BLOB NOT NULL,
            compatibility_json BLOB NOT NULL,
            total_integration_ticks INTEGER NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
            committed_unix_ms INTEGER NOT NULL,
            product_kind TEXT NULL CHECK(product_kind IS NULL OR product_kind IN ('PixelData', 'Metadata')),
            product_schema_version TEXT NULL CHECK(product_schema_version IS NULL OR length(product_schema_version) BETWEEN 1 AND 128),
            content_identity_sha256 TEXT NULL CHECK(content_identity_sha256 IS NULL OR length(content_identity_sha256) = 64),
            availability_state TEXT NOT NULL DEFAULT 'Available' CHECK(availability_state IN ('Available', 'Missing', 'Quarantined')),
            availability_reason TEXT NULL CHECK(availability_reason IS NULL OR length(availability_reason) BETWEEN 1 AND 128),
            unavailable_unix_ms INTEGER NULL,
            quarantine_relative_path TEXT NULL,
            CHECK((product_kind IS NULL AND product_schema_version IS NULL AND content_identity_sha256 IS NULL) OR
                  (product_kind IS NOT NULL AND product_schema_version IS NOT NULL AND content_identity_sha256 IS NOT NULL))
        ) STRICT;
        CREATE TABLE processing_output_sources(
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            source_ordinal INTEGER NOT NULL CHECK(source_ordinal >= 0 AND source_ordinal < 512),
            source_artifact_id TEXT NOT NULL CHECK(length(source_artifact_id) = 32),
            PRIMARY KEY(output_identity_sha256, source_ordinal),
            UNIQUE(output_identity_sha256, source_artifact_id),
            FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256)
                ON DELETE CASCADE
        ) STRICT;
        CREATE TABLE processing_lifecycle_operations(
            operation_id TEXT PRIMARY KEY CHECK(length(operation_id) BETWEEN 1 AND 128),
            kind TEXT NOT NULL CHECK(kind IN ('quarantine', 'delete', 'orphan')),
            output_identity_sha256 TEXT NULL CHECK(output_identity_sha256 IS NULL OR length(output_identity_sha256) = 64),
            source_relative_path TEXT NULL,
            companion_relative_path TEXT NULL,
            destination_relative_path TEXT NOT NULL,
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            observed_bytes INTEGER NOT NULL CHECK(observed_bytes >= 0),
            planned_unix_ms INTEGER NOT NULL
            ,phase TEXT NOT NULL DEFAULT 'planned' CHECK(phase IN ('planned', 'moved', 'database-completed', 'files-deleted')),
            CHECK((kind = 'quarantine' AND source_relative_path IS NOT NULL) OR kind IN ('orphan', 'delete'))
        ) STRICT;
        CREATE TABLE processing_reconciliation_state(
            state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
            output_identity_sha256 TEXT NULL CHECK(output_identity_sha256 IS NULL OR length(output_identity_sha256) = 64),
            modern_sidecar_relative_path TEXT NULL,
            legacy_sidecar_relative_path TEXT NULL,
            payload_relative_path TEXT NULL
        ) STRICT;
        CREATE TABLE processing_output_diagnostics(
            diagnostic_id INTEGER PRIMARY KEY AUTOINCREMENT,
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
            node_id TEXT NOT NULL,
            availability_state TEXT NOT NULL CHECK(availability_state IN ('Missing', 'Quarantined')),
            availability_reason TEXT NULL,
            descriptor_json BLOB NOT NULL,
            quarantine_relative_path TEXT NULL,
            recorded_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX ix_processing_outputs_capture_node
            ON processing_outputs(capture_id, node_id);
        CREATE INDEX ix_processing_outputs_window
            ON processing_outputs(agent_id, node_id, role, capture_sequence);
        CREATE INDEX ix_processing_nodes_status
            ON processing_nodes(status, capture_id);
        CREATE INDEX ix_processing_nodes_recipe
            ON processing_nodes(upper(recipe_name), capture_id);
        CREATE INDEX ix_processing_outputs_role
            ON processing_outputs(role, capture_id);
        CREATE INDEX ix_processing_outputs_recipe
            ON processing_outputs(recipe_identity_sha256, capture_id);
        CREATE INDEX ix_processing_outputs_product
            ON processing_outputs(capture_id, product_schema_version, output_identity_sha256);
        CREATE INDEX ix_processing_outputs_retention_available
            ON processing_outputs(committed_unix_ms, output_identity_sha256) WHERE availability_state = 'Available';
        CREATE INDEX ix_processing_outputs_retention_unavailable
            ON processing_outputs(unavailable_unix_ms, output_identity_sha256) WHERE availability_state <> 'Available';
        CREATE INDEX ix_processing_output_sources_artifact
            ON processing_output_sources(source_artifact_id, output_identity_sha256);
        CREATE INDEX ix_processing_node_inputs_artifact
            ON processing_node_inputs(artifact_id, capture_id, node_id);
        CREATE INDEX ix_processing_graph_revisions_name
            ON processing_graph_revisions(graph_name, created_unix_ms DESC);
        CREATE UNIQUE INDEX ix_processing_executions_live_capture
            ON processing_executions(capture_id) WHERE execution_class = 'Live';
        CREATE INDEX ix_processing_executions_status
            ON processing_executions(execution_class, status, accepted_unix_ms, execution_id);
        CREATE INDEX ix_processing_replay_work_claim
            ON processing_replay_work(state, priority DESC, available_unix_ms, work_id);
        CREATE INDEX ix_processing_node_attempts_history
            ON processing_node_attempts(execution_id, node_id, attempt_number DESC);
        CREATE INDEX ix_processing_execution_inputs_window
            ON processing_execution_inputs(capture_id, window_position, execution_id);
        CREATE INDEX ix_processing_execution_pins_active
            ON processing_execution_input_pins(raw_capture_row_id, execution_id) WHERE released_flag = 0;
        CREATE INDEX ix_processing_execution_output_pins_active
            ON processing_execution_output_input_pins(output_identity_sha256, execution_id) WHERE released_flag = 0;
        CREATE INDEX ix_processing_execution_outputs_publication
            ON processing_execution_outputs(output_identity_sha256, published_flag);
        CREATE INDEX ix_processing_graph_delivery_proposals_lookup
            ON processing_graph_delivery_proposals(disposition, local_revision_id, issued_unix_ms);
        CREATE INDEX ix_processing_graph_delivery_facts_pending
            ON processing_graph_delivery_facts(delivery_state, next_attempt_unix_ms, occurred_unix_ms);
        CREATE INDEX ix_processing_graph_delivery_facts_lifecycle
            ON processing_graph_delivery_facts(proposal_id, fact_kind)
            WHERE fact_kind IN ('Activated', 'RolledBack');
        CREATE UNIQUE INDEX ix_processing_graph_delivery_facts_settlement
            ON processing_graph_delivery_facts(proposal_id)
            WHERE fact_kind IN ('Accepted', 'Rejected', 'Expired');
        """;

}
