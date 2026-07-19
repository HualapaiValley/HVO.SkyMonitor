using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
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
    private IReadOnlyList<ProcessingArtifact> _historicalInputs = [];

    public CaptureProcessingContext(
        CameraModuleConfig config,
        CaptureLoopSubmission submission,
        RawCaptureReceipt? rawCapture = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _artifacts = submission.Result.Artifacts
            ?? (submission.Result.Frame is { } frame ? new FrameArtifactSet(frame) : null);
        if (_artifacts is not null)
        {
            _allArtifacts.AddRange(_artifacts.Artifacts.Values);
        }
        RawCapture = rawCapture;
    }

    public CameraModuleConfig Config { get; }

    public CaptureLoopSubmission Submission => _submission;

    public CameraFrame? Frame => _submission.Result.Frame;

    public CaptureAcquisitionTiming? AcquisitionTiming => _submission.Result.AcquisitionTiming;

    public FrameArtifactSet? Artifacts => _artifacts;

    public RawCaptureReceipt? RawCapture { get; }

    public ReconstructionDescriptor? ReconstructionDescriptor => RawCapture?.Manifest.Descriptor;

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

    internal void BeginNode(string nodeId, IReadOnlyList<string> dependencies)
    {
        _currentNodeId = nodeId;
        _currentDependencies = dependencies;
    }

    internal IReadOnlyList<FrameArtifact> GetDependencyArtifacts()
        => _currentDependencies
            .Where(_artifactsByNode.ContainsKey)
            .SelectMany(dependency => _artifactsByNode[dependency])
            .ToArray();

    internal IReadOnlyList<ProcessingProduct> GetDependencyProducts()
        => _currentDependencies
            .Where(_productArtifactIdsByNode.ContainsKey)
            .SelectMany(dependency => _productArtifactIdsByNode[dependency])
            .Select(artifactId => _processingProductsByArtifactId[artifactId])
            .ToArray();

    internal bool HasDeclaredDependencies => _currentDependencies.Count > 0;

    internal void SetHistoricalInputs(IReadOnlyList<ProcessingArtifact> historicalInputs)
        => _historicalInputs = historicalInputs;

    internal IReadOnlyList<ProcessingArtifact> GetHistoricalInputs() => _historicalInputs;

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
        _artifacts = (_artifacts ?? new FrameArtifactSet(artifact.Frame)).WithDerivative(
            artifact.Role,
            artifact.Frame,
            artifact.RecipeVersion,
            artifact.SourceArtifactIds,
            artifact.ArtifactId);
        _submission = _submission with { Result = _submission.Result with { Artifacts = _artifacts } };
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
