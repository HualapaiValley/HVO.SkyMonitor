using System.Collections.Immutable;
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

internal sealed class ProcessingGraphOperationsCoordinator : IProcessingGraphOperations, IDisposable
{
    private const string ConfiguredGraphName = "configured-basic";
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory;
    private readonly SqliteCaptureProcessingStore _store;
    private readonly ProcessingGraphExecutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ProcessingReplayWakeup _replayWakeup;
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
            var revision = CreateRevisionSnapshot(
                configuration,
                ConfiguredGraphName,
                pipelineIdentity[..16],
                _timeProvider.GetUtcNow(),
                ProcessingGraphRevisionLifecycle.Validated);
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
        EnsureLiveEligible(await _store.ReadRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false));
        var rawGate = RawIngressLifecycleLock.ForRoot(_store.StorageRoot);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.ActivateRevisionAsync(
                revisionId, expectedVersion, idempotencyKey, actor, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawGate.Release();
        }
    }

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
        ProcessingGraphRevisionLifecycle lifecycle = ProcessingGraphRevisionLifecycle.Draft)
    {
        using var graph = new DisposableProcessingGraph(_pipelineFactory.CreateGraph(configuration));
        var sharedPlan = graph.Value.SharedPlan
            ?? throw new InvalidOperationException("Processing graph revisions require explicit pipeline v2 compilation.");
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

    private static void EnsureExplicitPipeline(CapturePipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2 ||
            pipeline.DependencyPolicy != CapturePipelineDependencyPolicy.RejectEnabledDependent)
        {
            throw new InvalidOperationException(
                "Durable processing graph revisions require cameraagent-capture-pipeline-v2 with reject-enabled-dependent-v1 policy.");
        }
    }

    private static void EnsureLiveEligible(ProcessingGraphRevisionSnapshot revision)
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

    private static void ValidateRevisionName(string value, string parameterName)
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
