using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CalibrationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    CalibrationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter,
    SyntheticCalibrationReferenceStore? syntheticReferences = null,
    SqliteCalibrationLibraryStore? calibrationLibrary = null,
    CalibrationLibraryProcessingInputLoader? libraryInputLoader = null,
    RawCaptureIngress? centeredIngress = null) : ConfigurableCaptureProcessingStep<CalibrationProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => Options.UsesReferences
        ? BuiltInProcessingRecipes.ReferenceCalibration
        : BuiltInProcessingRecipes.LinearNormalization;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;

    public string OutputVariant => Options.OutputVariant;

    public string? OutputMediaType => "application/x-hvo-linear-frame";

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Options.Enabled)
        {
            return;
        }

        var raw = context.Artifacts?.Raw;
        if (raw is null)
        {
            return;
        }

        if (context.Config.CenteredExposure is not null &&
            !CenteredLinearQualification.TryResolveLevels(context.ReconstructionDescriptor?.Layout, out _, out _, out var unsupported))
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(unsupported!, nameof(Options.Strategy)));
            return;
        }

        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config, raw, "source", context.AcquisitionTiming, context.ReconstructionDescriptor);
        ProcessingOutcome outcome;
        if (Options.UsesSyntheticReferences)
        {
            if (context.ReconstructionDescriptor is null)
            {
                outcome = ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.InvalidCalibrationProfile,
                    nameof(context.ReconstructionDescriptor));
            }
            else
            {
                try
                {
                    if (!MatchesSyntheticCalibrationModel(
                            context.ReconstructionDescriptor,
                            Options.SyntheticCalibration!))
                    {
                        outcome = ProcessingOutcome.TerminalFailure(
                            ProcessingReasonCodes.CalibrationReferenceConditionsMismatch,
                            nameof(Options.SyntheticCalibration));
                        context.AddProcessingOutcome(outcome);
                        return;
                    }
                    var referenceStore = syntheticReferences ?? throw new InvalidOperationException(
                        "Synthetic calibration reference storage is unavailable.");
                    var bundle = await referenceStore.GetOrCreateAsync(
                        context.ReconstructionDescriptor,
                        Options.SyntheticCalibration!,
                        cancellationToken).ConfigureAwait(false);
                    var inputs = new List<ProcessingArtifact> { input };
                    inputs.AddRange(CalibrationReferenceKinds.All.Select(kind => bundle.References[kind]));
                    outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
                        BuiltInProcessingRecipes.ReferenceCalibration,
                        JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
                        ProcessingInputSelector.Raw("source"),
                        inputs,
                        Options.OutputVariant,
                        AuxiliaryInputs: bundle.AuxiliaryInputs,
                        InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or
                                                   UnauthorizedAccessException or CalibrationLibraryStoreConflictException)
                {
                    outcome = ProcessingOutcome.TerminalFailure(
                        ProcessingReasonCodes.CalibrationReferenceChecksumMismatch,
                        nameof(Options.SyntheticCalibration));
                }
            }
        }
        else if (Options.UsesCalibrationLibrary)
        {
            if (context.ReconstructionDescriptor is null)
            {
                outcome = ProcessingOutcome.TerminalFailure(
                    CalibrationLibraryReasonCodes.IncompatibleIdentity,
                    nameof(context.ReconstructionDescriptor));
            }
            else
            {
                var store = calibrationLibrary ?? throw new InvalidOperationException(
                    "Calibration library storage is unavailable.");
                var binding = context.RawCapture?.Manifest.CenteredExposure;
                var frozen = binding is null ? null : await (centeredIngress ??
                    throw new InvalidOperationException("Centered calibration requires frozen input custody."))
                    .ReadCenteredRunAsync(binding, cancellationToken).ConfigureAwait(false);
                var selection = frozen is null
                    ? await store.SelectAsync(context.ReconstructionDescriptor, cancellationToken).ConfigureAwait(false)
                    : await store.SelectFrozenAsync(frozen.Calibration, context.ReconstructionDescriptor,
                        cancellationToken).ConfigureAwait(false);
                if (!selection.IsSelected)
                {
                    outcome = selection.ReasonCode is CalibrationLibraryReasonCodes.Missing or
                        CalibrationLibraryReasonCodes.Inactive
                        ? ProcessingOutcome.Skipped(selection.ReasonCode, nameof(Options.Strategy))
                        : ProcessingOutcome.TerminalFailure(selection.ReasonCode, nameof(Options.Strategy));
                }
                else
                {
                    try
                    {
                        var loader = libraryInputLoader ?? throw new InvalidOperationException(
                            "Calibration library input loading is unavailable.");
                        var selectedBundle = selection.Bundle!;
                        var libraryInputs = await loader.LoadAsync(
                            selectedBundle, cancellationToken).ConfigureAwait(false);
                        var confirmed = frozen is null
                            ? await store.SelectAsync(context.ReconstructionDescriptor, cancellationToken).ConfigureAwait(false)
                            : selection;
                        if (!confirmed.IsSelected || confirmed.StateVersion != selection.StateVersion ||
                            !string.Equals(
                                confirmed.Bundle!.BundleIdentitySha256,
                                selectedBundle.BundleIdentitySha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            outcome = ProcessingOutcome.TerminalFailure(
                                CalibrationLibraryReasonCodes.Inactive,
                                nameof(Options.Strategy));
                            context.AddProcessingOutcome(outcome);
                            return;
                        }
                        if (frozen?.Calibration is { } pinned)
                            context.RecordCanonicalInput("centered-calibration-bundle", "calibration-library-bundle-v1",
                                pinned.BundleIdentitySha256);
                        var inputs = new List<ProcessingArtifact> { input };
                        inputs.AddRange(CalibrationReferenceKinds.All.Select(kind => libraryInputs.References[kind]));
                        var selectionJson = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
                            JsonSerializer.SerializeToElement(new
                            {
                                schemaVersion = "calibration-library-selection-v1",
                                bundleIdentitySha256 = confirmed.Bundle.BundleIdentitySha256,
                                stateVersion = confirmed.StateVersion
                            })));
                        var auxiliary = libraryInputs.AuxiliaryInputs.ToList();
                        auxiliary.Add(new ProcessingAuxiliaryInput(
                            "calibration-library-selection",
                            ProcessingAuxiliaryInputKind.CanonicalJson,
                            SchemaVersion: "calibration-library-selection-v1",
                            IdentitySha256: PayloadChecksum.ComputeSha256(selectionJson),
                            Payload: selectionJson));
                        outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
                            BuiltInProcessingRecipes.ReferenceCalibration,
                            JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
                            ProcessingInputSelector.Raw("source"),
                            inputs,
                            Options.OutputVariant,
                            AuxiliaryInputs: auxiliary,
                            InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        outcome = ProcessingOutcome.TerminalFailure(
                            CalibrationLibraryReasonCodes.Corrupt,
                            nameof(Options.Strategy));
                    }
                }
            }
        }
        else
        {
            // No-correction keeps every sample. An explicitly declared native level on shifted/scaled
            // codes becomes its stored-space level in the working artifact; the raw descriptor is unchanged.
            if (context.RawCapture?.Manifest.CenteredExposure is not null && input.Layout is { } layout)
            {
                var storedLayout = CenteredNormalizationLayout.CreateWorkingLayout(layout);
                if (storedLayout != layout && CenteredLinearQualification.TryResolveLevels(layout, out _, out _, out _))
                {
                    context.RecordCanonicalInput("centered-normalization-levels", "centered-stored-levels-v1",
                        CaptureContractJson.ComputeCanonicalJsonSha256(new { Declared = layout, Working = storedLayout }));
                    input = input with { Layout = storedLayout };
                }
            }
            outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.LinearNormalization,
                JsonSerializer.SerializeToElement(new LinearNormalizationOptions(Options.Strategy)),
                ProcessingInputSelector.Raw("source"),
                [input],
                Options.OutputVariant,
                InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
        }
        context.AddProcessingOutcome(outcome);
        if (outcome.Status == ProcessingOutcomeStatus.Produced)
        {
            var product = outcome.Products[0];
            var artifact = context.AddDerivative(
                FrameArtifactRole.Calibrated,
                CameraAgentRecipeExecutionAdapter.CreateFrame(product, raw.Frame, "Calibration"),
                product.Recipe.Descriptor.ImplementationVersion,
                product.SourceArtifactIds,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
        }
    }

    internal static bool MatchesSyntheticCalibrationModel(
        ReconstructionDescriptor descriptor,
        SyntheticCalibrationModelV1 model)
        => string.Equals(
               descriptor.Profiles.Calibration.Name,
               "synthetic-calibration-model",
               StringComparison.Ordinal) &&
            string.Equals(
                descriptor.Profiles.Calibration.Version,
                model.SchemaVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                descriptor.Profiles.Calibration.Sha256,
               SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model),
               StringComparison.OrdinalIgnoreCase);
}

public sealed class CalibrationProcessingStepOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Range(1, 10)]
    public int CalibrationPasses { get; init; } = 1;

    [Range(1, 600)]
    public int MaxCalibrationSeconds { get; init; } = 30;

    [Required(AllowEmptyStrings = false)]
    public string Strategy { get; init; } = "None";

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "none";

    public SyntheticCalibrationModelV1? SyntheticCalibration { get; init; }

    internal bool UsesSyntheticReferences => string.Equals(Strategy, "SyntheticReferences", StringComparison.Ordinal);

    internal bool UsesCalibrationLibrary => string.Equals(Strategy, "CalibrationLibrary", StringComparison.Ordinal);

    internal bool UsesReferences => UsesSyntheticReferences || UsesCalibrationLibrary;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(Strategy, "None", StringComparison.Ordinal) && !UsesReferences)
        {
            yield return new ValidationResult(
                "Calibration strategy must be None, SyntheticReferences, or CalibrationLibrary.",
                [nameof(Strategy)]);
        }
        if (UsesSyntheticReferences && SyntheticCalibration is null)
        {
            yield return new ValidationResult(
                "SyntheticReferences requires a synthetic calibration model.",
                [nameof(SyntheticCalibration)]);
        }
        if (!UsesSyntheticReferences && SyntheticCalibration is not null)
        {
            yield return new ValidationResult(
                "A synthetic calibration model is only valid with SyntheticReferences.",
                [nameof(SyntheticCalibration)]);
        }
    }
}
