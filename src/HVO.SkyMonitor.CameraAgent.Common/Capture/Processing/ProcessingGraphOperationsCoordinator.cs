using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ProcessingGraphOperationsCoordinator :
    IProcessingGraphOperations,
    IProcessingGraphDeliveryInbox,
    Evidence.IExecutionEvidenceSource,
    IDisposable
{
    private const string ConfiguredGraphName = "configured-basic";
    private static readonly HashSet<string> LocalPolicyStepAliases = new(
        ["Storage", "Telemetry"],
        StringComparer.OrdinalIgnoreCase);
    internal static string LocalPolicyNodeDescription => string.Join(
        " and ",
        LocalPolicyStepAliases.Order(StringComparer.OrdinalIgnoreCase));
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory;
    private readonly SqliteCaptureProcessingStore _store;
    private readonly ProcessingGraphExecutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ProcessingReplayWakeup _replayWakeup;
    /// <summary>
    /// Serializes configured-basic refreshes (<see cref="EnsureConfiguredBasicAsync"/>) so a single pipeline identity
    /// compiles and persists exactly once. Lock order (see <see cref="RawIngressLifecycleLock"/>): callers that hold
    /// the raw-ingress lifecycle lock may acquire this gate (raw-ingress accept and recovery binding call
    /// <see cref="PrepareLiveExecutionAsync"/> under the lifecycle lock), so this gate must never be held while
    /// waiting for the lifecycle lock. <see cref="StageAsync"/> therefore takes only the lifecycle lock; proposal
    /// acceptance is made atomic against configured-basic refreshes by the in-transaction active-revision recheck in
    /// <c>SettleDeliveryProposalAsync</c>, not by this gate.
    /// </summary>
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private CameraModuleConfig? _baseConfiguration;
    private string? _configuredPipelineIdentity;

    public ProcessingGraphOperationsCoordinator(
        ICaptureProcessingPipelineFactory pipelineFactory,
        SqliteCaptureProcessingStore store,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ProcessingReplayWakeup replayWakeup)
    {
        _pipelineFactory = pipelineFactory;
        _store = store;
        _options = options.Value.ProcessingGraphs;
        _timeProvider = timeProvider;
        _replayWakeup = replayWakeup;
    }

    public ProcessingGraphAgentCapabilities Capabilities =>
        ProcessingGraphAgentCapabilities.Create(_pipelineFactory.StableStepAliases);

    internal async ValueTask<ProcessingGraphRegistryState> EnsureConfiguredBasicAsync(
        CameraModuleConfig configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureExplicitPipeline(configuration.Pipeline);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _baseConfiguration, configuration);
            var pipelineIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(configuration.Pipeline);
            if (string.Equals(_configuredPipelineIdentity, pipelineIdentity, StringComparison.Ordinal))
            {
                return await _store.ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
            }
            var revisionName = pipelineIdentity[..16];
            var revision = CreateRevisionSnapshot(
                configuration,
                ConfiguredGraphName,
                revisionName,
                _timeProvider.GetUtcNow(),
                ProcessingGraphRevisionLifecycle.Validated);
            var occupant = await _store.ReadRevisionIdByNameAsync(ConfiguredGraphName, revisionName, cancellationToken)
                .ConfigureAwait(false);
            if (occupant is not null && !string.Equals(occupant, revision.State.RevisionId, StringComparison.Ordinal))
            {
                // The persisted configured-basic revision for this pipeline configuration was compiled by an earlier
                // contract (for example schema-6 rows that predate output media types) and therefore has different
                // immutable identities. Revision rows are append-only, so supersede it under a deterministic name
                // derived from the current compiled contract instead of colliding on the unique (name, revision) pair.
                revision = CreateRevisionSnapshot(
                    configuration,
                    ConfiguredGraphName,
                    SupersededRevisionName(revisionName, revision.State),
                    _timeProvider.GetUtcNow(),
                    ProcessingGraphRevisionLifecycle.Validated);
            }
            EnsureLiveEligible(revision);
            var state = await _store.UpsertConfiguredBasicRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
            _configuredPipelineIdentity = pipelineIdentity;
            return state;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    internal async ValueTask<(ProcessingLiveExecutionSeed Seed, CameraModuleConfig Configuration)> PrepareLiveExecutionAsync(
        CameraModuleConfig configuration,
        Guid captureId,
        Guid artifactId,
        DateTimeOffset acceptedUtc,
        CancellationToken cancellationToken)
    {
        _ = await EnsureConfiguredBasicAsync(configuration, cancellationToken).ConfigureAwait(false);
        var revision = await _store.ReadActiveRevisionAsync(cancellationToken).ConfigureAwait(false);
        var effectiveConfiguration = configuration with { Pipeline = revision.Pipeline };
        var executionId = StableExecutionId(new
        {
            Class = ProcessingGraphExecutionClass.Live.ToString(),
            CaptureId = captureId.ToString("N"),
            RevisionId = revision.State.RevisionId
        });
        var durableConfiguration = effectiveConfiguration with
        {
            Observatory = new ObservatoryLocation(0, 0, 0, "UTC"),
            DeploymentLocation = null,
            DeploymentLocationRedacted = true
        };
        var canonicalConfiguration = CanonicalBytes(durableConfiguration);
        return (new ProcessingLiveExecutionSeed(
            executionId,
            captureId,
            artifactId,
            revision,
            acceptedUtc,
            acceptedUtc,
            acceptedUtc.AddSeconds(_options.LiveDeadlineSeconds),
            acceptedUtc.AddSeconds(_options.LiveMaximumQueueAgeSeconds),
            canonicalConfiguration), effectiveConfiguration);
    }

    public ValueTask<ProcessingGraphRegistryState> GetRegistryAsync(CancellationToken cancellationToken)
        => _store.ReadRegistryAsync(cancellationToken);

    internal void NotifyLiveWorkAccepted() => _replayWakeup.SignalLiveWork();

    internal void NotifyLiveWorkChanged() => _replayWakeup.Signal();

    public async ValueTask<ProcessingGraphRevisionState> CreateRevisionAsync(
        string name,
        string revision,
        CapturePipelineConfig pipeline,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionName(name, nameof(name));
        ValidateRevisionName(revision, nameof(revision));
        EnsureExplicitPipeline(pipeline);
        var baseConfiguration = Volatile.Read(ref _baseConfiguration)
            ?? throw new InvalidOperationException(
                "A camera configuration must be initialized before named processing graph revisions can be created.");
        return await CreateRevisionAsync(
            baseConfiguration, name, revision, pipeline, idempotencyKey, actor, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    internal ValueTask<ProcessingGraphRevisionState> CreateRevisionAsync(
        CameraModuleConfig baseConfiguration,
        string name,
        string revision,
        CapturePipelineConfig pipeline,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionName(name, nameof(name));
        ValidateRevisionName(revision, nameof(revision));
        EnsureExplicitPipeline(pipeline);
        var snapshot = CreateRevisionSnapshot(
            baseConfiguration with { Pipeline = pipeline }, name, revision, _timeProvider.GetUtcNow());
        return _store.InsertNamedRevisionAsync(snapshot, idempotencyKey, actor, reason, cancellationToken);
    }

    public async ValueTask<ProcessingGraphRegistryState> ActivateRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var activity = ProcessingGraphDeliveryTelemetry.ActivitySource.StartActivity("processing-graph.activate");
        activity?.SetTag("processing_graph.revision_id", revisionId);
        EnsureLiveEligible(await _store.ReadRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false));
        var rawGate = RawIngressLifecycleLock.ForRoot(_store.StorageRoot);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.ActivateRevisionAsync(
                revisionId, expectedVersion, idempotencyKey, actor, reason, cancellationToken).ConfigureAwait(false);
            await _store.RecordDeliveredActivationAsync(revisionId, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return state;
        }
        finally
        {
            rawGate.Release();
        }
    }

    public async ValueTask<ProcessingGraphRegistryState> RollbackRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var activity = ProcessingGraphDeliveryTelemetry.ActivitySource.StartActivity("processing-graph.rollback");
        activity?.SetTag("processing_graph.target_revision_id", revisionId);
        EnsureLiveEligible(await _store.ReadRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false));
        var rawGate = RawIngressLifecycleLock.ForRoot(_store.StorageRoot);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _store.RollbackRevisionAsync(
                revisionId, expectedVersion, idempotencyKey, actor, reason, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return state;
        }
        finally
        {
            rawGate.Release();
        }
    }

    public async ValueTask StageAsync(
        ProcessingGraphDeliveryProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        EnsureProposalEnvelope(proposal);
        var stored = await _store.UpsertDeliveryProposalAsync(proposal, cancellationToken).ConfigureAwait(false);
        if (stored is not ProcessingGraphLocalProposalDisposition.Pending)
        {
            return;
        }
        // Staging is conditioned on the local active revision. Operator activation/rollback run under the raw-ingress
        // lifecycle lock, so staging takes the same lock to observe a stable registry while it compiles and validates
        // the delivered revision. Configured-basic refreshes are NOT excluded here: raw-ingress accept and recovery
        // binding hold the lifecycle lock while acquiring _configurationGate, so taking _configurationGate here (in
        // either order relative to the lifecycle lock) would invert the lock order and deadlock. Correctness against a
        // concurrent configured-basic refresh comes from SettleDeliveryProposalAsync, which re-reads the active
        // revision inside the same immediate SQLite transaction that records acceptance, and every registry mutation
        // (UpsertConfiguredBasicRevisionAsync, ActivateRevisionAsync, RollbackRevisionAsync) commits inside its own
        // immediate transaction; a stale expectation settles as Rejected(active-revision-changed).
        var rawGate = RawIngressLifecycleLock.ForRoot(_store.StorageRoot);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StageUnderLifecycleLocksAsync(proposal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawGate.Release();
        }
    }

    private async ValueTask StageUnderLifecycleLocksAsync(
        ProcessingGraphDeliveryProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        try
        {
            if (proposal.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                await _store.ExpireDeliveryProposalAsync(
                    proposal.ProposalId, "proposal-expired", cancellationToken).ConfigureAwait(false);
                return;
            }
            EnsureProposalCompatibility(proposal);
            var registry = await _store.ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    registry.ActiveRevisionId,
                    proposal.ExpectedActiveLocalRevisionId,
                    StringComparison.Ordinal))
            {
                await _store.RejectDeliveryProposalAsync(
                    proposal.ProposalId, "active-revision-changed", cancellationToken).ConfigureAwait(false);
                return;
            }
            var baseConfiguration = Volatile.Read(ref _baseConfiguration)
                ?? throw new InvalidOperationException("The CameraAgent configuration has not been initialized.");
            var pipeline = CreatePipeline(proposal.Definition, baseConfiguration.Pipeline);
            var localName = string.Concat("central-", proposal.CatalogRevisionId.ToString("N"));
            var snapshot = CreateRevisionSnapshot(
                baseConfiguration with { Pipeline = pipeline },
                localName,
                proposal.Definition.Revision,
                _timeProvider.GetUtcNow(),
                sourceDefinition: proposal.Definition);
            if (!string.Equals(snapshot.State.DefinitionIdentitySha256, proposal.DefinitionIdentitySha256,
                    StringComparison.Ordinal) ||
                !string.Equals(snapshot.State.SharedPlanIdentitySha256, proposal.SharedPlanIdentitySha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The locally compiled processing graph identity does not match the proposal.");
            }
            var created = await _store.InsertNamedRevisionAsync(
                snapshot,
                string.Concat("central-create-", proposal.ProposalId.ToString("N")),
                "central-delivery",
                "proposal-staged",
                cancellationToken).ConfigureAwait(false);
            var validated = await _store.ValidateRevisionAsync(
                created.RevisionId,
                string.Concat("central-validate-", proposal.ProposalId.ToString("N")),
                "central-delivery",
                "proposal-validated",
                cancellationToken).ConfigureAwait(false);
            if (proposal.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                await _store.ExpireDeliveryProposalAsync(
                    proposal.ProposalId, "proposal-expired", cancellationToken).ConfigureAwait(false);
                return;
            }
            await _store.AcceptDeliveryProposalAsync(
                proposal.ProposalId,
                validated,
                cancellationToken,
                proposal.ExpectedActiveLocalRevisionId,
                enforceExpectedActive: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedProposalValidationFailure(exception))
        {
            if (proposal.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                await _store.ExpireDeliveryProposalAsync(
                    proposal.ProposalId, "proposal-expired", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _store.RejectDeliveryProposalAsync(
                    proposal.ProposalId, "local-validation-failed", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask ObserveActiveRevisionAsync(CancellationToken cancellationToken)
    {
        var registry = await _store.ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
        await _store.RecordDeliveredActivationAsync(registry.ActiveRevisionId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ExpirePendingProposalsAsync(CancellationToken cancellationToken)
        => _store.ExpireStalePendingProposalsAsync(cancellationToken);

    public ValueTask<ProcessingGraphDeliveryFactV1?> ReadPendingFactAsync(CancellationToken cancellationToken)
        => _store.ReadPendingDeliveryFactAsync(cancellationToken);

    public ValueTask AcknowledgeFactAsync(Guid factId, CancellationToken cancellationToken)
        => _store.AcknowledgeDeliveryFactAsync(factId, cancellationToken);

    public ValueTask SupersedeProposalAsync(Guid proposalId, CancellationToken cancellationToken)
        => _store.SupersedeDeliveryProposalAsync(proposalId, cancellationToken);

    public ValueTask RetryFactAsync(
        Guid factId,
        DateTimeOffset nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken)
        => _store.RetryDeliveryFactAsync(factId, nextAttemptUtc, reasonCode, cancellationToken);

    public ValueTask RejectFactAsync(Guid factId, string reasonCode, CancellationToken cancellationToken)
        => _store.RejectDeliveryFactAsync(factId, reasonCode, cancellationToken);

    public ValueTask<ProcessingGraphDeliveryBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
        => _store.ReadDeliveryBacklogAsync(cancellationToken);

    public ValueTask<ProcessingGraphRevisionState> ValidateRevisionAsync(
        string revisionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => _store.ValidateRevisionAsync(revisionId, idempotencyKey, actor, reason, cancellationToken);

    public ValueTask<ProcessingGraphRegistryState> RetireRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => _store.RetireRevisionAsync(
            revisionId, expectedVersion, idempotencyKey, actor, reason, cancellationToken);

    public ValueTask<ProcessingReplaySubmissionResult> SubmitReplayAsync(
        ProcessingReplaySubmission submission,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
        => SubmitReplayCoreAsync(submission, idempotencyKey, actor, cancellationToken);

    public ValueTask<IReadOnlyList<ProcessingGraphExecutionState>> ReadExecutionsAsync(
        ProcessingGraphExecutionClass? executionClass,
        int maximumCount,
        CancellationToken cancellationToken)
        => _store.ReadExecutionsAsync(executionClass, maximumCount, cancellationToken);

    public ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
        => _store.ReadExecutionAsync(executionId, cancellationToken);

    public ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken)
        => _store.ReadExecutionDetailAsync(executionId, cancellationToken);

    public async ValueTask<CapturePipelineConfig?> ReadRevisionPipelineAsync(
        string revisionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _store.ReadRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)).Pipeline;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read-only accessor for the immutable revision body an evidence export projects: the canonical graph
    /// definition and the frozen plan already persisted for <paramref name="revisionId"/>. It writes nothing and
    /// changes no durable state.
    /// </summary>
    public ValueTask<ProcessingGraphRevisionSnapshot> ReadRevisionSnapshotAsync(
        string revisionId,
        CancellationToken cancellationToken)
        => _store.ReadRevisionAsync(revisionId, cancellationToken);

    /// <summary>Forward, resumable sweep over terminal executions for the durable evidence exporter.</summary>
    public ValueTask<IReadOnlyList<ProcessingGraphTerminalExecution>> ReadTerminalExecutionsAsync(
        long afterTerminalUnixMs,
        string afterExecutionId,
        int maximumCount,
        CancellationToken cancellationToken)
        => _store.ReadTerminalExecutionsAsync(
            afterTerminalUnixMs, afterExecutionId, maximumCount, cancellationToken);

    /// <summary>The lowest terminal ordering key the source still holds, or null when it holds none.</summary>
    public ValueTask<long?> ReadOldestTerminalExecutionKeyAsync(CancellationToken cancellationToken)
        => _store.ReadOldestTerminalExecutionKeyAsync(cancellationToken);

    /// <summary>The barrier the evidence sweep may not advance past; null when nothing is still active.</summary>
    public ValueTask<long?> ReadOldestActiveExecutionKeyAsync(CancellationToken cancellationToken)
        => _store.ReadOldestActiveExecutionKeyAsync(cancellationToken);

    /// <summary>Central assignment provenance for a revision, or null when the revision was compiled locally.</summary>
    public ValueTask<ExecutionEvidenceAssignmentProvenanceV1?> ReadAssignmentProvenanceAsync(
        string revisionId,
        CancellationToken cancellationToken)
        => _store.ReadAssignmentProvenanceAsync(revisionId, cancellationToken);

    public ValueTask<ProcessingGraphExecutionState> CancelReplayAsync(
        Guid executionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
        => _store.CancelReplayAsync(
            executionId, idempotencyKey, actor, reason, cancellationToken);

    private async ValueTask<ProcessingReplaySubmissionResult> SubmitReplayCoreAsync(
        ProcessingReplaySubmission submission,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentOutOfRangeException.ThrowIfEqual(submission.CaptureId, Guid.Empty);
        if (submission.PrimaryArtifactId == Guid.Empty || submission.Priority is < -1000 or > 1000 ||
            string.IsNullOrWhiteSpace(submission.TriggerKind) || submission.TriggerKind.Length > 64 ||
            submission.TriggerReference?.Length > 128)
        {
            throw new ArgumentException("The processing replay request is invalid.", nameof(submission));
        }
        var revision = await _store.ReadRevisionAsync(submission.GraphRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision.State.Lifecycle is ProcessingGraphRevisionLifecycle.Draft or ProcessingGraphRevisionLifecycle.Retired)
        {
            throw new ProcessingGraphStoreConflictException(
                "The selected processing graph revision is not eligible for replay.");
        }
        var source = await _store.ReadReplaySourceAsync(
            submission.CaptureId, submission.PrimaryArtifactId, cancellationToken).ConfigureAwait(false);
        var configuration = source.Configuration with { Pipeline = revision.Pipeline };
        var executionId = StableExecutionId(new
        {
            SchemaVersion = "cameraagent-processing-replay-execution-v1",
            IdempotencyKey = idempotencyKey,
            Actor = actor,
            CaptureId = submission.CaptureId.ToString("N"),
            ArtifactId = source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId.ToString("N"),
            revision.State.RevisionId,
            submission.TriggerKind,
            submission.TriggerReference,
            submission.Priority
        });
        var result = await _store.InsertReplayAsync(
            submission,
            source,
            revision,
            executionId,
            CanonicalBytes(configuration),
            idempotencyKey,
            actor,
            cancellationToken).ConfigureAwait(false);
        _replayWakeup.Signal();
        return result;
    }

    private ProcessingGraphRevisionSnapshot CreateRevisionSnapshot(
        CameraModuleConfig configuration,
        string name,
        string revision,
        DateTimeOffset createdUtc,
        ProcessingGraphRevisionLifecycle lifecycle = ProcessingGraphRevisionLifecycle.Draft,
        ProcessingGraphDefinition? sourceDefinition = null)
    {
        using var graph = new DisposableProcessingGraph(sourceDefinition is null
            ? _pipelineFactory.CreateGraph(configuration)
            : _pipelineFactory.CreateGraph(configuration, sourceDefinition.Name, sourceDefinition.Revision));
        var sharedPlan = graph.Value.SharedPlan
            ?? throw new InvalidOperationException("Processing graph revisions require explicit pipeline v2 compilation.");
        if (sourceDefinition is not null && !ProcessingGraphJson.SerializeCanonical(sourceDefinition)
                .AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(sharedPlan.CanonicalDefinition.GetRawText())))
        {
            throw new InvalidDataException("The delivered processing graph cannot be represented by this CameraAgent.");
        }
        var localPlanIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            SchemaVersion = "cameraagent-processing-local-plan-v1",
            SharedPlanIdentitySha256 = sharedPlan.PlanIdentitySha256,
            Nodes = graph.Value.Nodes.Select(static node => new
            {
                node.Id,
                node.PlanSha256,
                node.SharedPlanNodeIdentitySha256,
                Publication = node.Publication?.Persistence.ToString()
            }).ToArray()
        });
        var revisionId = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            SchemaVersion = "cameraagent-processing-graph-revision-v1",
            Name = name,
            Revision = revision,
            sharedPlan.DefinitionIdentitySha256,
            sharedPlan.PlanIdentitySha256,
            LocalPlanIdentitySha256 = localPlanIdentity
        });
        var sharedNodes = sharedPlan.Nodes.ToDictionary(
            static node => node.Definition.Id,
            StringComparer.Ordinal);
        var nodes = graph.Value.Nodes.Select(node =>
        {
            var sharedNode = sharedNodes[node.Id];
            return new ProcessingExecutionNodeSeed(
                node.Id,
                node.Required,
                node.PlanSha256,
                node.SharedPlanNodeIdentitySha256,
                CanonicalText(sharedNode.Definition.Dependencies),
                CanonicalText(sharedNode.Definition.Inputs),
                CanonicalText(sharedNode.Definition.Outputs),
                sharedNode.Definition.Window is null ? null : CanonicalText(sharedNode.Definition.Window));
        }).ToImmutableArray();
        var frozenPlan = CanonicalBytes(new
        {
            SchemaVersion = "cameraagent-processing-frozen-plan-v1",
            RevisionId = revisionId,
            sharedPlan.DefinitionIdentitySha256,
            SharedPlanIdentitySha256 = sharedPlan.PlanIdentitySha256,
            LocalPlanIdentitySha256 = localPlanIdentity,
            Nodes = nodes
        });
        var state = new ProcessingGraphRevisionState(
            revisionId,
            name,
            revision,
            lifecycle,
            sharedPlan.DefinitionIdentitySha256,
            sharedPlan.PlanIdentitySha256,
            localPlanIdentity,
            createdUtc,
            lifecycle == ProcessingGraphRevisionLifecycle.Validated ? createdUtc : null,
            null,
            null);
        return new(
            state,
            configuration.Pipeline,
            CanonicalBytes(configuration.Pipeline),
            Encoding.UTF8.GetBytes(sharedPlan.CanonicalDefinition.GetRawText()),
            frozenPlan,
            nodes);
    }

    private static CapturePipelineConfig CreatePipeline(
        ProcessingGraphDefinition definition,
        CapturePipelineConfig basePipeline)
    {
        if (definition.Sources.Length != 1 ||
            !string.Equals(definition.Sources[0].Id, "$raw", StringComparison.Ordinal))
        {
            throw new InvalidDataException("CameraAgent delivery supports only the canonical raw source.");
        }
        var baseById = basePipeline.Steps.ToDictionary(
            static step => string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim(),
            StringComparer.OrdinalIgnoreCase);
        var incomingById = definition.Nodes.ToDictionary(static node => node.Id, StringComparer.OrdinalIgnoreCase);
        if (basePipeline.Steps.Any(step =>
                step.Enabled != false &&
                LocalPolicyStepAliases.Contains(step.Type) &&
                (!incomingById.TryGetValue(
                        string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim(),
                        out var incoming) ||
                    !string.Equals(incoming.StepAlias, step.Type, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException(
                $"The delivered graph must preserve every required local {LocalPolicyNodeDescription} node.");
        }
        var steps = definition.Nodes.Select(node =>
        {
            baseById.TryGetValue(node.Id, out var local);
            var matchesLocalType = local is not null &&
                string.Equals(local.Type, node.StepAlias, StringComparison.OrdinalIgnoreCase);
            if (LocalPolicyStepAliases.Contains(node.StepAlias))
            {
                if (!matchesLocalType || local!.Enabled == false)
                {
                    throw new InvalidDataException(
                        $"The delivered graph cannot add or replace local {LocalPolicyNodeDescription} policy.");
                }
                return local;
            }
            return new CaptureProcessingStepConfig(
                node.StepAlias,
                node.Id,
                node.Order,
                node.EffectiveOptions,
                node.Dependencies.Select(static dependency => dependency.ProducerId).ToArray(),
                node.FailurePolicy == ProcessingGraphNodeFailurePolicy.Required,
                node.Enabled,
                matchesLocalType ? local!.Publication : null);
        }).ToList();
        return new(steps);
    }

    internal static bool IsExpectedProposalValidationFailure(Exception exception)
        => exception is ArgumentException or ValidationException or InvalidDataException or
            ProcessingGraphStoreConflictException or KeyNotFoundException or JsonException or NotSupportedException or
            FormatException or OverflowException or OptionsValidationException;

    private void EnsureProposalEnvelope(ProcessingGraphDeliveryProposalV1 proposal)
    {
        if (!string.Equals(proposal.SchemaVersion, ProcessingGraphDeliverySchemaVersions.Current, StringComparison.Ordinal) ||
            proposal.ProposalId == Guid.Empty || proposal.CatalogRevisionId == Guid.Empty ||
            proposal.AssignmentId == Guid.Empty || proposal.RegistrationId == Guid.Empty ||
            proposal.LogicalCameraInstallationId == Guid.Empty || proposal.InstallationPublicId == Guid.Empty ||
            proposal.IssuedAtUtc.Offset != TimeSpan.Zero || proposal.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            proposal.ExpiresAtUtc <= proposal.IssuedAtUtc ||
            !string.Equals(proposal.CapabilitySnapshotSha256, Capabilities.IdentitySha256, StringComparison.Ordinal) ||
            !IsSha256(proposal.DefinitionIdentitySha256) || !IsSha256(proposal.SharedPlanIdentitySha256) ||
            proposal.Definition is null)
        {
            throw new InvalidDataException("The processing graph delivery proposal is invalid.");
        }
        var compiled = ProcessingGraphCompiler.Compile(proposal.Definition);
        if (!compiled.IsValid ||
            !string.Equals(compiled.Plan!.DefinitionIdentitySha256, proposal.DefinitionIdentitySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The processing graph proposal identity is invalid.");
        }
    }

    private void EnsureProposalCompatibility(ProcessingGraphDeliveryProposalV1 proposal)
    {
        var compiled = ProcessingGraphCompiler.Compile(
            proposal.Definition,
            new(ProcessingGraphHosts.CameraAgent, Capabilities.CapabilityLabels));
        var aliases = Capabilities.StepAliases.ToHashSet(StringComparer.Ordinal);
        if (!compiled.IsValid || proposal.Definition.Nodes.Any(node => node.Enabled && !aliases.Contains(node.StepAlias)) ||
            !string.Equals(compiled.Plan!.DefinitionIdentitySha256, proposal.DefinitionIdentitySha256,
                StringComparison.Ordinal) ||
            !string.Equals(compiled.Plan.PlanIdentitySha256, proposal.SharedPlanIdentitySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The processing graph proposal identity is invalid.");
        }
    }

    internal static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static byte[] CanonicalBytes<T>(T value)
        => Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(value)).GetRawText());

    private static string CanonicalText<T>(T value)
        => Encoding.UTF8.GetString(CanonicalBytes(value));

    private static Guid StableExecutionId<T>(T value)
    {
        var hash = Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    internal static string SupersededRevisionName(string revisionName, ProcessingGraphRevisionState state)
        => string.Concat(
            revisionName,
            "-",
            CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                SchemaVersion = "cameraagent-processing-configured-basic-contract-v1",
                state.DefinitionIdentitySha256,
                state.SharedPlanIdentitySha256,
                state.LocalPlanIdentitySha256
            })[..16]);

    internal static void EnsureExplicitPipeline(CapturePipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2 ||
            pipeline.DependencyPolicy != CapturePipelineDependencyPolicy.RejectEnabledDependent)
        {
            throw new InvalidOperationException(
                "Durable processing graph revisions require cameraagent-capture-pipeline-v2 with reject-enabled-dependent-v1 policy.");
        }
    }

    internal static void EnsureLiveEligible(ProcessingGraphRevisionSnapshot revision)
    {
        if (revision.Nodes.Any(static node => node.WindowJson is { } windowJson &&
                JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(windowJson, SerializerOptions)?.Kind ==
                ProcessingGraphWindowKind.Centered))
        {
            throw new ProcessingGraphStoreConflictException(
                "Centered processing windows are replay-only and cannot be activated for immediate live execution.");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void ValidateRevisionName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("The processing graph revision name is invalid.", parameterName);
        }
    }

    private sealed class DisposableProcessingGraph(CaptureProcessingGraph value) : IDisposable
    {
        internal CaptureProcessingGraph Value { get; } = value;

        public void Dispose() => Value.DisposeSteps();
    }

    public void Dispose() => _configurationGate.Dispose();
}
