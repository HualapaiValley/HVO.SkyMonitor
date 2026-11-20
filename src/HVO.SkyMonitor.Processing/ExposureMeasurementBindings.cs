using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Binds measurements to the admission owner's immutable inputs and exact publication slot.</summary>
public static class ExposureMeasurementBindings
{
    public static ExposureMeasurementInputV1 FromAdmitted(
        LinearExposureLookup settled,
        ProjectionContext? aperture = null,
        double? nativePixelPitchMicrometers = null)
    {
        ArgumentNullException.ThrowIfNull(settled);
        var exposure = settled.Exposure;
        if (exposure is null || settled.Revision is null or <= 0 || !exposure.Raw.Validate().IsValid ||
            exposure.Raw.Artifact.Role != FrameArtifactRole.Raw ||
            exposure.Key.CaptureId != exposure.Raw.Capture.CaptureId ||
            exposure.Key.Camera.AgentId != exposure.Raw.Capture.AgentId ||
            exposure.Geometry.Layout != exposure.Raw.Layout ||
            !Same(exposure.Geometry.RigIdentitySha256, exposure.Raw.Profiles.Rig.Sha256))
            throw Invalid("raw or geometry");

        var raw = ExposureMeasurementSourceV1.FromDescriptor(exposure.Raw);
        var disposition = settled.Disposition switch
        {
            LinearExposureDisposition.Calibrated => ExposureCalibrationDisposition.ReferenceCalibrated,
            LinearExposureDisposition.QualifiedNormalizationOnly => ExposureCalibrationDisposition.NormalizationOnly,
            LinearExposureDisposition.Unsupported => ExposureCalibrationDisposition.Incompatible,
            LinearExposureDisposition.Unavailable or LinearExposureDisposition.Expired => ExposureCalibrationDisposition.Missing,
            LinearExposureDisposition.Failed => ExposureCalibrationDisposition.Failed,
            _ => throw Invalid("nonterminal step-three result")
        };
        var usable = disposition is ExposureCalibrationDisposition.ReferenceCalibrated or ExposureCalibrationDisposition.NormalizationOnly;
        var linear = usable ? ResolveLinear(settled, raw) : null;
        var reason = settled.Reason ?? settled.Provenance?.Reason;
        if (!usable && string.IsNullOrWhiteSpace(reason))
            throw Invalid("missing terminal reason");
        return new(raw, linear, disposition, reason, exposure.Geometry.RigIdentitySha256, nativePixelPitchMicrometers,
            exposure.Geometry.Orientation, aperture, exposure.Key, exposure.Geometry.OpticalCalibrationVersion,
            exposure.Geometry.PixelScaleArcseconds, settled.Disposition, settled.Revision);
    }

    /// <summary>Creates a processing request from an exact step-three lookup and its borrowed payload, when qualified.</summary>
    public static ProcessingExecutionRequest Request(LinearExposureLookup settled,
        ProcessingArtifact raw, LinearExposureInputV1? borrowed,
        ProjectionContext? aperture = null, double? nativePixelPitchMicrometers = null)
    {
        var input = FromAdmitted(settled, aperture, nativePixelPitchMicrometers);
        if ((input.Linear is null) != (borrowed is null) ||
            borrowed is not null && (borrowed.Exposure.Key != settled.Exposure.Key ||
                borrowed.Provenance.LinearArtifactId != input.Linear!.ArtifactId ||
                !Same(borrowed.Provenance.LinearDescriptorSha256, input.Linear.DescriptorSha256) ||
                !Same(borrowed.Provenance.LinearPayloadSha256, input.Linear.PayloadSha256) ||
                !Same(borrowed.Provenance.OutputIdentitySha256, input.Linear.OutputIdentitySha256)))
            throw Invalid("borrowed linear product");
        return ExposureMeasurementProducts.Request(input, raw, borrowed?.Linear);
    }

    /// <summary>Reads a committed live measurement without opening raw or linear pixel payloads.</summary>
    /// <remarks>
    /// ReadLinearAsync resolves the immutable admission-owned live step-three settlement for this exposure.
    /// The recorded revision and live execution must agree. A recomputation that produces its own linear
    /// product supplies its exact separately retained settlement to the typed reader.
    /// Supply the aperture and pixel pitch from the capture's pinned rig evidence.
    /// </remarks>
    public static async ValueTask<ExposureMeasurementV1> ReadCommittedAsync(
        IExposureProductReader reader,
        ExposureProductRef exact,
        ProcessingArtifact retained,
        CancellationToken cancellationToken,
        ProjectionContext? aperture = null,
        double? nativePixelPitchMicrometers = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(retained);
        if (exact.Slot is null || exact.Slot != Slot(exact.Slot.ExecutionId))
            throw Invalid("measurement reference");

        return await ReadCommittedCoreAsync(reader, exact.Exposure, exact.Slot, retained, exact,
            aperture, nativePixelPitchMicrometers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the fixed measurement slot from the owning execution and validates its retained payload.</summary>
    public static ValueTask<ExposureMeasurementV1> ReadCommittedAsync(
        IExposureProductReader reader,
        ExposureKey exposure,
        Guid executionId,
        ProcessingArtifact retained,
        CancellationToken cancellationToken,
        ProjectionContext? aperture = null,
        double? nativePixelPitchMicrometers = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(exposure);
        ArgumentNullException.ThrowIfNull(retained);
        return ReadCommittedCoreAsync(reader, exposure, Slot(executionId), retained, null,
            aperture, nativePixelPitchMicrometers, cancellationToken);
    }

    private static ExposureProductSlot Slot(Guid executionId) => new(executionId,
        ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);

    private static async ValueTask<ExposureMeasurementV1> ReadCommittedCoreAsync(IExposureProductReader reader,
        ExposureKey exposure, ExposureProductSlot slot, ProcessingArtifact retained, ExposureProductRef? exact,
        ProjectionContext? aperture, double? nativePixelPitchMicrometers, CancellationToken cancellationToken)
    {
        if (slot.ExecutionId == Guid.Empty)
            throw Invalid("measurement reference");
        var committed = await reader.ReadProductAsync(exposure, slot, cancellationToken).ConfigureAwait(false);
        if (committed is not { Availability: ExposureProductAvailability.Available, Revision: > 0 } ||
            committed.Product is not { } product || product.Exposure != exposure || product.Slot != slot ||
            committed.Exposure != exposure || committed.Slot != slot || exact is not null && product != exact)
            throw Invalid("measurement is unavailable or differs from the requested slot");
        if (product.ArtifactId != retained.ArtifactId ||
            !Same(product.ContentIdentitySha256, retained.ContentIdentitySha256) ||
            !Same(product.PayloadSha256, retained.ContentIdentitySha256) ||
            !Same(product.OutputIdentitySha256, ProcessingIdentity.CreateOutputIdentity(retained.Role,
                retained.Variant, retained.RecipeIdentitySha256, retained.SourceArtifactIds ?? [])))
            throw Invalid("measurement reference");

        var linear = await reader.ReadLinearAsync(exposure, cancellationToken).ConfigureAwait(false);
        if (linear is null || linear.Exposure.Key != exposure ||
            linear.Product is { } inputProduct && inputProduct.Slot.ExecutionId != slot.ExecutionId)
            throw Invalid("admitted input is unavailable");
        return ExposureMeasurementProducts.Read(retained, FromAdmitted(linear, aperture, nativePixelPitchMicrometers));
    }

    private static ExposureMeasurementSourceV1 ResolveLinear(
        LinearExposureLookup settled, ExposureMeasurementSourceV1 raw)
    {
        if (settled.Provenance is not { } provenance || settled.Product is not { } product ||
            settled.Revision is null or <= 0 || product.Exposure != settled.Exposure.Key ||
            provenance.Disposition != settled.Disposition || provenance.RawArtifactId != raw.ArtifactId ||
            !Same(provenance.RawDescriptorSha256, raw.DescriptorSha256) || !Same(provenance.RawPayloadSha256, raw.PayloadSha256) ||
            provenance.LinearArtifactId != product.ArtifactId ||
            !Same(provenance.LinearPayloadSha256, product.PayloadSha256) ||
            !Same(provenance.OutputIdentitySha256, product.OutputIdentitySha256))
            throw Invalid("linear provenance");

        var calibrated = settled.Disposition == LinearExposureDisposition.Calibrated;
        var expectedRecipe = calibrated ? BuiltInProcessingRecipes.ReferenceCalibration : BuiltInProcessingRecipes.LinearNormalization;
        if (provenance.Recipe.Descriptor.Name != expectedRecipe ||
            !Same(ProcessingIdentity.CreateRecipeIdentity(provenance.Recipe.Descriptor).IdentitySha256, provenance.Recipe.IdentitySha256) ||
            provenance.CalibrationArtifactIds.Length != (calibrated ? 4 : 0) ||
            provenance.CalibrationArtifactIds.Distinct().Count() != provenance.CalibrationArtifactIds.Length)
            throw Invalid("linear producer");

        var layout = calibrated
            ? CalibrationMasterBuilder.CreateNormalizedLayout(raw.Layout)
            : ProcessingRecipeSupport.CreatePackedLayout(raw.Layout);
        if (!ThresholdMatches(provenance.RawWhiteThreshold, raw.ArtifactId, raw.Layout) ||
            !ThresholdMatches(provenance.LinearWhiteThreshold, product.ArtifactId, layout))
            throw Invalid("linear clipping thresholds");
        return new(raw.CaptureId, product.ArtifactId, provenance.LinearDescriptorSha256, product.PayloadSha256,
            provenance.Recipe.IdentitySha256, product.OutputIdentitySha256, layout);
    }

    private static bool ThresholdMatches(ExposureWhiteThreshold? threshold, Guid artifactId, FrameLayoutDescriptor layout)
        => threshold is not null && threshold.ArtifactId == artifactId &&
            ExposureSampleCodes.Clipping(layout).StoredWhite is { } white && threshold.StoredWhiteLevel == white;

    private static bool Same(string? left, string? right) => ExposureMeasurementProducts.Same(left, right);

    private static InvalidDataException Invalid(string field) =>
        new($"Invalid exposure measurement binding ({field}).");
}
