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

    public CaptureProcessingContext(
        CameraModuleConfig config,
        CaptureLoopSubmission submission,
        RawCaptureReceipt? rawCapture = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _artifacts = submission.Result.Artifacts
            ?? (submission.Result.Frame is { } frame ? new FrameArtifactSet(frame) : null);
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
        IReadOnlyList<Guid>? sourceArtifactIds = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _artifacts = (_artifacts ?? new FrameArtifactSet(frame)).WithDerivative(role, frame, recipeVersion, sourceArtifactIds);
        _submission = _submission with { Result = _submission.Result with { Artifacts = _artifacts } };
        return _artifacts[role];
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
}

public interface ICaptureProcessingStep
{
    string Name { get; }

    int Order { get; }

    ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken);
}
