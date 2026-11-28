using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ExposureMeasurementCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ExposureMeasurementStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter,
    RawCaptureIngress ingress,
    CenteredExposureProductReader inputs,
    CenteredExposureInputCustody custody)
    : ConfigurableCaptureProcessingStep<ExposureMeasurementStepOptions>(metadata, options),
        ICaptureProcessingGraphStep, ICenteredRawPrimaryCaptureProcessingStep
{
    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.ExposureMeasurement;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => ExposureMeasurementProducts.Variant;
    public string OutputSchemaVersion => ExposureMeasurementProducts.SchemaVersion;
    public string OutputMediaType => ExposureMeasurementProducts.MediaType;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated };

    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements { get; } =
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Calibrated },
            new HashSet<string>(StringComparer.Ordinal)
            {
                BuiltInProcessingRecipes.LinearNormalization,
                BuiltInProcessingRecipes.ReferenceCalibration
            }, Required: false)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var manifest = context.RawCapture?.Manifest ?? throw new InvalidDataException("Exposure measurement requires admitted raw.");
        var key = CaptureExposureMeasurementReader.Key(manifest);
        var execution = context.ProcessingExecution ?? throw new InvalidDataException("Exposure measurement requires a durable execution.");
        var evidence = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The admitted exposure is unavailable.");
        var liveExecution = CaptureExposureMeasurementReader.LiveExecution(key, evidence);
        if (execution.GraphRevisionId != evidence.Run.GraphRevisionId ||
            execution.ExecutionClass == ProcessingGraphExecutionClass.Live && execution.ExecutionId != liveExecution)
            throw new InvalidDataException("The graph execution differs from the admitted exposure.");

        var settled = await inputs.ReadLinearAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The admitted linear disposition is unavailable.");
        if (settled.Disposition is LinearExposureDisposition.Pending or LinearExposureDisposition.Expired)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(
                settled.Disposition == LinearExposureDisposition.Pending ? "measurement.linear-pending" : "measurement.linear-expired"));
            return;
        }

        if (settled.Disposition is not (LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly))
        {
            await ProcessAdmittedAsync(context, settled, null, evidence, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (settled.Product is not { } exact || exact.Slot.ExecutionId != liveExecution ||
            !context.GetDependencyProducts().Any(product => product.OutputIdentitySha256 == exact.OutputIdentitySha256))
            throw new InvalidDataException("The linear graph input differs from the admitted publication.");
        var lease = await custody.OpenLinearAsync(exact, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure("measurement.linear-lease-unavailable"));
            return;
        }
        await using var leaseLifetime = lease.ConfigureAwait(false);
        await ProcessAdmittedAsync(context, settled, lease.Input, evidence, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ProcessAdmittedAsync(CaptureProcessingContext context, LinearExposureLookup settled,
        LinearExposureInputV1? linear, CenteredRawEvidence evidence, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor!;
        await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
            descriptor.Artifact.Variant, context.AcquisitionTiming, descriptor);
        var (aperture, pitch) = CaptureExposureMeasurementReader.Geometry(evidence, settled.Exposure.Geometry.Layout);
        string? retainedOutputIdentity = null;
        if (context.IsReplayExecution)
        {
            var key = settled.Exposure.Key;
            var slot = new ExposureProductSlot(CaptureExposureMeasurementReader.LiveExecution(key, evidence),
                ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
            var retained = await inputs.ReadProductAsync(key, slot, cancellationToken).ConfigureAwait(false);
            retainedOutputIdentity = retained?.Product?.OutputIdentitySha256;
        }
        var request = ExposureMeasurementBindings.Request(settled, raw, linear, aperture, pitch, retainedOutputIdentity);
        var outcome = await adapter.ExecuteAsync(context, request, cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }
}

internal sealed class ExposureMeasurementStepOptions;
