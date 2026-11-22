using System.Numerics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public sealed record CenteredViewPreviewCrop(int X, int Y, int Width, int Height);

public sealed record CenteredViewPreviewOptions(
    int MaximumDimension = 2048, int JpegQuality = JpegImageCodec.DefaultQuality,
    double Gamma = 2.2, CenteredViewPreviewCrop? Crop = null, bool AllowRawFallback = true);

internal sealed record CenteredViewStackPreviewFill(
    ProcessingArtifact Reference, Guid SupportArtifactId, ReadOnlyMemory<byte> Support);

internal static class CenteredViewPreviewEncoder
{
    internal const string Version = "centered-view-preview-v1";

    internal static (ProcessingProduct Product, CenteredViewPreview Preview) Encode(
        IReadOnlyList<ProcessingArtifact> channels, FrameLayoutDescriptor levelSource,
        CenteredViewImageSource source, LinearExposureDisposition? disposition, string? reason,
        CenteredViewPreviewOptions options, CancellationToken token) =>
        Encode(channels, levelSource, source, disposition, reason, options, [], token);

    internal static (ProcessingProduct Product, CenteredViewPreview Preview) Encode(
        IReadOnlyList<ProcessingArtifact> channels, FrameLayoutDescriptor levelSource,
        CenteredViewImageSource source, LinearExposureDisposition? disposition, string? reason,
        CenteredViewPreviewOptions options, IReadOnlyList<Guid> retainedEvidence, CancellationToken token) =>
        Encode(channels, levelSource, source, disposition, reason, options, retainedEvidence, null, token);

    internal static (ProcessingProduct Product, CenteredViewPreview Preview) Encode(
        IReadOnlyList<ProcessingArtifact> channels, FrameLayoutDescriptor levelSource,
        CenteredViewImageSource source, LinearExposureDisposition? disposition, string? reason,
        CenteredViewPreviewOptions options, IReadOnlyList<Guid> retainedEvidence, CenteredViewStackPreviewFill? fill,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumDimension, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumDimension, 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.JpegQuality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.JpegQuality, 100);
        var primary = channels[0];
        var layout = primary.Layout ?? throw new InvalidDataException("Centered preview has no retained input layout.");
        if ((long)layout.Width * layout.Height > LinearBayerReconstruction.MaximumSupportedPixels)
            throw new InvalidDataException("The retained raster exceeds the centered preview pixel bound.");

        var transfer = DisplayLevels(levelSource, options.Gamma, source);
        var phase = DeliveredPhase(layout);
        var color = channels.Count == 3 || layout.PixelFormat is CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24;
        var format = color ? CameraPixelFormat.Rgb24 : CameraPixelFormat.Mono8;
        var pixels = channels.Count == 3
            ? TransferRgb(channels, layout, transfer, token)
            : FixedDisplayTransfer.ApplyWithDeliveredPhase(layout, primary.Payload, transfer, phase, token);
        if (fill is not null)
            FillUnsupportedPixels(pixels, layout, color, transfer, fill, token);
        var crop = options.Crop ?? new CenteredViewPreviewCrop(0, 0, layout.Width, layout.Height);
        var cropped = Crop(pixels, layout.Width, layout.Height, crop, color ? 3 : 1, token);
        var packed = PackedLayout(crop.Width, crop.Height, format);
        var (width, height, resized) = PackedImageDownsampler.DownsampleAtPixelCenters(packed, cropped, options.MaximumDimension);
        token.ThrowIfCancellationRequested();
        var jpeg = color
            ? JpegImageCodec.EncodeRgb24ToJpeg(width, height, resized, quality: options.JpegQuality, cancellationToken: token)
            : JpegImageCodec.EncodeMono8ToJpeg(width, height, resized, quality: options.JpegQuality, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        var transform = new CenteredViewPreviewTransform(layout.Width, layout.Height, crop.X, crop.Y, crop.Width, crop.Height, width, height);
        var recipeOptions = JsonSerializer.SerializeToNode(new
        {
            source,
            disposition,
            reason,
            channels = channels.Count == 3 ? "planar-red-green-blue" : color ? "packed-red-green-blue" : "mono",
            deliveredCfa = phase?.ToString(),
            inputLayout = layout,
            levelSource,
            transfer,
            transform,
            downsampler = PackedImageDownsampler.PixelCenterAlgorithmVersion,
            encoder = JpegImageCodec.AlgorithmVersion,
            options.JpegQuality
        })!.AsObject();
        var lineage = channels.Select(channel => channel.ArtifactId).Concat(retainedEvidence).ToArray();
        if (fill is not null)
        {
            recipeOptions["unsupportedPixels"] = JsonSerializer.SerializeToNode(new
            {
                algorithm = "qualified-reference-display-v1",
                minimumContributors = 2,
                referenceArtifactId = fill.Reference.ArtifactId,
                supportArtifactId = fill.SupportArtifactId,
                deliveredCfa = DeliveredPhase(levelSource)?.ToString()
            });
            lineage = [.. lineage, fill.SupportArtifactId, fill.Reference.ArtifactId];
        }
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(Version, "1.0.0", Version,
            JsonSerializer.SerializeToElement(recipeOptions)));
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, Version, recipe.IdentitySha256, lineage);
        var checksum = ProcessingIdentity.ComputePayloadSha256(jpeg);
        var product = new ProcessingProduct(FrameArtifactRole.Preview, Version, outputIdentity, JpegImageCodec.MediaType,
            null, jpeg, checksum, recipe,
            [new("display-transfer", FixedDisplayTransfer.AlgorithmVersion),
             new("display-resize", PackedImageDownsampler.PixelCenterAlgorithmVersion), new("jpeg", JpegImageCodec.AlgorithmVersion)],
            lineage, primary.Integration, primary.Compatibility);
        var reference = new PresentationProductReference(ProcessingIdentity.CreateArtifactId(outputIdentity), outputIdentity,
            JpegImageCodec.MediaType, new(width, height,
                PresentationProcessingProducts.ComputeLayoutIdentity(PackedLayout(width, height, format)),
                CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(transform))));
        return (product, new CenteredViewPreview(reference, checksum, recipe.Descriptor,
            "Skia JPEG", JpegImageCodec.AlgorithmVersion, transform)
        {
            Source = source,
            LinearDisposition = disposition,
            QualificationReason = reason
        });
    }

    private static void FillUnsupportedPixels(Span<byte> pixels, FrameLayoutDescriptor layout, bool color,
        FixedDisplayTransferOptions transfer, CenteredViewStackPreviewFill fill, CancellationToken token)
    {
        var reference = fill.Reference.Layout;
        var support = fill.Support.Span;
        var components = color ? 3 : 1;
        if (reference is null || fill.Reference.Role != FrameArtifactRole.Calibrated ||
            reference.Width != layout.Width || reference.Height != layout.Height ||
            reference.PixelFormat != (color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16) ||
            support.Length != layout.Width * layout.Height)
            throw new InvalidDataException("The stack support or qualified reference differs from the preview lattice.");

        var referencePixels = FixedDisplayTransfer.ApplyWithDeliveredPhase(reference, fill.Reference.Payload, transfer,
            DeliveredPhase(reference), token).AsSpan();
        for (var index = 0; index < support.Length; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            if (BitOperations.PopCount(support[index]) < 2)
                referencePixels.Slice(index * components, components).CopyTo(pixels.Slice(index * components, components));
        }
    }

    private static FixedDisplayTransferOptions DisplayLevels(FrameLayoutDescriptor layout, double gamma, CenteredViewImageSource source)
    {
        var black = layout.BlackLevel;
        var white = layout.WhiteLevel;
        if (layout.StoredCodeTransform == FrameStoredCodeTransform.OpaqueContainerV1 || black is null || white is null)
        {
            if (source != CenteredViewImageSource.RawFallback)
                throw new InvalidDataException("Qualified preview pixels require known code levels.");
            // A raw view is diagnostic when only the stored container is known.
            return new(0, (1 << layout.ContainerDepthBits) - 1, gamma);
        }
        return new(ToStored(black.Value), ToStored(white.Value), gamma);

        double ToStored(double value) => layout.LevelCodeSpace != FrameLevelCodeSpace.NativeSample ? value :
            layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.LeftShiftedV1 => value * (1 << (layout.ContainerDepthBits - layout.SampleDepthBits)),
                FrameStoredCodeTransform.FullRangeScaledV1 =>
                    Math.Round(value * ((1 << layout.ContainerDepthBits) - 1) / ((1 << layout.SampleDepthBits) - 1)),
                _ => value
            };
    }

    private static BayerPattern? DeliveredPhase(FrameLayoutDescriptor layout)
    {
        if (layout.PixelFormat != CameraPixelFormat.BayerRggb16)
            return null;
        return (layout.Readout?.CfaOriginX, layout.Readout?.CfaOriginY) switch
        {
            (0, 0) => BayerPattern.Rggb,
            (1, 0) => BayerPattern.Grbg,
            (0, 1) => BayerPattern.Gbrg,
            (1, 1) => BayerPattern.Bggr,
            _ => throw new InvalidDataException("A centered color preview requires a retained delivered CFA phase.")
        };
    }

    private static byte[] TransferRgb(IReadOnlyList<ProcessingArtifact> channels, FrameLayoutDescriptor layout,
        FixedDisplayTransferOptions transfer, CancellationToken token)
    {
        if (layout.PixelFormat != CameraPixelFormat.Mono16 || channels.Any(channel => channel.Layout != layout))
            throw new InvalidDataException("The registered RGB channels must have identical delivered linear layouts.");
        var rgb = new byte[checked(layout.Width * layout.Height * 3)];
        for (var channel = 0; channel < channels.Count; channel++)
        {
            var values = FixedDisplayTransfer.ApplyWithDeliveredPhase(layout, channels[channel].Payload, transfer, null, token);
            for (var i = 0; i < values.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                rgb[i * 3 + channel] = values[i];
            }
        }
        return rgb;
    }

    private static FrameLayoutDescriptor PackedLayout(int width, int height, CameraPixelFormat format)
    {
        var stride = width * ImageLayout.BytesPerPixel(format);
        return new(width, height, stride, format, FrameByteOrder.NotApplicable, 8, 8,
            FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, (long)stride * height);
    }

    private static byte[] Crop(byte[] pixels, int width, int height, CenteredViewPreviewCrop crop, int channels, CancellationToken token)
    {
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height)
            throw new ArgumentOutOfRangeException(nameof(crop));
        if (crop.Width == width && crop.Height == height)
            return pixels;
        var cropped = new byte[checked(crop.Width * crop.Height * channels)];
        var bytesPerRow = crop.Width * channels;
        for (var row = 0; row < crop.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            pixels.AsSpan(((row + crop.Y) * width + crop.X) * channels, bytesPerRow)
                .CopyTo(cropped.AsSpan(row * bytesPerRow));
        }
        return cropped;
    }
}
