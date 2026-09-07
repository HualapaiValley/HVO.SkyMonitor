using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CaptureProcessingContext
{
    private CaptureLoopSubmission _submission;
    private readonly List<CaptureProcessingStepTelemetry> _stepTelemetry = new();
    private FrameArtifactSet? _artifacts;
    private readonly List<ProcessingOutcome> _processingOutcomes = new();
    private readonly Dictionary<Guid, ProcessingProduct> _processingProductsByArtifactId = new();
    private readonly Dictionary<string, List<Guid>> _productArtifactIdsByNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FrameArtifact> _allArtifacts = new();
    private readonly Dictionary<string, List<FrameArtifact>> _artifactsByNode = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentNodeId;
    private IReadOnlyList<string> _currentDependencies = [];
    private IReadOnlyList<string> _currentDeclaredDependencies = [];
    private IReadOnlyList<ProcessingArtifact> _historicalInputs = [];
    private IReadOnlyList<ProcessingArtifact> _frozenAuxiliaryInputs = [];
    private FrozenAuxiliaryInputFailure? _frozenAuxiliaryInputFailure;
    private readonly List<DurableProcessingNodeInput> _currentInputs = [];
    private readonly Func<CancellationToken, ValueTask<CaptureResult>>? _rawFrameLoader;
    private bool _rawFrameLoaded;

    public CaptureProcessingContext(
        CameraModuleConfig config,
        CaptureLoopSubmission submission,
        RawCaptureReceipt? rawCapture = null)
        : this(config, submission, rawCapture, null, null)
    {
    }

    internal CaptureProcessingContext(
        CameraModuleConfig config,
        CaptureLoopSubmission submission,
        RawCaptureReceipt? rawCapture,
        Func<CancellationToken, ValueTask<CaptureResult>>? rawFrameLoader,
        ProcessingExecutionContext? processingExecution)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _artifacts = submission.Result.Artifacts
            ?? (submission.Result.Frame is { } frame ? new FrameArtifactSet(frame) : null);
        if (_artifacts is not null)
        {
            _allArtifacts.AddRange(_artifacts.Artifacts.Values);
            _rawFrameLoaded = true;
        }
        RawCapture = rawCapture;
        _rawFrameLoader = rawFrameLoader;
        ProcessingExecution = processingExecution;
    }

    public CameraModuleConfig Config { get; }

    public CaptureLoopSubmission Submission => _submission;

    public CameraFrame? Frame => _submission.Result.Frame;

    public CaptureAcquisitionTiming? AcquisitionTiming => _submission.Result.AcquisitionTiming;

    public FrameArtifactSet? Artifacts => _artifacts;

    public RawCaptureReceipt? RawCapture { get; }

    public ProcessingExecutionContext? ProcessingExecution { get; }

    public ReconstructionDescriptor? ReconstructionDescriptor => RawCapture?.Manifest.Descriptor;

    internal async ValueTask EnsureRawFrameAsync(CancellationToken cancellationToken)
    {
        if (_rawFrameLoaded || _rawFrameLoader is null)
        {
            return;
        }
        var result = await _rawFrameLoader(cancellationToken).ConfigureAwait(false);
        var rawArtifacts = result.Artifacts ?? (result.Frame is { } frame ? new FrameArtifactSet(frame) : null);
        if (rawArtifacts is null)
        {
            throw new InvalidDataException("Raw reconstruction did not produce an artifact set.");
        }
        foreach (var derivative in _allArtifacts.Where(static artifact => artifact.Role != FrameArtifactRole.Raw).ToArray())
        {
            rawArtifacts = rawArtifacts.WithDerivative(
                derivative.Role,
                derivative.Frame,
                derivative.RecipeVersion,
                derivative.SourceArtifactIds,
                derivative.ArtifactId);
        }
        _artifacts = rawArtifacts;
        _rawFrameLoaded = true;
        _allArtifacts.RemoveAll(static artifact => artifact.Role == FrameArtifactRole.Raw);
        _allArtifacts.Insert(0, rawArtifacts.Raw);
        _submission = _submission with
        {
            Result = result with { Frame = rawArtifacts.Raw.Frame, Artifacts = rawArtifacts }
        };
    }

    public IReadOnlyList<CaptureProcessingStepTelemetry> StepTelemetry => _stepTelemetry;

    public IReadOnlyList<ProcessingOutcome> ProcessingOutcomes => _processingOutcomes;

    public IReadOnlyList<ProcessingProduct> ProcessingProducts =>
        _processingOutcomes.SelectMany(static outcome => outcome.Products).ToArray();

    public IReadOnlyList<FrameArtifact> AllArtifacts => _allArtifacts;

    public void ReplaceFrame(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _artifacts = _artifacts?.WithDerivative(FrameArtifactRole.Calibrated, frame)
            ?? new FrameArtifactSet(frame);
        var result = _submission.Result with { Frame = frame, Artifacts = _artifacts };
        _submission = _submission with { Result = result };
    }

    public FrameArtifact AddDerivative(
        FrameArtifactRole role,
        CameraFrame frame,
        string? recipeVersion = null,
        IReadOnlyList<Guid>? sourceArtifactIds = null,
        Guid? artifactId = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _artifacts = (_artifacts ?? new FrameArtifactSet(frame)).WithDerivative(
            role, frame, recipeVersion, sourceArtifactIds, artifactId);
        _submission = _submission with { Result = _submission.Result with { Artifacts = _artifacts } };
        var artifact = _artifacts[role];
        _allArtifacts.RemoveAll(existing => existing.ArtifactId == artifact.ArtifactId);
        _allArtifacts.Add(artifact);
        if (_currentNodeId is not null)
        {
            if (!_artifactsByNode.TryGetValue(_currentNodeId, out var nodeArtifacts))
            {
                nodeArtifacts = [];
                _artifactsByNode[_currentNodeId] = nodeArtifacts;
            }
            nodeArtifacts.Add(artifact);
        }
        return artifact;
    }

    public void UpdateResult(CaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _submission = _submission with { Result = result };
    }

    internal void AddStepTelemetry(CaptureProcessingStepTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _stepTelemetry.Add(telemetry);
    }

    internal void AddProcessingOutcome(ProcessingOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _processingOutcomes.Add(outcome);
    }

    internal void AssociateProcessingProduct(FrameArtifact artifact, ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(product);
        if (artifact.Role != product.Role)
        {
            throw new ArgumentException("Artifact and processing product roles must match.", nameof(product));
        }
        if (artifact.ArtifactId != CreateArtifactId(product.OutputIdentitySha256))
        {
            throw new ArgumentException("Artifact and processing product identities must match.", nameof(product));
        }
        RegisterProcessingProduct(product);
    }

    internal ProcessingProduct? GetProcessingProduct(Guid artifactId) =>
        _processingProductsByArtifactId.GetValueOrDefault(artifactId);

    internal void BeginNode(
        string nodeId,
        IReadOnlyList<string> dependencies,
        IReadOnlyList<string>? declaredDependencies = null)
    {
        _currentNodeId = nodeId;
        _currentDependencies = dependencies;
        _currentDeclaredDependencies = declaredDependencies ?? dependencies;
        _currentInputs.Clear();
        _frozenAuxiliaryInputs = [];
        _frozenAuxiliaryInputFailure = null;
    }

    internal IReadOnlyList<FrameArtifact> GetDependencyArtifacts()
    {
        var artifacts = _currentDependencies
            .Where(_artifactsByNode.ContainsKey)
            .SelectMany(dependency => _artifactsByNode[dependency])
            .ToList();
        if (_currentDeclaredDependencies.Any(static dependency =>
                string.Equals(dependency, "$raw", StringComparison.OrdinalIgnoreCase)) &&
            _artifacts?.Raw is { } raw)
        {
            artifacts.Insert(0, raw);
        }
        return artifacts;
    }

    internal IReadOnlyList<ProcessingProduct> GetDependencyProducts()
        => _currentDependencies
            .Where(_productArtifactIdsByNode.ContainsKey)
            .SelectMany(dependency => _productArtifactIdsByNode[dependency])
            .Select(artifactId => _processingProductsByArtifactId[artifactId])
            .ToArray();

    internal CaptureProcessingPublicationPolicy? GetDependencyPublicationPolicy(Guid artifactId)
    {
        var nodeId = GetDependencyProducerStepId(artifactId);
        return nodeId is null
            ? null
            : Config.Pipeline?.Steps.SingleOrDefault(step => string.Equals(
                string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim(),
                nodeId,
                StringComparison.OrdinalIgnoreCase))?.Publication;
    }

    internal string? GetDependencyProducerStepId(Guid artifactId)
        => _currentDependencies.SingleOrDefault(dependency =>
            _artifactsByNode.GetValueOrDefault(dependency)?.Any(artifact => artifact.ArtifactId == artifactId) == true ||
            _productArtifactIdsByNode.GetValueOrDefault(dependency)?.Contains(artifactId) == true);

    internal IReadOnlyList<CaptureProcessingStepTelemetry> GetDependencyStepTelemetry()
        => _stepTelemetry
            .Where(telemetry => _currentDependencies.Contains(telemetry.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    internal bool HasDeclaredDependencies => _currentDeclaredDependencies.Count > 0;

    internal void SetHistoricalInputs(IReadOnlyList<ProcessingArtifact> historicalInputs)
        => _historicalInputs = historicalInputs;

    internal bool IsReplayExecution => ProcessingExecution?.ExecutionClass == ProcessingGraphExecutionClass.Replay;

    internal void SetFrozenAuxiliaryInputs(IReadOnlyList<ProcessingArtifact> inputs)
    {
        _frozenAuxiliaryInputs = inputs;
        _frozenAuxiliaryInputFailure = null;
    }

    internal void SetFrozenAuxiliaryInputFailure(FrozenAuxiliaryInputFailure failure)
    {
        _frozenAuxiliaryInputs = [];
        _frozenAuxiliaryInputFailure = failure;
    }

    internal IReadOnlyList<ProcessingArtifact> GetFrozenAuxiliaryInputs() => _frozenAuxiliaryInputs;

    internal FrozenAuxiliaryInputFailure? FrozenAuxiliaryInputFailure => _frozenAuxiliaryInputFailure;

    internal IReadOnlyList<ProcessingArtifact> GetHistoricalInputs() => _historicalInputs;

    internal IReadOnlyList<DurableProcessingNodeInput> GetCurrentInputEvidence() => _currentInputs.ToArray();

    internal string? CurrentNodeId => _currentNodeId;

    internal void RecordExecutionRequest(ProcessingExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectedArtifactId = request.InputArtifactId;
        if (selectedArtifactId is not null &&
            request.Inputs.Count(input => input.ArtifactId == selectedArtifactId) != 1)
        {
            throw new InvalidOperationException("The selected processing input must identify exactly one submitted artifact.");
        }
        var evidence = new List<DurableProcessingNodeInput>();
        foreach (var input in request.Inputs)
        {
            evidence.Add(new DurableProcessingNodeInput(
                _currentInputs.Count + evidence.Count, "Artifact", null, input.ArtifactId, input.Role, input.Variant,
                input.RecipeIdentitySha256, null, null, input.ArtifactId == selectedArtifactId));
        }
        foreach (var auxiliary in request.AuxiliaryInputs ?? [])
        {
            evidence.Add(new DurableProcessingNodeInput(
                _currentInputs.Count + evidence.Count,
                auxiliary.Kind == ProcessingAuxiliaryInputKind.Artifact ? "AuxiliaryArtifact" : "CanonicalContext",
                auxiliary.Name,
                auxiliary.ArtifactId,
                auxiliary.Selector?.Role,
                auxiliary.Selector?.Variant,
                auxiliary.Selector?.RecipeIdentitySha256,
                auxiliary.SchemaVersion,
                auxiliary.IdentitySha256,
                false));
        }
        if (request.Annotation is { } annotation)
        {
            evidence.Add(new DurableProcessingNodeInput(
                _currentInputs.Count + evidence.Count, "CanonicalContext", "annotation", null, null, null, null,
                "processing-annotation-v1", annotation.ProvenanceSha256, false));
        }
        AppendInputEvidence(evidence);
    }

    internal void RecordExecutionOutcome(ProcessingOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var selectedArtifactIds = outcome.Products
            .SelectMany(static product => product.SourceArtifactIds)
            .ToHashSet();
        if (selectedArtifactIds.Count == 0)
        {
            return;
        }
        for (var index = 0; index < _currentInputs.Count; index++)
        {
            var input = _currentInputs[index];
            if (input.ArtifactId is { } artifactId && selectedArtifactIds.Contains(artifactId))
            {
                _currentInputs[index] = input with { Selected = true };
            }
        }
    }

    internal void RecordConsumedArtifacts(
        IReadOnlyList<FrameArtifact> artifacts,
        IReadOnlyList<ProcessingProduct> products)
    {
        var evidence = new List<DurableProcessingNodeInput>(artifacts.Count + products.Count);
        var recordedIds = new HashSet<Guid>();
        foreach (var artifact in artifacts)
        {
            var product = GetProcessingProduct(artifact.ArtifactId);
            evidence.Add(new DurableProcessingNodeInput(
                _currentInputs.Count + evidence.Count,
                "Artifact",
                null,
                artifact.ArtifactId,
                artifact.Role,
                product?.Variant ?? (ReconstructionDescriptor?.Artifact.ArtifactId == artifact.ArtifactId
                    ? ReconstructionDescriptor.Artifact.Variant
                    : null),
                product?.Recipe.IdentitySha256 ?? (artifact.RecipeVersion is { } recipeVersion && IsSha256(recipeVersion)
                    ? recipeVersion
                    : null),
                null,
                null,
                true));
            recordedIds.Add(artifact.ArtifactId);
        }
        foreach (var product in products)
        {
            var artifactId = CreateArtifactId(product.OutputIdentitySha256);
            if (!recordedIds.Add(artifactId))
            {
                continue;
            }
            evidence.Add(new DurableProcessingNodeInput(
                _currentInputs.Count + evidence.Count,
                "Artifact",
                null,
                artifactId,
                product.Role,
                product.Variant,
                product.Recipe.IdentitySha256,
                null,
                null,
                true));
        }
        AppendInputEvidence(evidence);
    }

    internal void RecordCanonicalInput(string name, string schemaVersion, string identitySha256)
        => AppendInputEvidence([
            new DurableProcessingNodeInput(
                _currentInputs.Count, "CanonicalContext", name, null, null, null, null,
                schemaVersion, identitySha256, false)
        ]);

    private void AppendInputEvidence(List<DurableProcessingNodeInput> evidence)
    {
        if (_currentInputs.Count + evidence.Count > 128 || evidence.Any(static input =>
                input.Ordinal is < 0 or >= 128 || input.Name is { Length: 0 or > 64 } ||
                input.Variant is { Length: 0 or > 128 } || input.SchemaVersion is { Length: 0 or > 128 } ||
                input.RecipeIdentitySha256 is { } recipe && !IsSha256(recipe) ||
                input.IdentitySha256 is { } identity && !IsSha256(identity)))
        {
            throw new InvalidOperationException("Processing input evidence exceeds its durable bounds.");
        }
        _currentInputs.AddRange(evidence);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal FrameArtifact FindArtifact(ProcessingProduct product)
        => _processingProductsByArtifactId
            .Where(pair => ReferenceEquals(pair.Value, product) ||
                string.Equals(pair.Value.OutputIdentitySha256, product.OutputIdentitySha256, StringComparison.Ordinal))
            .Select(pair => _allArtifacts.Single(artifact => artifact.ArtifactId == pair.Key))
            .Single();

    internal void RestoreProduct(string nodeId, FrameArtifact artifact, ProcessingProduct product)
    {
        _currentNodeId = nodeId;
        if (!_artifactsByNode.TryGetValue(nodeId, out var nodeArtifacts))
        {
            nodeArtifacts = [];
            _artifactsByNode[nodeId] = nodeArtifacts;
        }
        nodeArtifacts.Add(artifact);
        _allArtifacts.Add(artifact);
        if (_rawFrameLoaded)
        {
            _artifacts = _artifacts!.WithDerivative(
                artifact.Role,
                artifact.Frame,
                artifact.RecipeVersion,
                artifact.SourceArtifactIds,
                artifact.ArtifactId);
            _submission = _submission with { Result = _submission.Result with { Artifacts = _artifacts } };
        }
        RegisterProcessingProduct(product);
    }

    internal void RestoreProduct(string nodeId, Guid artifactId, ProcessingProduct product)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(product);
        if (artifactId == Guid.Empty || artifactId != CreateArtifactId(product.OutputIdentitySha256))
        {
            throw new ArgumentException("Restored product artifact identity is invalid.", nameof(artifactId));
        }
        _currentNodeId = nodeId;
        RegisterProcessingProduct(product);
    }

    internal void RegisterProcessingProduct(ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var artifactId = CreateArtifactId(product.OutputIdentitySha256);
        _processingProductsByArtifactId[artifactId] = product;
        if (_currentNodeId is null)
        {
            return;
        }
        if (!_productArtifactIdsByNode.TryGetValue(_currentNodeId, out var productIds))
        {
            productIds = [];
            _productArtifactIdsByNode[_currentNodeId] = productIds;
        }
        if (!productIds.Contains(artifactId))
        {
            productIds.Add(artifactId);
        }
    }

    internal static Guid CreateArtifactId(string outputIdentitySha256)
        => ProcessingIdentity.CreateArtifactId(outputIdentitySha256);
}

public interface ICaptureProcessingStep
{
    string Name { get; }

    int Order { get; }

    ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken);
}

/// <summary>Marks a step that consumes capture descriptors or auxiliary facts but never raw pixel content.</summary>
internal sealed class CaptureDescriptorProcessingContext(CaptureProcessingContext context)
{
    public CameraModuleConfig Config => context.Config;

    public CaptureAcquisitionTiming? AcquisitionTiming => context.AcquisitionTiming;

    public ReconstructionDescriptor? ReconstructionDescriptor => context.ReconstructionDescriptor;

    public string? CommittedManifestSha256 => context.RawCapture?.CommittedManifestSha256;

    public SceneProvenance? SceneProvenance => context.RawCapture?.Manifest.Scene;

    /// <summary>Gets whether the current node runs inside an archived replay execution.</summary>
    public bool IsReplayExecution => context.IsReplayExecution;

    /// <summary>Gets the durable outputs that replay submission pinned as this node's auxiliary inputs.</summary>
    public IReadOnlyList<ProcessingArtifact> FrozenAuxiliaryInputs => context.GetFrozenAuxiliaryInputs();

    /// <summary>Gets why the pinned auxiliary inputs could not be restored, when they could not.</summary>
    internal FrozenAuxiliaryInputFailure? FrozenAuxiliaryInputFailure => context.FrozenAuxiliaryInputFailure;

    public void RecordCanonicalInput(string name, string schemaVersion, string identitySha256)
        => context.RecordCanonicalInput(name, schemaVersion, identitySha256);

    internal async ValueTask<ProcessingOutcome> ExecuteAsync(
        CameraAgentRecipeExecutionAdapter adapter,
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var outcome = await adapter.ExecuteAsync(context, request, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    public void AddProcessingOutcome(ProcessingOutcome outcome) => context.AddProcessingOutcome(outcome);
}

/// <summary>Internal trusted contract for steps that cannot access frame artifacts or pixel payloads.</summary>
internal interface IDescriptorOnlyCaptureProcessingStep : ICaptureProcessingStep
{
    ValueTask ProcessAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken);

    ValueTask ICaptureProcessingStep.ProcessAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
        => ProcessAsync(new CaptureDescriptorProcessingContext(context), cancellationToken);
}

/// <summary>Runs only after a processing node and all of its outputs have committed durably.</summary>
internal interface IDurableCaptureProcessingPostCommit
{
    ValueTask OnCommittedAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken);
}
