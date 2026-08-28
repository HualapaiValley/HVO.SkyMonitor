using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Runs the canonical assessment against the raw capture and one explicitly declared dependency.</summary>
internal sealed class CloudAssessmentCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    CloudAssessmentProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter,
    CameraAgentClearReferenceLoader clearReferenceLoader,
    CameraAgentCloudEnvironment? cloudEnvironment = null)
    : ConfigurableCaptureProcessingStep<CloudAssessmentProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled { get; } = RegisterReference(options, clearReferenceLoader);

    public string RecipeName => BuiltInProcessingRecipes.CloudAssessment;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string? OutputSchemaVersion => CloudAssessmentV1.CurrentSchemaVersion;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var currentArtifact = context.GetDependencyArtifacts()
            .SingleOrDefault(artifact => AcceptedInputRoles.Contains(artifact.Role));
        if (currentArtifact is null && !context.HasDeclaredDependencies)
        {
            currentArtifact = context.Artifacts?.Raw;
        }
        if (!Options.Enabled || currentArtifact is null)
        {
            return;
        }
        var currentProduct = context.GetProcessingProduct(currentArtifact.ArtifactId);
        var current = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            currentArtifact,
            currentProduct?.Variant ?? "source",
            context.AcquisitionTiming,
            context.ReconstructionDescriptor,
            currentProduct);
        if (currentProduct is not null)
        {
            current = current with { RecipeIdentitySha256 = currentProduct.Recipe.IdentitySha256 };
        }

        var inputs = new List<ProcessingArtifact> { current };
        var auxiliary = new List<ProcessingAuxiliaryInput>();
        if (!string.IsNullOrWhiteSpace(Options.ClearReferenceManifestPath))
        {
            try
            {
                var clear = await clearReferenceLoader.LoadAsync(
                    Options.ClearReferenceManifestPath,
                    cancellationToken).ConfigureAwait(false);
                inputs.Add(clear);
                auxiliary.Add(new ProcessingAuxiliaryInput(
                    "clear-reference",
                    ProcessingAuxiliaryInputKind.Artifact,
                    CreateSelector(clear),
                    ArtifactId: clear.ArtifactId));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }
        var environment = cloudEnvironment is null
            ? CameraAgentCloudEnvironment.CreateMissingInput(context)
            : await cloudEnvironment.CreateInputAsync(context, cancellationToken).ConfigureAwait(false);
        if (environment is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(
                ProcessingReasonCodes.EnvironmentAssociationPending));
            return;
        }
        auxiliary.Add(environment);
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.CloudAssessment,
            JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
                Options.GridColumns,
                Options.GridRows,
                Options.TransmissionThresholdMillionths,
                Options.MinimumReferenceSignal,
                Options.MinimumSamplesPerTile,
                Options.MaximumSaturatedFractionMillionths,
                Options.IncludeMask)),
            CameraAgentRecipeExecutionAdapter.CreateSelector(currentArtifact, currentProduct, current.Variant),
            inputs,
            Options.OutputVariant,
            AuxiliaryInputs: auxiliary,
            InputArtifactId: current.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }

    private static ProcessingInputSelector CreateSelector(ProcessingArtifact artifact)
        => artifact.Role switch
        {
            FrameArtifactRole.Raw => ProcessingInputSelector.Raw(artifact.Variant),
            FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(artifact.Variant),
            FrameArtifactRole.Combined => ProcessingInputSelector.Combined(artifact.Variant),
            _ => throw new InvalidOperationException("Configured clear-reference role is invalid.")
        };

    private static bool RegisterReference(
        CloudAssessmentProcessingStepOptions configuredOptions,
        CameraAgentClearReferenceLoader loader)
    {
        if (configuredOptions.Enabled)
        {
            loader.RegisterRetentionHold(configuredOptions.ClearReferenceManifestPath);
        }
        return configuredOptions.Enabled;
    }
}

public sealed class CloudAssessmentProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    public string? ClearReferenceManifestPath { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "cloud-assessment-v1";

    [Range(1, 64)]
    public int GridColumns { get; init; } = 16;

    [Range(1, 64)]
    public int GridRows { get; init; } = 12;

    [Range(1, 999_999)]
    public int TransmissionThresholdMillionths { get; init; } = 850_000;

    [Range(0, ushort.MaxValue)]
    public ushort MinimumReferenceSignal { get; init; } = 64;

    [Range(1, int.MaxValue)]
    public int MinimumSamplesPerTile { get; init; } = 16;

    [Range(0, 1_000_000)]
    public int MaximumSaturatedFractionMillionths { get; init; } = 100_000;

    public bool IncludeMask { get; init; } = true;
}

internal sealed class CameraAgentCloudEnvironment(
    EnvironmentalAssociationService associations,
    ILocalEnvironmentalObservationStore observations,
    IOptions<CameraAgentHostOptions> options)
{
    internal async ValueTask<ProcessingAuxiliaryInput?> CreateInputAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RawCapture?.Manifest.Descriptor is not { } descriptor)
        {
            return CreateMissingInput(context);
        }
        if (!options.Value.EnvironmentalAcquisition.Enabled)
        {
            return CreateMissingInput(context);
        }
        var capture = descriptor.Capture;
        var timing = descriptor.Timing;
        var exposureFromUtc = timing.ExposureStartedUtc.ToUniversalTime();
        var exposureThroughUtc = timing.ExposureEndedUtc.ToUniversalTime();
        if (exposureThroughUtc <= exposureFromUtc)
        {
            exposureThroughUtc = exposureFromUtc.AddTicks(1);
        }
        var completed = await associations.ReadCompletedAsync(
            capture.CaptureId,
            capture.CaptureSequence,
            exposureFromUtc,
            exposureThroughUtc,
            capture.RigId,
            [EnvironmentalObservationKind.RainState],
            cancellationToken).ConfigureAwait(false);
        if (completed is null)
        {
            return null;
        }
        var association = completed.Single();
        LocalEnvironmentalObservationRecord? selected = null;
        if (association.SelectedRecordId is { } selectedRecordId)
        {
            selected = await observations.ReadLocalDetailAsync(
                options.Value.RawIngressRoot, selectedRecordId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The selected environmental association evidence is unavailable.");
            if (selected.Fact.Value.Kind != EnvironmentalObservationKind.RainState ||
                selected.Fact.Value.BooleanValue is null)
            {
                throw new InvalidDataException("The selected precipitation association evidence is invalid.");
            }
        }
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            context.Submission.CycleEvidence?.SolarRegime,
            association.Status switch
            {
                LocalEnvironmentalAssociationStatus.Fresh => EnvironmentalObservationMatchStatus.Fresh,
                LocalEnvironmentalAssociationStatus.Stale => EnvironmentalObservationMatchStatus.Stale,
                LocalEnvironmentalAssociationStatus.Missing => EnvironmentalObservationMatchStatus.Missing,
                LocalEnvironmentalAssociationStatus.Contradictory => EnvironmentalObservationMatchStatus.Contradictory,
                _ => throw new InvalidDataException("The environmental association status is invalid.")
            },
            selected?.Fact.ObservationId,
            selected?.ContentSha256,
            selected?.Fact.Value.BooleanValue ?? false,
            association.AssociationIdentitySha256);
        return CreateInput(environment);
    }

    internal static ProcessingAuxiliaryInput CreateMissingInput(CaptureProcessingContext context)
    {
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            context.Submission.CycleEvidence?.SolarRegime,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
        return CreateInput(environment);
    }

    private static ProcessingAuxiliaryInput CreateInput(CloudAssessmentEnvironmentV1 environment)
    {
        var element = CaptureContractJson.SerializeToElement(environment);
        var payload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        return new ProcessingAuxiliaryInput(
            "environment",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(payload),
            Payload: payload);
    }
}
