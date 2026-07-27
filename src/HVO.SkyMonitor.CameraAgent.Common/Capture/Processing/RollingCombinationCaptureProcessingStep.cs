using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a rolling linear 16-bit combined artifact after every compatible capture.</summary>
internal sealed class RollingCombinationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    RollingCombinationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<RollingCombinationProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep, IWindowCaptureProcessingGraphStep
{
    private readonly Queue<ProcessingArtifact> _window = new();

    internal int BufferedFrameCount => _window.Count;

    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.RollingMean;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Combined;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated, FrameArtifactRole.Combined };

    public int MaximumInputCount => Options.WindowSize;

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceArtifact = context.GetDependencyArtifacts()
            .SingleOrDefault(artifact => AcceptedInputRoles.Contains(artifact.Role));
        if (sourceArtifact is null && !context.HasDeclaredDependencies)
        {
            sourceArtifact = context.Artifacts?.Raw;
        }
        if (!Options.Enabled || sourceArtifact is null ||
            sourceArtifact.Frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceProduct = context.GetProcessingProduct(sourceArtifact.ArtifactId);
        var current = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config, sourceArtifact, sourceProduct?.Variant ?? "source", context.AcquisitionTiming,
            context.ReconstructionDescriptor, sourceProduct) with
        {
            RecipeIdentitySha256 = sourceProduct?.Recipe.IdentitySha256 ??
                CameraAgentRecipeExecutionAdapter.CreateArtifact(
                    context.Config, sourceArtifact, "source", context.AcquisitionTiming,
                    context.ReconstructionDescriptor, sourceProduct).RecipeIdentitySha256,
            Payload = sourceArtifact.Frame.PixelData.ToArray()
        };
        var durableHistory = context.GetHistoricalInputs();
        IEnumerable<ProcessingArtifact> priorInputs = durableHistory.Count > 0 ? durableHistory : _window;
        var compatibleHistory = priorInputs
            .Where(source => source.ArtifactId != current.ArtifactId)
            .Reverse()
            .TakeWhile(source => IsCompatible(source, current))
            .Reverse();
        var candidateWindow = compatibleHistory
            .Append(current)
            .TakeLast(Options.WindowSize)
            .ToList();

        var outcome = await adapter.ExecuteAsync(context, CreateRequest(candidateWindow), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }

        var product = outcome.Products[0];
        _window.Clear();
        if (durableHistory.Count == 0)
        {
            var selectedIds = product.SourceArtifactIds.ToHashSet();
            foreach (var source in candidateWindow.Where(source => selectedIds.Contains(source.ArtifactId)))
            {
                _window.Enqueue(source);
            }
        }

        var stackMetadata = sourceArtifact.Frame.Metadata.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(sourceArtifact.Frame.Metadata.Extra, StringComparer.Ordinal);
        stackMetadata["stackCount"] = product.SourceArtifactIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        stackMetadata["totalIntegrationMilliseconds"] = product.TotalIntegration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        var combinedFrame = CameraAgentRecipeExecutionAdapter.CreateFrame(product, sourceArtifact.Frame, "RollingCombination");
        var artifact = context.AddDerivative(FrameArtifactRole.Combined,
            combinedFrame with { Metadata = combinedFrame.Metadata with { Extra = stackMetadata } },
            $"rolling-mean-v1-n{product.SourceArtifactIds.Count}",
            product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);

        ProcessingExecutionRequest CreateRequest(IReadOnlyList<ProcessingArtifact> inputs) => new(
            BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new RollingMeanOptions(
                Options.WindowSize,
                Options.MaximumIntegrationMilliseconds,
                Options.MaximumAgeMilliseconds)),
            sourceArtifact.Role switch
            {
                FrameArtifactRole.Raw => ProcessingInputSelector.Raw(current.Variant),
                FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(current.Variant),
                FrameArtifactRole.Combined => ProcessingInputSelector.Combined(current.Variant),
                _ => throw new InvalidOperationException("Rolling input role is invalid.")
            },
            inputs,
            Options.OutputVariant);

        static bool IsCompatible(ProcessingArtifact candidate, ProcessingArtifact current) =>
            candidate.Role == current.Role &&
            string.Equals(candidate.Variant, current.Variant, StringComparison.Ordinal) &&
            candidate.Layout == current.Layout &&
            candidate.Compatibility == current.Compatibility;
    }
}

public sealed class RollingCombinationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 100)]
    public int WindowSize { get; init; } = 5;

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumIntegrationMilliseconds { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumAgeMilliseconds { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "rolling-mean";
}
