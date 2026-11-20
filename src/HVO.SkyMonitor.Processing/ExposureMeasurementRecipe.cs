using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

internal static class ExposureSampleCodes
{
    internal static ExposureClippingV1 Clipping(FrameLayoutDescriptor layout)
        => FrameWhiteThreshold.TryResolveStored(layout, out var white, out var reason)
            ? new(white, 0, null)
            : new(null, null, reason);

    internal static ushort Read16(ReadOnlySpan<byte> sample, FrameByteOrder order)
        => order == FrameByteOrder.BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(sample)
            : BinaryPrimitives.ReadUInt16LittleEndian(sample);
}

internal sealed class ExposureMeasurementRecipe : IProcessingRecipe
{
    private const string UnqualifiedSource = "linear-input-unavailable";
    private const string NoSources = "no-candidates";
    private static readonly FocusStarMeasurementOptions FocusOptions = new(
        ApertureRadiusPixels: 12, AnnulusInnerRadiusPixels: 15, AnnulusOuterRadiusPixels: 22,
        MinimumApertureRadiusPixels: 6);
    private static readonly string FocusSettings = FocusStarMeasurer.SettingsIdentity(FocusOptions).ToUpperInvariant();

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.ExposureMeasurement, "1.0.0", ExposureMeasurementProducts.SchemaVersion,
        ProcessingOperationKind.Analyzer);

    internal static IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms(ExposureMeasurementInputV1 input)
    {
        List<ProcessingAlgorithmIdentity> algorithms =
        [
            new("image-statistics", ImageStatisticsCalculator.StoredCodeAlgorithmVersion),
            new("stellar-source-measurer", StellarSourceMeasurer.AlgorithmVersion),
            new("focus-star-measurer", FocusStarMeasurer.AlgorithmVersion)
        ];
        if (input.Linear?.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
            algorithms.Add(new("linear-bayer-reconstruction", LinearBayerReconstruction.AlgorithmVersion));
        return algorithms;
    }

    public JsonElement NormalizeOptions(JsonElement options)
        => ExposureMeasurementProducts.SerializeInput(ExposureMeasurementProducts.ReadInput(options));

    internal static ExposureMeasurementInputV1 Input(ProcessingRecipeIdentity recipe)
        => ExposureMeasurementProducts.ReadInput(recipe.Descriptor.Options.GetProperty("parameters"));

    public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity, CancellationToken cancellationToken)
    {
        var input = Input(identity);
        var sources = request.Inputs;
        if (request.OutputVariant != ExposureMeasurementProducts.Variant || request.Annotation is not null ||
            request.AuxiliaryInputs is { Count: > 0 } ||
            !sources.Select(static source => source.ArtifactId).SequenceEqual(ExposureMeasurementProducts.SourceIds(input)) ||
            ProcessingRecipeSupport.ResolveSingle(request, out _) is not { Role: FrameArtifactRole.Raw } raw ||
            !Matches(raw, input.Raw))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("measurement.raw-binding-mismatch"));

        var linear = input.Linear is null ? null : sources[1];
        if (linear is not null && !MatchesLinear(raw, linear, input))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("measurement.linear-binding-mismatch"));

        var rawBrightness = Brightness(raw, cancellationToken);
        var linearClipping = linear is null ? null : Brightness(linear, cancellationToken).SensorClipping;
        var (stars, hfd) = Measure(raw, linear, rawBrightness.SensorClipping, linearClipping,
            input.ApertureProjection, cancellationToken);
        var result = new ExposureMeasurementV1(ExposureMeasurementProducts.SchemaVersion,
            input, rawBrightness, null, linearClipping, stars, hfd);
        var payload = ExposureMeasurementProducts.Serialize(result);
        var product = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata,
            ExposureMeasurementProducts.Variant, ExposureMeasurementProducts.MediaType, null, payload,
            identity, Algorithms(input), sources, raw.Integration, raw.Compatibility,
            ProcessingProductKind.Metadata, ExposureMeasurementProducts.SchemaVersion,
            ProcessingIdentity.ComputePayloadSha256(payload));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static bool Matches(ProcessingArtifact artifact, ExposureMeasurementSourceV1 source)
        => artifact.ArtifactId == source.ArtifactId && artifact.CaptureId == source.CaptureId &&
           artifact.ProductKind == ProcessingProductKind.PixelData && artifact.Layout == source.Layout &&
           artifact.Payload.Length == source.Layout.ByteLength &&
           ExposureMeasurementProducts.Same(artifact.DescriptorIdentitySha256, source.DescriptorSha256) &&
           ExposureMeasurementProducts.Same(artifact.RecipeIdentitySha256, source.RecipeSha256) &&
           ExposureMeasurementProducts.Same(ProcessingIdentity.ComputePayloadSha256(artifact.Payload), source.PayloadSha256);

    private static bool MatchesLinear(ProcessingArtifact raw, ProcessingArtifact linear, ExposureMeasurementInputV1 input)
    {
        if (linear is not { Role: FrameArtifactRole.Calibrated, MediaType: "application/x-hvo-linear-frame",
                RecipeDescriptor: { } producer, SourceArtifactIds: { Count: > 0 } lineage } ||
            lineage[0] != raw.ArtifactId || !Matches(linear, input.Linear!) ||
            !ExposureMeasurementProducts.Same(ProcessingIdentity.CreateRecipeIdentity(producer).IdentitySha256, linear.RecipeIdentitySha256) ||
            !ExposureMeasurementProducts.Same(ProcessingIdentity.CreateOutputIdentity(linear.Role, linear.Variant,
                linear.RecipeIdentitySha256, lineage), input.Linear!.OutputIdentitySha256))
            return false;

        return input.Calibration switch
        {
            ExposureCalibrationDisposition.NormalizationOnly =>
                producer is { Name: BuiltInProcessingRecipes.LinearNormalization, SemanticVersion: "1.0.0",
                    ImplementationVersion: "linear-normalization-none-v1" } &&
                lineage.Count == 1 && linear.Layout == (input.Exposure is null
                    ? ProcessingRecipeSupport.CreatePackedLayout(input.Raw.Layout)
                    : CenteredNormalizationLayout.CreateExpectedPackedLayout(input.Raw.Layout)),
            ExposureCalibrationDisposition.ReferenceCalibrated =>
                producer is { Name: BuiltInProcessingRecipes.ReferenceCalibration, SemanticVersion: "1.0.0",
                    ImplementationVersion: "reference-calibration-linear16-v2" } &&
                lineage.Count == 5 && linear.Layout == CalibrationMasterBuilder.CreateNormalizedLayout(input.Raw.Layout),
            _ => false
        };
    }

    private static ExposureBrightnessV1 Brightness(ProcessingArtifact artifact, CancellationToken cancellationToken)
    {
        var layout = artifact.Layout!;
        var clipping = ExposureSampleCodes.Clipping(layout);
        var stats = ImageStatisticsCalculator.CalculateStored(
            new ImageLayout(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes),
            artifact.Payload, layout.ByteOrder, clipping.StoredWhite, cancellationToken);
        return new(stats.SampleCount, stats.Minimum, stats.Maximum, stats.Sum, stats.ZeroCount,
            clipping with { Count = clipping.StoredWhite is null ? null : stats.SaturatedCount });
    }

    internal static BayerPattern? LocalPattern(FrameLayoutDescriptor layout)
        => (layout.Readout?.CfaOriginX, layout.Readout?.CfaOriginY) switch
        {
            (0, 0) => BayerPattern.Rggb,
            (1, 0) => BayerPattern.Grbg,
            (0, 1) => BayerPattern.Gbrg,
            (1, 1) => BayerPattern.Bggr,
            _ => null
        };

    private static (ExposureSourcesV1, ExposureHfdV1) Measure(ProcessingArtifact raw,
        ProcessingArtifact? linear, ExposureClippingV1 sensorClipping, ExposureClippingV1? outputClipping,
        ProjectionContext? aperture,
        CancellationToken cancellationToken)
    {
        if (sensorClipping.StoredWhite is not { } sensorWhite)
            return Unavailable(sensorClipping.Reason!);
        if (linear?.Layout is not { } layout)
            return Unavailable(UnqualifiedSource);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.Width < 16 || layout.Height < 16 || (long)layout.Width * layout.Height > StellarDetector.MaximumSupportedPixels)
            return Unavailable("unsupported-linear-layout");
        if (outputClipping?.StoredWhite is not { } outputWhite)
            return Unavailable(outputClipping?.Reason ?? UnqualifiedSource);
        if (!string.Equals(raw.Compatibility.Orientation, linear.Compatibility.Orientation, StringComparison.Ordinal))
            return Unavailable("incompatible-linear-orientation");

        var cfa = layout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var pattern = cfa ? LocalPattern(layout) : null;
        if (cfa && pattern is null)
            return Unavailable("unqualified-cfa-phase");

        var (samples, clipped, valid) = Prepare(raw, linear, sensorWhite, outputWhite, pattern, aperture, cancellationToken);
        var options = MeasuredStellarAssociationRecipe.MeasurementOptions(layout.PixelFormat);
        var measured = StellarSourceMeasurer.Measure(samples.Span, valid.Span, clipped,
            layout.Width, layout.Height, options, cancellationToken);
        var (status, reason) = measured switch
        {
            { Status: StellarMeasurementStatus.CandidateBudgetExceeded } =>
                (ExposureMeasurementStatus.Unsuitable, "candidate-budget-exceeded"),
            { AvailableBackgroundTileCount: 0 } => (ExposureMeasurementStatus.Unsuitable, "insufficient-background"),
            { Detections.Count: > 0 } => (ExposureMeasurementStatus.Usable, (string?)null),
            { CandidateCount: > 0 } => (ExposureMeasurementStatus.Unsuitable, "all-candidates-rejected"),
            _ => (ExposureMeasurementStatus.Empty, (string?)NoSources)
        };
        var stars = new ExposureSourcesV1(status, reason, measured.CandidateCount, measured.AvailableBackgroundTileCount,
            measured.SettingsIdentitySha256.ToUpperInvariant(), pattern, measured.Detections, measured.Exclusions);
        return (stars, MeasureFocus(stars, samples.Span, valid.Span, clipped, layout, cfa, cancellationToken));
    }

    private static (ReadOnlyMemory<double> Samples, bool[] Clipped, ReadOnlyMemory<bool> Valid) Prepare(
        ProcessingArtifact raw, ProcessingArtifact linear, ushort sensorWhite, ushort outputWhite, BayerPattern? pattern,
        ProjectionContext? aperture, CancellationToken cancellationToken)
    {
        var layout = linear.Layout!;
        var rawLayout = raw.Layout!;
        var count = checked(layout.Width * layout.Height);
        var samples = new double[count];
        var mask = new bool[count];
        var valid = new bool[count];
        var rawBytes = raw.Payload.Span;
        var linearBytes = linear.Payload.Span;
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawRow = rawBytes.Slice(y * rawLayout.StrideBytes, layout.Width * 2);
            var linearRow = linearBytes.Slice(y * layout.StrideBytes, layout.Width * 2);
            for (var x = 0; x < layout.Width; x++)
            {
                var linearCode = ExposureSampleCodes.Read16(linearRow[(x * 2)..], layout.ByteOrder);
                var rawCode = ExposureSampleCodes.Read16(rawRow[(x * 2)..], rawLayout.ByteOrder);
                var index = y * layout.Width + x;
                samples[index] = linearCode;
                mask[index] = rawCode >= sensorWhite || linearCode >= outputWhite;
                valid[index] = aperture?.ContainsSample(x + 0.5, y + 0.5) ?? true;
            }
        }
        if (pattern is not { } bayer)
            return (samples, mask, valid);

        var luminance = LinearBayerReconstruction.Reconstruct(samples, valid, layout.Width, layout.Height, bayer, cancellationToken)
            .ToLuminance(cancellationToken);
        return (luminance.Pixels, mask, luminance.ValidMask);
    }

    private static ExposureHfdV1 MeasureFocus(ExposureSourcesV1 stars, ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> valid, ReadOnlySpan<bool> clipped, FrameLayoutDescriptor layout, bool cfa,
        CancellationToken cancellationToken)
    {
        if (FocusUnavailableReason(stars, layout) is { } unavailable)
            return new(ExposureMeasurementStatus.Unavailable, unavailable, null, null, null, 0, null, []);
        if (stars.Detections.Count == 0)
            return new(stars.Status, stars.Reason, 0, null, null, 0, null, []);

        var count = Math.Min(stars.Detections.Count, ExposureMeasurementProducts.MaximumHfdSamples);
        var windowRadius = (int)Math.Ceiling(FocusOptions.AnnulusOuterRadiusPixels) + 3;
        var diameter = 2 * windowRadius + 1;
        var windowPixels = new double[diameter * diameter];
        var windowValid = new bool[windowPixels.Length];
        var windowClipped = new bool[windowPixels.Length];
        var results = new List<ExposureHfdSampleV1>(count);
        var widths = new List<double>(count);
        foreach (var detection in stars.Detections.Take(count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (detection.Conditions.HasFlag(StellarSourceConditions.Saturated))
            {
                results.Add(new(detection.Index, FocusStarReasonCodes.Saturated, null));
                continue;
            }
            var x0 = (int)detection.Pixel.X - windowRadius;
            var y0 = (int)detection.Pixel.Y - windowRadius;
            if (detection.Conditions.HasFlag(StellarSourceConditions.Trailed))
            {
                results.Add(new(detection.Index, "trailed-source", null));
                continue;
            }
            if (x0 < 0 || y0 < 0 || x0 + diameter > layout.Width || y0 + diameter > layout.Height)
            {
                results.Add(new(detection.Index, FocusStarReasonCodes.ApertureTruncated, null));
                continue;
            }
            for (var y = 0; y < diameter; y++)
            {
                var sourceStart = (y0 + y) * layout.Width + x0;
                var start = y * diameter;
                pixels.Slice(sourceStart, diameter).CopyTo(windowPixels.AsSpan(start, diameter));
                valid.Slice(sourceStart, diameter).CopyTo(windowValid.AsSpan(start, diameter));
                for (var x = 0; x < diameter; x++)
                    windowClipped[start + x] = cfa
                        ? CfaNeighborClipped(clipped, x0 + x, y0 + y, layout)
                        : clipped[sourceStart + x];
            }

            var focus = FocusStarMeasurer.Measure(windowPixels, windowValid, windowClipped,
                diameter, diameter, detection.Pixel, x0, y0, FocusOptions, cancellationToken);
            if (focus.HalfFluxDiameterPixels is not > 0 || !focus.HasMeasurement)
            {
                results.Add(new(detection.Index, focus.ReasonCode, null));
                continue;
            }
            var center = focus.Centroid!.Value;
            var dx = center.X - detection.Pixel.X;
            var dy = center.Y - detection.Pixel.Y;
            if (dx * dx + dy * dy > 1)
            {
                results.Add(new(detection.Index, "focus-centroid-mismatch", null));
                continue;
            }

            widths.Add(focus.HalfFluxDiameterPixels.Value);
            results.Add(new(detection.Index, FocusStarReasonCodes.Valid, focus.HalfFluxDiameterPixels.Value));
        }

        var omitted = stars.Detections.Count - count;
        var (status, reason) = FocusDisposition(stars, widths.Count, omitted);
        return new(status, reason, widths.Count, widths.Count == 0 ? null : widths.Average(),
            ExposureMeasurementProducts.Median(widths), omitted, FocusSettings, results);
    }

    internal static string? FocusUnavailableReason(ExposureSourcesV1 sources, FrameLayoutDescriptor? layout)
    {
        if (sources.Status == ExposureMeasurementStatus.Unavailable || sources.Reason is "candidate-budget-exceeded" or "insufficient-background")
            return sources.Reason;
        return layout?.Readout is { } readout && readout.BinX != readout.BinY ? "anisotropic-readout" : null;
    }

    internal static (ExposureMeasurementStatus Status, string? Reason) FocusDisposition(
        ExposureSourcesV1 sources, int valid, int omitted)
        => sources.Detections.Count == 0 ? (sources.Status, sources.Reason)
            : (valid > 0 ? ExposureMeasurementStatus.Usable : ExposureMeasurementStatus.Unsuitable,
                omitted > 0 ? "focus-budget-limited" : valid == 0 ? "no-suitable-focus-samples" : null);

    private static bool CfaNeighborClipped(ReadOnlySpan<bool> clipped, int x, int y, FrameLayoutDescriptor layout)
    {
        for (var row = Math.Max(0, y - 1); row <= Math.Min(layout.Height - 1, y + 1); row++)
        {
            for (var column = Math.Max(0, x - 1); column <= Math.Min(layout.Width - 1, x + 1); column++)
            {
                if (clipped[row * layout.Width + column])
                    return true;
            }
        }
        return false;
    }

    private static (ExposureSourcesV1, ExposureHfdV1) Unavailable(string reason) =>
        (new(ExposureMeasurementStatus.Unavailable, reason, null, null, null, null, [], []),
            new(ExposureMeasurementStatus.Unavailable, reason, null, null, null, 0, null, []));
}
