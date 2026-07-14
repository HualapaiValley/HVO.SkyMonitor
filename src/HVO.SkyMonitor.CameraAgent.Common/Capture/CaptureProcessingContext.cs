using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CaptureProcessingContext
{
    private CaptureLoopSubmission _submission;
    private readonly List<CaptureProcessingStepTelemetry> _stepTelemetry = new();
    private FrameArtifactSet? _artifacts;
    private readonly List<ProcessingOutcome> _processingOutcomes = new();
    private readonly Dictionary<Guid, ProcessingProduct> _processingProductsByArtifactId = new();
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

    public FrameArtifactSet? Artifacts => _artifacts;

    public RawCaptureReceipt? RawCapture { get; }

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
        _processingProductsByArtifactId[artifact.ArtifactId] = product;
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
        _processingProductsByArtifactId[artifact.ArtifactId] = product;
    }

    internal static Guid CreateArtifactId(string outputIdentitySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIdentitySha256);
        var bytes = Convert.FromHexString(outputIdentitySha256);
        if (bytes.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("Output identity must be a SHA-256 value.", nameof(outputIdentitySha256));
        }
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public interface ICaptureProcessingStep
{
    string Name { get; }

    int Order { get; }

    ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken);
}
