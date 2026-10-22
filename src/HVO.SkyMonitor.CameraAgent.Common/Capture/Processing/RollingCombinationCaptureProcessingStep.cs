using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// Creates a rolling linear 16-bit combined artifact after every compatible capture. With
/// <see cref="RollingCombinationProcessingStepOptions.Recipe"/> set to the mixed-gain recipe it stacks raw frames
/// through a frozen normalization plan instead (#1475); otherwise a gain change breaks the window.
/// </summary>
internal sealed class RollingCombinationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    RollingCombinationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<RollingCombinationProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep, IWindowCaptureProcessingGraphStep
{
    private static readonly IReadOnlySet<FrameArtifactRole> StackRoles =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated, FrameArtifactRole.Combined };

    // The mixed-gain recipe calibrates each raw frame with its own gain's masters, so it accepts raw frames only.
    private static readonly IReadOnlySet<FrameArtifactRole> RawRole = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    private readonly Queue<ProcessingArtifact> _window = new();

    internal int BufferedFrameCount => _window.Count;

    public bool Enabled => Options.Enabled;

    public string RecipeName => MixedGain ? BuiltInProcessingRecipes.MixedGainNormalizedMean : BuiltInProcessingRecipes.RollingMean;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Combined;

    public string OutputVariant => Options.OutputVariant;

    public string? OutputMediaType => "application/x-hvo-linear-frame";

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles => MixedGain ? RawRole : StackRoles;

    public int MaximumInputCount => Options.WindowSize;

    public ProcessingGraphWindowKind WindowKind => Options.WindowKind;

    public int MinimumInputCount => Options.WindowKind == ProcessingGraphWindowKind.Centered
        ? Options.WindowSize
        : 1;

    public IReadOnlyList<int> RequiredPositions => Options.WindowKind == ProcessingGraphWindowKind.Centered
        ? Enumerable.Range(-(Options.WindowSize / 2), Options.WindowSize).ToArray()
        : [];

    private bool MixedGain => string.Equals(Options.Recipe, BuiltInProcessingRecipes.MixedGainNormalizedMean, StringComparison.Ordinal);

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
        // The mixed-gain recipe executes under the node's frozen normalization plan, frozen here on first execution
        // and reused on every retry, with the masters it selects (#1475).
        var normalization = MixedGain
            ? await adapter.PrepareNormalizationAsync(
                context,
                new NormalizationWindowShape(
                    Options.WindowKind,
                    RequiredPositions,
                    MinimumInputCount,
                    Options.MaximumAgeMilliseconds,
                    Options.MaximumIntegrationMilliseconds),
                cancellationToken).ConfigureAwait(false)
            : GainNormalizationPreparation.None;
        if (normalization.Outcome is { } preparationOutcome)
        {
            context.AddProcessingOutcome(preparationOutcome);
            return;
        }
        var durableHistory = context.GetHistoricalInputs();
        IEnumerable<ProcessingArtifact> priorInputs = context.ProcessingExecution is not null
            ? durableHistory
            : durableHistory.Count > 0 ? durableHistory : _window;
        var compatibleHistory = priorInputs
            .Where(source => source.ArtifactId != current.ArtifactId)
            .Reverse()
            .TakeWhile(source => IsCompatible(source, current, normalization.Plan))
            .Reverse();
        var candidateWindow = context.ProcessingExecution is null
            ? compatibleHistory.Append(current).TakeLast(Options.WindowSize).ToList()
            : compatibleHistory.Append(current)
                .OrderBy(static artifact => artifact.CaptureSequence ?? long.MaxValue)
                .Take(Options.WindowSize)
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
        // A mixed-gain product also names the calibration masters it used; only the stacked frames count.
        var windowIds = candidateWindow.Select(static source => source.ArtifactId).ToHashSet();
        var stackCount = product.SourceArtifactIds.Count(windowIds.Contains);
        stackMetadata["stackCount"] = stackCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        stackMetadata["totalIntegrationMilliseconds"] = product.TotalIntegration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        var combinedFrame = CameraAgentRecipeExecutionAdapter.CreateFrame(product, sourceArtifact.Frame, "RollingCombination");
        var artifact = context.AddDerivative(FrameArtifactRole.Combined,
            combinedFrame with { Metadata = combinedFrame.Metadata with { Extra = stackMetadata } },
            MixedGain ? $"mixed-gain-normalized-mean-v1-n{stackCount}" : $"rolling-mean-v1-n{stackCount}",
            product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);

        ProcessingExecutionRequest CreateRequest(IReadOnlyList<ProcessingArtifact> inputs) => MixedGain
            ? new(
                BuiltInProcessingRecipes.MixedGainNormalizedMean,
                JsonSerializer.SerializeToElement(new MixedGainNormalizedMeanOptions()),
                ProcessingInputSelector.Raw(current.Variant),
                [.. inputs, .. normalization.Masters],
                Options.OutputVariant,
                AuxiliaryInputs: normalization.PlanInput is null ? null : [normalization.PlanInput])
            : new(
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
    }

    /// <summary>
    /// The rolling window boundary. Gain is part of the compatibility, so a gain change breaks the window. Only under
    /// a frozen normalization plan (#1475) do raw frames that differ in gain alone compare equal, through
    /// <see cref="GainNormalizationCompatibility.Normalize"/>; a change of any other axis, or of the plan's family,
    /// policy or response, still breaks it.
    /// </summary>
    internal static bool IsCompatible(ProcessingArtifact candidate, ProcessingArtifact current, NormalizationPlanV1? plan) =>
        candidate.Role == current.Role &&
        string.Equals(candidate.Variant, current.Variant, StringComparison.Ordinal) &&
        candidate.Layout == current.Layout &&
        (plan is null
            ? candidate.Compatibility == current.Compatibility
            : candidate.Role == FrameArtifactRole.Raw &&
              GainNormalizationCompatibility.Normalize(candidate.Compatibility, plan) ==
              GainNormalizationCompatibility.Normalize(current.Compatibility, plan));
}

public sealed class RollingCombinationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 100)]
    public int WindowSize { get; init; } = 5;

    public ProcessingGraphWindowKind WindowKind { get; init; } = ProcessingGraphWindowKind.Trailing;

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumIntegrationMilliseconds { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumAgeMilliseconds { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "rolling-mean";

    /// <summary>
    /// Null for the rolling mean, or <c>mixed-gain-normalized-mean</c> to stack raw frames across gain changes through
    /// a frozen normalization plan (#1475). Omitted from serialized options when null.
    /// </summary>
    [AllowedValues(null, BuiltInProcessingRecipes.MixedGainNormalizedMean)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recipe { get; init; }
}
