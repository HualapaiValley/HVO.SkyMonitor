using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

internal static class CenteredStackLinearInput
{
    internal static bool MatchesExposure(CenteredStackExposurePin pin, ProcessingArtifact raw, ProcessingArtifact linear,
        ProcessingArtifact mask, LinearExposureInputV1 qualified)
    {
        var provenance = qualified.Provenance;
        var descriptor = qualified.Exposure.Raw;
        if (raw.Role != FrameArtifactRole.Raw || linear.Role != FrameArtifactRole.Calibrated ||
            qualified.Exposure.Key != pin.Key || qualified.Linear.ArtifactId != linear.ArtifactId ||
            descriptor.Capture.CaptureId != pin.Key.CaptureId || descriptor.Capture.AgentId != pin.Key.Camera.AgentId ||
            !descriptor.Validate().IsValid || descriptor.Layout != raw.Layout || descriptor.Layout != qualified.Exposure.Geometry.Layout ||
            descriptor.Artifact.ArtifactId != raw.ArtifactId || descriptor.Artifact.ChecksumSha256 != pin.Raw.PayloadSha256 ||
            CaptureContractJson.ComputeDescriptorSha256(descriptor) != pin.Raw.DescriptorSha256 ||
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256 != pin.Raw.ProducerSha256 ||
            descriptor.Capture.CaptureSequence != pin.CaptureSequence ||
            descriptor.Controls.EffectiveExposure.Ticks != pin.IntegrationTicks ||
            descriptor.Controls.EffectiveGain != raw.Conditions?.Gain ||
            descriptor.Controls.EffectiveOffset != raw.Conditions?.Offset ||
            descriptor.Timing.ExposureStartedUtc != pin.ExposureStartedUtc ||
            descriptor.Timing.ExposureEndedUtc != raw.ObservationEndedUtc ||
            pin.GeometrySha256 != ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(qualified.Exposure.Geometry)) ||
            pin.LinearProvenanceSha256 != ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(provenance)) ||
            provenance.RawArtifactId != raw.ArtifactId || provenance.RawDescriptorSha256 != pin.Raw.DescriptorSha256 ||
            provenance.RawPayloadSha256 != pin.Raw.PayloadSha256 ||
            provenance.LinearArtifactId != linear.ArtifactId || provenance.LinearDescriptorSha256 != pin.Linear.DescriptorSha256 ||
            provenance.LinearPayloadSha256 != pin.Linear.PayloadSha256 ||
            provenance.Recipe.IdentitySha256 != linear.RecipeIdentitySha256 ||
            raw.DescriptorIdentitySha256 is null || raw.CaptureId != pin.Key.CaptureId || linear.CaptureId != pin.Key.CaptureId ||
            !CenteredStackValidityMask.Matches(mask, pin.ValidityMask) || mask.CaptureId != pin.Key.CaptureId ||
            raw.CaptureSequence != pin.CaptureSequence || linear.CaptureSequence != pin.CaptureSequence ||
            raw.ObservationStartedUtc != pin.ExposureStartedUtc || linear.ObservationStartedUtc != pin.ExposureStartedUtc ||
            raw.ObservationEndedUtc is not { } rawEnd || linear.ObservationEndedUtc is not { } linearEnd ||
            raw.Integration.Ticks != pin.IntegrationTicks || linear.Integration != raw.Integration ||
            rawEnd < pin.ExposureStartedUtc || linearEnd < pin.ExposureStartedUtc ||
            ProcessingArtifact.ResolveObservationEndedUtc(pin.ExposureStartedUtc, rawEnd, raw.Integration) != pin.ExposureEndedUtc ||
            ProcessingArtifact.ResolveObservationEndedUtc(pin.ExposureStartedUtc, linearEnd, linear.Integration) != pin.ExposureEndedUtc ||
            raw.Compatibility.Orientation != linear.Compatibility.Orientation ||
            raw.Compatibility.LocationIdentitySha256 != linear.Compatibility.LocationIdentitySha256 ||
            mask.SourceArtifactIds is not { Count: 2 } maskParents || maskParents[0] != raw.ArtifactId || maskParents[1] != linear.ArtifactId ||
            linear.RecipeDescriptor is not { } recipe || ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256 != linear.RecipeIdentitySha256 ||
            linear.SourceArtifactIds is not { Count: > 0 } parents || parents[0] != raw.ArtifactId ||
            parents.Contains(Guid.Empty) || parents.Distinct().Count() != parents.Count ||
            !SupportedLayout(raw.Layout, raw.Payload.Length) || !SupportedLayout(linear.Layout, linear.Payload.Length))
        {
            return false;
        }

        var rawLayout = raw.Layout!;
        var linearLayout = linear.Layout!;
        if (mask.Payload.Length != rawLayout.Width * rawLayout.Height ||
            provenance.RawWhiteThreshold != new ExposureWhiteThreshold(raw.ArtifactId, StoredWhite(rawLayout)) ||
            provenance.LinearWhiteThreshold != new ExposureWhiteThreshold(linear.ArtifactId, StoredWhite(linearLayout)) ||
            provenance.OutputIdentitySha256 != ProcessingIdentity.CreateOutputIdentity(linear.Role, linear.Variant,
                linear.RecipeIdentitySha256, parents) ||
            linear.ArtifactId != ProcessingIdentity.CreateArtifactId(provenance.OutputIdentitySha256) ||
            !provenance.CalibrationArtifactIds.SequenceEqual(parents.Skip(1)))
        {
            return false;
        }
        return recipe.Name switch
        {
            BuiltInProcessingRecipes.LinearNormalization => parents.Count == 1 &&
                provenance.Disposition == LinearExposureDisposition.QualifiedNormalizationOnly &&
                provenance.CalibrationBundleIdentitySha256 is null && provenance.CalibrationPayloadHashes.IsEmpty &&
                linearLayout == ProcessingRecipeSupport.CreatePackedLayout(rawLayout),
            BuiltInProcessingRecipes.ReferenceCalibration => parents.Count == 5 &&
                provenance.Disposition == LinearExposureDisposition.Calibrated &&
                IsSha256(provenance.CalibrationBundleIdentitySha256) &&
                provenance.CalibrationPayloadHashes.Length == 4 && provenance.CalibrationPayloadHashes.All(IsSha256) &&
                linearLayout == CalibrationMasterBuilder.CreateNormalizedLayout(rawLayout),
            _ => false
        };
    }

    internal static LinearRegistrationImage Decode(ProcessingArtifact raw, ProcessingArtifact linear,
        ProcessingArtifact mask, ProjectionContext? aperture, CancellationToken cancellationToken)
    {
        var layout = linear.Layout!;
        var size = layout.Width * layout.Height;
        var values = new double[size];
        var valid = new bool[size];
        var pattern = Pattern(layout);
        var sourceWhite = StoredWhite(raw.Layout!);
        var linearWhite = StoredWhite(layout);
        for (var y = 0; y < layout.Height; y++)
        {
            var rawRow = raw.Payload.Span.Slice(y * raw.Layout!.StrideBytes, layout.Width * 2);
            var linearRow = linear.Payload.Span.Slice(y * layout.StrideBytes, layout.Width * 2);
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var rawCode = ExposureSampleCodes.Read16(rawRow[(x * 2)..], raw.Layout!.ByteOrder);
                var linearCode = ExposureSampleCodes.Read16(linearRow[(x * 2)..], layout.ByteOrder);
                var validCode = mask.Payload.Span[index];
                if (validCode > 1)
                {
                    throw new ArgumentException("Invalid retained mask sample.", nameof(mask));
                }
                values[index] = linearCode;
                valid[index] = validCode == 1 && rawCode < sourceWhite && linearCode < linearWhite &&
                    (aperture?.ContainsSample(x + .5, y + .5) ?? true);
            }
        }
        if (pattern is null)
        {
            return new(layout.Width, layout.Height, valid, values);
        }
        var rgb = LinearBayerReconstruction.Reconstruct(values, valid, layout.Width, layout.Height, pattern.Value, cancellationToken);
        return new(layout.Width, layout.Height, rgb.ValidMask, rgb.Red, rgb.Green, rgb.Blue);
    }

    private static bool SupportedLayout(FrameLayoutDescriptor? layout, int payloadLength) =>
        layout is
        {
            Readout: not null, ContainerDepthBits: 16, Packing: FrameSamplePacking.ByteAligned,
            ByteOrder: FrameByteOrder.LittleEndian, BlackLevel: not null, WhiteLevel: not null
        } &&
        layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 &&
        layout.Validate().IsValid && layout.ByteLength == payloadLength &&
        layout.StoredCodeTransform != FrameStoredCodeTransform.OpaqueContainerV1 &&
        (long)layout.Width * layout.Height <= LinearRegisteredMean.MaximumPixels &&
        (layout.PixelFormat == CameraPixelFormat.Mono16 || layout.Width >= 3 && layout.Height >= 3 && Pattern(layout) is not null) &&
        StoredWhite(layout) is > 0;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static double StoredWhite(FrameLayoutDescriptor layout) =>
        ExposureSampleCodes.Clipping(layout).StoredWhite is { } white ? white : double.NaN;

    private static BayerPattern? Pattern(FrameLayoutDescriptor layout) =>
        layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? ExposureMeasurementRecipe.LocalPattern(layout) : null;
}
