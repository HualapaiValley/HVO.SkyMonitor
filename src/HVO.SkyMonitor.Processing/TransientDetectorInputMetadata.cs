using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Restores a V1 detector mapping from retained facts without reading or converting image pixels.</summary>
public static class TransientDetectorInputMetadata
{
    /// <summary>
    /// Restores only the two supported V1 conversions, and accepts the mapping only when the entire reconstructed
    /// descriptor reproduces the retained detector identity. Dimensions alone never establish a transform.
    /// </summary>
    public static TransientDetectorInputDescriptorV1? Restore(
        TransientSourceEvidenceReferenceV1 source,
        FrameLayoutDescriptor sourceLayout,
        TransientLinearLevelsV1 levels,
        ProcessingCompatibilityIdentity compatibility,
        string saturationMaskChecksumSha256,
        string expectedInputIdentitySha256)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceLayout);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(compatibility);
        if (!sourceLayout.Validate().IsValid ||
            sourceLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            sourceLayout.BlackLevel != levels.BlackLevel || sourceLayout.WhiteLevel != levels.WhiteLevel ||
            sourceLayout.PixelFormat == CameraPixelFormat.BayerRggb16 &&
                ((sourceLayout.Width & 1) != 0 || (sourceLayout.Height & 1) != 0))
        {
            return null;
        }

        var color = sourceLayout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var width = color ? sourceLayout.Width / 2 : sourceLayout.Width;
        var height = color ? sourceLayout.Height / 2 : sourceLayout.Height;
        var stride = color ? checked(width * 2) : sourceLayout.StrideBytes;
        var layout = new FrameLayoutDescriptor(
            width, height, stride, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
            levels.BlackLevel, levels.WhiteLevel, checked((long)stride * height));
        var descriptor = new TransientDetectorInputDescriptorV1(
            TransientDetectorInputDescriptorV1.CurrentSchemaVersion,
            expectedInputIdentitySha256,
            TransientContractJson.NormalizeSourceEvidence(source),
            color ? TransientDetectorRepresentation.Rggb16CellAverage : TransientDetectorRepresentation.Mono16,
            layout, levels, compatibility,
            new ProcessingAlgorithmIdentity("linear16-detector-input", color
                ? Linear16DetectorInputConverter.Rggb16AlgorithmVersion
                : Linear16DetectorInputConverter.Mono16AlgorithmVersion),
            new TransientDetectorTransformV1(Linear16DetectorInputConverter.TransformVersion,
                color ? 0.5 : 1, color ? 0.5 : 1, 0, 0),
            saturationMaskChecksumSha256);
        return TransientContractJson.Validate(descriptor).IsValid ? descriptor : null;
    }
}
