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
        if (raw.Role != FrameArtifactRole.Raw || linear.Role != FrameArtifactRole.Calibrated || pin.LinearLayout != linear.Layout ||
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
            ProcessingArtifact.ResolveObservationEndedUtc(descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure) != pin.ExposureEndedUtc ||
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
                MatchesNormalizationLayout(rawLayout, linearLayout),
            BuiltInProcessingRecipes.ReferenceCalibration => parents.Count == 5 &&
                provenance.Disposition == LinearExposureDisposition.Calibrated &&
                IsSha256(provenance.CalibrationBundleIdentitySha256) &&
                provenance.CalibrationPayloadHashes.Length == 4 && provenance.CalibrationPayloadHashes.All(IsSha256) &&
                linearLayout == CalibrationMasterBuilder.CreateNormalizedLayout(rawLayout),
            _ => false
        };
    }

    internal static bool MatchesNormalizationLayout(FrameLayoutDescriptor source, FrameLayoutDescriptor normalized)
    {
        var packed = ProcessingRecipeSupport.CreatePackedLayout(source);
        if (packed == normalized)
        {
            return true;
        }
        // The centered admission path can relabel scaled/shifted native levels in
        // stored units without changing a single sample. No other layout change is allowed.
        if (source.LevelCodeSpace != FrameLevelCodeSpace.NativeSample ||
            source.BlackLevel is not { } black ||
            !FrameWhiteThreshold.TryResolveStored(source, out var white, out _))
        {
            return false;
        }
        var scale = source.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.LeftShiftedV1 => 1 << (source.ContainerDepthBits - source.SampleDepthBits),
            FrameStoredCodeTransform.FullRangeScaledV1 => ushort.MaxValue / (double)((1 << source.SampleDepthBits) - 1),
            _ => 0
        };
        return scale > 0 && normalized == (packed with
        {
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
            BlackLevel = black * scale,
            WhiteLevel = white
        });
    }

    /// <summary>One source's scratch, reused after the accumulator has consumed that source.</summary>
    internal sealed class Decoder(FrameLayoutDescriptor layout)
    {
        private readonly double[] _values = new double[layout.Width * layout.Height];
        private readonly bool[] _valid = new bool[layout.Width * layout.Height];
        private readonly ColorPlanes? _color = Pattern(layout) is { } pattern ? new(layout.Width * layout.Height, pattern) : null;

        internal LinearRegistrationImage Decode(ProcessingArtifact raw, ProcessingArtifact linear,
            ProcessingArtifact mask, ProjectionContext? aperture, CancellationToken cancellationToken)
        {
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
                    _values[index] = linearCode;
                    _valid[index] = validCode == 1 && rawCode < sourceWhite && linearCode < linearWhite &&
                        (aperture?.ContainsSample(x + .5, y + .5) ?? true);
                }
            }
            if (_color is not { } color)
            {
                return new(layout.Width, layout.Height, _valid, _values);
            }
            LinearBayerReconstruction.ReconstructInto(_values, _valid, layout.Width, layout.Height, color.Pattern,
                color.Red, color.Green, color.Blue, color.Mask, cancellationToken);
            return new(layout.Width, layout.Height, color.Mask, color.Red, color.Green, color.Blue);
        }

        private sealed class ColorPlanes(int size, BayerPattern pattern)
        {
            internal BayerPattern Pattern { get; } = pattern;
            internal double[] Red { get; } = new double[size];
            internal double[] Green { get; } = new double[size];
            internal double[] Blue { get; } = new double[size];
            internal bool[] Mask { get; } = new bool[size];
        }
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
