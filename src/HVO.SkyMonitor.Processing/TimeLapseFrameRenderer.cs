using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>The accepted display recipe. A prepared frame is independent of its later video hold duration.</summary>
public sealed record TimeLapseFrameOptions(
    int StackCount = 3,
    bool CardinalDirections = true,
    bool CornerMetadata = true,
    bool ImageCircle = true,
    int JpegQuality = 92,
    Mono16DisplayStretchOptions? NightStretch = null,
    FixedDisplayTransferOptions? DayTransfer = null,
    int? MaximumSaturatedMillionths = null);

public sealed record TimeLapseRenderedFrame(
    ReadOnlyMemory<byte> Jpeg,
    string RenderingIdentitySha256,
    string RecipeIdentitySha256,
    IReadOnlyList<Guid> StackSourceIds,
    IReadOnlyList<string> StackPayloadSha256,
    TimeSpan TotalIntegration,
    IReadOnlyList<PresentationLayerPayloadV1> Layers);

/// <summary>
/// Host-neutral, bounded rendering of one selected source and at most two earlier compatible captures. Uses the
/// delivered unaligned arithmetic mean, stretch/demosaic, typed presentation compositor and JPEG encoder.
/// Hosts restore and verify sources; this class owns no files, processes, catalogs or publication state.
/// </summary>
public static class TimeLapseFrameRenderer
{
    public const string Version = "hvo-timelapse-stack-display-v1";
    public const string OverlayVersion = "hvo-timelapse-proportional-overlays-v1";
    public const int MaximumColorSourcePixels = LinearBayerReconstruction.MaximumSupportedPixels;

    public static string RecipeIdentity(TimeLapseFrameOptions options)
    {
        Validate(options);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Version, OverlayVersion, options,
            mean = Linear16ArithmeticMean.AlgorithmVersion,
            stretch = Mono16DisplayStretch.AlgorithmVersion,
            demosaic = BayerRggb16Demosaicer.AlgorithmVersion,
            dayTransfer = FixedDisplayTransfer.AlgorithmVersion,
            quality = ImageStatisticsCalculator.AlgorithmVersion,
            compositor = PresentationLayerCompositor.AlgorithmVersion
        });
    }

    /// <summary>Optional quality admission on the verified linear source, before timeline planning or stacking.</summary>
    public static bool AcceptsQuality(ProcessingArtifact source, TimeLapseFrameOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Validate(options);
        if (options.MaximumSaturatedMillionths is not { } threshold) return true;
        var layout = source.Layout ?? throw new ArgumentException("Quality admission requires a linear image layout.", nameof(source));
        if (!layout.Validate().IsValid || layout.Width > 4096 || layout.Height > 4096 || source.Payload.Length != layout.ByteLength ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) || layout.ByteOrder != FrameByteOrder.LittleEndian)
            throw new ArgumentException("Unsupported quality source.", nameof(source));
        var statistics = ImageStatisticsCalculator.Calculate(layout.Width, layout.Height, layout.StrideBytes, layout.PixelFormat,
            source.Payload, layout.WhiteLevel is { } white ? checked((ushort)Math.Round(white)) : null, cancellationToken);
        return statistics.SaturatedCount * 1_000_000L <= (long)layout.Width * layout.Height * threshold;
    }

    public static TimeLapseRenderedFrame Render(IReadOnlyList<ProcessingArtifact> sources, CameraRigConfig rig,
        TimeLapseFrameOptions options, bool daytime, int outputWidth, int outputHeight,
        IReadOnlyList<PresentationCompositorLayer>? additionalLayers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rig);
        Validate(options);
        if (sources.Count < 1 || sources.Count > options.StackCount || outputWidth is < 2 or > 4096 || outputHeight is < 2 or > 4096 ||
            outputWidth % 2 != 0 || outputHeight % 2 != 0 || additionalLayers?.Count > 12)
            throw new ArgumentException("A time-lapse frame requires one to three bounded sources and even output dimensions.");
        var latest = sources[^1];
        var layout = latest.Layout ?? throw new ArgumentException("Missing source layout.", nameof(sources));
        var rigIdentity = RigProjectionContextFactory.CreateProfileHashSha256(rig);
        if (layout.Width is < 2 or > 4096 || layout.Height is < 2 or > 4096 ||
            (layout.PixelFormat == CameraPixelFormat.BayerRggb16 && (long)layout.Width * layout.Height > MaximumColorSourcePixels) ||
            outputWidth > layout.Width || outputHeight > layout.Height ||
            !string.Equals(latest.Compatibility.Rig, rigIdentity, StringComparison.OrdinalIgnoreCase) ||
            sources.Any(source => source.Layout != layout || source.Compatibility != latest.Compatibility ||
                source.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated) ||
                source.ObservationStartedUtc is null || source.Integration < TimeSpan.Zero ||
                source.Payload.Length != layout.ByteLength) || !layout.Validate().IsValid ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.ByteOrder != FrameByteOrder.LittleEndian ||
            sources.Select(static source => source.ArtifactId).Distinct().Count() != sources.Count ||
            sources.Zip(sources.Skip(1)).Any(pair => pair.Second.ObservationStartedUtc <= pair.First.ObservationStartedUtc ||
                pair.Second.ObservationStartedUtc - pair.First.ObservationStartedUtc > TimeSpan.FromMinutes(1)))
            throw new ArgumentException("Time-lapse stack sources are incompatible, unavailable or outside the supported linear layout.", nameof(sources));
        var combined = Linear16ArithmeticMean.Compute(sources.Select(source => new Linear16Frame(layout.Width,
            layout.Height, layout.StrideBytes, layout.PixelFormat, source.Payload)).ToArray(), cancellationToken);
        var integration = TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks));
        var packed = layout with { StrideBytes = combined.StrideBytes, ByteLength = combined.PixelData.Length };
        var color = layout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var pixels = daytime
            ? FixedDisplayTransfer.Apply(packed, combined.PixelData, options.DayTransfer ?? new(), cancellationToken)
            : color
                ? BayerRggb16Demosaicer.DemosaicToRgb24(layout.Width, layout.Height, combined.PixelData, cancellationToken,
                    stretchOptions: options.NightStretch)
                : Mono16DisplayStretch.Apply(layout.Width, layout.Height, combined.PixelData, cancellationToken,
                    options: options.NightStretch);
        var imageLayout = new ImageLayout(layout.Width, layout.Height, color ? CameraPixelFormat.Rgb24 : CameraPixelFormat.Mono8,
            layout.Width * (color ? 3 : 1));
        var checksums = sources.Select(source => ProcessingIdentity.ComputePayloadSha256(source.Payload)).ToArray();
        var recipeIdentity = RecipeIdentity(options);
        var baseIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            recipeIdentity, rigIdentity, sourceIds = sources.Select(static source => source.ArtifactId), checksums,
            latest.ObservationStartedUtc, daytime, outputWidth, outputHeight,
            selectedLayers = additionalLayers?.Select(layer => new
            {
                layer.Payload.ContentIdentitySha256, layer.Enabled, layer.BlendMode, layer.OpacityMillionths
            })
        });
        var facts = new PresentationMetadataFactsV1(baseIdentity,
            [rig.Sensor.Name, latest.ObservationStartedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)],
            ["EXPOSURE", FormattableString.Invariant($"{latest.Integration.TotalSeconds:0.###} s | Gain {latest.Conditions?.Gain ?? 0:0.##}")],
            ["ROLLING STACK", FormattableString.Invariant($"{sources.Count} frames | {integration.TotalSeconds:0.###} s")],
            ["TIMELAPSE", FormattableString.Invariant($"Video {outputWidth} x {outputHeight}"),
                FormattableString.Invariant($"Source {layout.Width} x {layout.Height}")]);
        var layers = CreateLayers(RigProjectionContextFactory.Create(rig), facts, layout.Width, layout.Height, options);
        var selected = layers.Select(payload => new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000))
            .Concat(additionalLayers ?? []).ToArray();
        var composed = PresentationLayerCompositor.CompositeDisplay(imageLayout, pixels, selected, cancellationToken);
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            baseIdentity, layers = layers.Select(static layer => layer.ContentIdentitySha256)
        });
        return new(JpegImageCodec.EncodeToJpeg(composed.Layout, composed.Pixels, options.JpegQuality, cancellationToken), identity, recipeIdentity,
            sources.Select(static source => source.ArtifactId).ToArray(), checksums, integration, selected.Select(static layer => layer.Payload).ToArray());
    }

    /// <summary>Largest exact-aspect even size within the requested bound, with a pixel-rounded fallback for coprime sensors.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maximumDimension)
    {
        if (width is < 2 or > 4096 || height is < 2 or > 4096 || maximumDimension is < 128 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        var a = width;
        var b = height;
        while (b != 0) (a, b) = (b, a % b);
        var factor = Math.Min(a, maximumDimension / Math.Max(width / a, height / a));
        if ((width / a * factor) % 2 != 0 || (height / a * factor) % 2 != 0) factor--;
        if (factor > 0) return (width / a * factor, height / a * factor);
        var scale = Math.Min(1d, (double)maximumDimension / Math.Max(width, height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }

    public static byte[] GapImage(int width, int height)
    {
        if (width is < 2 or > 4096 || height is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new { Version, gap = true, width, height });
        var label = new PresentationTextBlockV1(PresentationTextAnchor.TopLeft, default, ["NO IMAGE", "Missing capture interval"],
            PresentationFont.FrameScale(width, height), 8, 4, new(220, 220, 220));
        var layer = PresentationLayerPayloadJson.Create(identity, width, height, textBlocks: [label]);
        var layout = new ImageLayout(width, height, CameraPixelFormat.Mono8, width);
        return JpegImageCodec.EncodeToJpeg(layout, PresentationLayerCompositor.Composite(layout, new byte[width * height],
            [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]), 92);
    }

    internal static IReadOnlyList<PresentationLayerPayloadV1> CreateLayers(ProjectionContext projection,
        PresentationMetadataFactsV1 facts, int width, int height, TimeLapseFrameOptions options)
    {
        var unit = Math.Min(width, height) / 630d;
        var result = new List<PresentationLayerPayloadV1>();
        if ((options.CardinalDirections || options.ImageCircle) && RigProjectionContextFactory.CreateAnnotationLandmarks(projection) is { } landmarks)
        {
            if (options.ImageCircle)
                result.Add(PresentationLayerPayloadJson.Create(facts.SourceIdentitySha256, width, height,
                    ellipses: [new(landmarks.Center, landmarks.ImageCircleRadius, landmarks.ImageCircleRadius,
                        new(116, 209, 255), new(0, 0, 450_000), Math.Clamp((int)Math.Round(2000 * unit), 1, 8000))]));
            if (options.CardinalDirections)
            {
                var color = new PresentationColor(195, 236, 255);
                var appearance = new PresentationTextAppearanceV3(new(PresentationFontFaceV3.MonoBold,
                    Math.Clamp((int)Math.Round(18000 * unit), 1000, 112000), Math.Clamp((int)Math.Round(720 * unit), 0, 8000),
                    color, new(3, 8, 14), 950000, Math.Clamp((int)Math.Round(4000 * unit), 0, 16000)));
                var plate = new PresentationBackplateV2(new(2, 8, 14), 840000, new(116, 209, 255),
                    Math.Clamp((int)Math.Round(5 * unit), 0, 32), 0,
                    new(Math.Clamp((int)Math.Round(1700 * unit), 0, 8000), 380000,
                        MinimumWidthMilliPixels: Math.Clamp((int)Math.Round(38000 * unit), 1, 256000),
                        MinimumHeightMilliPixels: Math.Clamp((int)Math.Round(25000 * unit), 1, 256000),
                        CornerRadiusMilliPixels: Math.Clamp((int)Math.Round(5000 * unit), 0, 32000)));
                using var font = PresentationFont.Create(appearance.Body);
                var blocks = new[] { ("N", landmarks.North), ("E", landmarks.East), ("S", landmarks.South), ("W", landmarks.West) }
                    .Select(pair =>
                    {
                        var glyph = PresentationFont.LineBounds(font, pair.Item1, 0, 0);
                        var block = new PresentationTextBlockV1(PresentationTextAnchor.Point,
                            new(pair.Item2.X - glyph.Width / 2, pair.Item2.Y - glyph.Height / 2), [pair.Item1], 1, 0, 0,
                            color, plate, appearance);
                        var bounds = PresentationFont.BackplateBounds(block, width, height, font);
                        var edge = Math.Max(2, unit);
                        var dx = bounds.Left < edge ? edge - bounds.Left : bounds.Right > width - edge ? width - edge - bounds.Right : 0;
                        var dy = bounds.Top < edge ? edge - bounds.Top : bounds.Bottom > height - edge ? height - edge - bounds.Bottom : 0;
                        return block with { Point = new(block.Point.X + dx, block.Point.Y + dy) };
                    }).ToArray();
                result.Add(PresentationLayerPayloadJson.Create(facts.SourceIdentitySha256, width, height, textBlocks: blocks));
            }
        }
        else if (options.CardinalDirections || options.ImageCircle)
            throw new ArgumentException("Selected time-lapse geometry overlays are unavailable for this rig.", nameof(projection));
        if (options.CornerMetadata)
        {
            var metadata = PresentationLayerProducers.FromMetadataFacts(facts, width, height);
            result.Add(PresentationLayerPayloadJson.Create(facts.SourceIdentitySha256, width, height,
                textBlocks: metadata.TextBlocks.Select(block => block with { Inset = block.Backplate!.Padding + 1 }).ToArray()));
        }
        return result;
    }

    private static void Validate(TimeLapseFrameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StackCount is not (1 or 3) || options.JpegQuality is < 1 or > 100 ||
            options.MaximumSaturatedMillionths is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        (options.NightStretch ?? new()).Validate();
        (options.DayTransfer ?? new()).Validate();
    }
}
