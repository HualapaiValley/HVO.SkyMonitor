using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Disposition codes written into measured source evidence.</summary>
public static class ExposureMeasurementSourceReasonCodes
{
    public const string LinearInputUnavailable = "linear-input-unavailable";
    public const string UnsupportedLinearLayout = "unsupported-linear-layout";
    public const string IncompatibleLinearOrientation = "incompatible-linear-orientation";
    public const string UnqualifiedCfaPhase = "unqualified-cfa-phase";
    public const string CandidateBudgetExceeded = "candidate-budget-exceeded";
    public const string InsufficientBackground = "insufficient-background";
    public const string AllCandidatesRejected = "all-candidates-rejected";
    public const string NoCandidates = "no-candidates";
}

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
    internal const string TrailedSource = "trailed-source";

    // Interpolated CFA luminance shares raw samples within a 3-by-3 footprint. Nine disjoint
    // pixel groups bound the combined local noise scale to three times the independent sum.
    private const double BayerNoiseCorrelationFactor = 3;
    private static readonly double TrailDivisionPixels = new StellarMeasurementOptions().MinimumTrailLengthPixels;
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
    {
        var (input, bayerCoreTrail) = ExposureMeasurementProducts.ReadParameters(options);
        return ExposureMeasurementProducts.SerializeParameters(input, bayerCoreTrail);
    }

    internal static ExposureMeasurementInputV1 Input(ProcessingRecipeIdentity recipe)
        => ExposureMeasurementProducts.ReadParameters(recipe.Descriptor.Options.GetProperty("parameters")).Input;

    public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity, CancellationToken cancellationToken)
    {
        var (input, bayerCoreTrail) = ExposureMeasurementProducts.ReadParameters(identity.Descriptor.Options.GetProperty("parameters"));
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
            input.ApertureProjection, bayerCoreTrail, cancellationToken);
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
        if (linear is not
            {
                Role: FrameArtifactRole.Calibrated, MediaType: "application/x-hvo-linear-frame",
                RecipeDescriptor: { } producer, SourceArtifactIds: { Count: > 0 } lineage
            } ||
            lineage[0] != raw.ArtifactId || !Matches(linear, input.Linear!) ||
            !ExposureMeasurementProducts.Same(ProcessingIdentity.CreateRecipeIdentity(producer).IdentitySha256, linear.RecipeIdentitySha256) ||
            !ExposureMeasurementProducts.Same(ProcessingIdentity.CreateOutputIdentity(linear.Role, linear.Variant,
                linear.RecipeIdentitySha256, lineage), input.Linear!.OutputIdentitySha256))
            return false;

        return input.Calibration switch
        {
            ExposureCalibrationDisposition.NormalizationOnly =>
                producer is
                {
                    Name: BuiltInProcessingRecipes.LinearNormalization, SemanticVersion: "1.0.0",
                    ImplementationVersion: "linear-normalization-none-v1"
                } &&
                lineage.Count == 1 && linear.Layout == (input.Exposure is null
                    ? ProcessingRecipeSupport.CreatePackedLayout(input.Raw.Layout)
                    : CenteredNormalizationLayout.CreateExpectedPackedLayout(input.Raw.Layout)),
            ExposureCalibrationDisposition.ReferenceCalibrated =>
                producer is
                {
                    Name: BuiltInProcessingRecipes.ReferenceCalibration, SemanticVersion: "1.0.0",
                    ImplementationVersion: "reference-calibration-linear16-v2"
                } &&
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
        ProjectionContext? aperture, bool bayerCoreTrail,
        CancellationToken cancellationToken)
    {
        if (sensorClipping.StoredWhite is not { } sensorWhite)
            return Unavailable(sensorClipping.Reason!);
        if (linear?.Layout is not { } layout)
            return Unavailable(ExposureMeasurementSourceReasonCodes.LinearInputUnavailable);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.Width < 16 || layout.Height < 16 || (long)layout.Width * layout.Height > StellarDetector.MaximumSupportedPixels)
            return Unavailable(ExposureMeasurementSourceReasonCodes.UnsupportedLinearLayout);
        if (outputClipping?.StoredWhite is not { } outputWhite)
            return Unavailable(outputClipping?.Reason ?? ExposureMeasurementSourceReasonCodes.LinearInputUnavailable);
        if (!string.Equals(raw.Compatibility.Orientation, linear.Compatibility.Orientation, StringComparison.Ordinal))
            return Unavailable(ExposureMeasurementSourceReasonCodes.IncompatibleLinearOrientation);

        var cfa = layout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var pattern = cfa ? LocalPattern(layout) : null;
        if (cfa && pattern is null)
            return Unavailable(ExposureMeasurementSourceReasonCodes.UnqualifiedCfaPhase);

        var (samples, clipped, valid) = Prepare(raw, linear, sensorWhite, outputWhite, pattern, aperture, cancellationToken);
        var options = MeasuredStellarAssociationRecipe.MeasurementOptions(layout.PixelFormat);
        var measured = StellarSourceMeasurer.Measure(samples.Span, valid.Span, clipped,
            layout.Width, layout.Height, options, cancellationToken);
        var (status, reason) = measured switch
        {
            { Status: StellarMeasurementStatus.CandidateBudgetExceeded } =>
                (ExposureMeasurementStatus.Unsuitable, ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded),
            { AvailableBackgroundTileCount: 0 } =>
                (ExposureMeasurementStatus.Unsuitable, ExposureMeasurementSourceReasonCodes.InsufficientBackground),
            { Detections.Count: > 0 } => (ExposureMeasurementStatus.Usable, (string?)null),
            { CandidateCount: > 0 } =>
                (ExposureMeasurementStatus.Unsuitable, ExposureMeasurementSourceReasonCodes.AllCandidatesRejected),
            _ => (ExposureMeasurementStatus.Empty, ExposureMeasurementSourceReasonCodes.NoCandidates)
        };
        var stars = new ExposureSourcesV1(status, reason, measured.CandidateCount, measured.AvailableBackgroundTileCount,
            measured.SettingsIdentitySha256.ToUpperInvariant(), pattern, measured.Detections, measured.Exclusions);
        return (stars, MeasureFocus(stars, samples.Span, valid.Span, clipped, layout, cfa, bayerCoreTrail, cancellationToken));
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
        ReadOnlySpan<bool> valid, ReadOnlySpan<bool> clipped, FrameLayoutDescriptor layout, bool cfa, bool bayerCoreTrail,
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
            var rejection = FocusDetectionRejection(detection.Conditions);
            var checkCore = bayerCoreTrail && rejection == TrailedSource;
            if (rejection is not null && !checkCore)
            {
                results.Add(new(detection.Index, rejection, null));
                continue;
            }
            var x0 = (int)detection.Pixel.X - windowRadius;
            var y0 = (int)detection.Pixel.Y - windowRadius;
            if (x0 < 0 || y0 < 0 || x0 + diameter > layout.Width || y0 + diameter > layout.Height)
            {
                results.Add(new(detection.Index, rejection ?? FocusStarReasonCodes.ApertureTruncated, null));
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

            var upper = checkCore
                ? BayerCoreTrailUpperBound(windowPixels, windowValid, windowClipped, diameter, x0, y0, detection)
                : null;
            if (checkCore && (upper is null || !IsRoundBayerCore(upper.Value)))
            {
                results.Add(new(detection.Index, TrailedSource, null));
                continue;
            }

            var focus = FocusStarMeasurer.Measure(windowPixels, windowValid, windowClipped,
                diameter, diameter, detection.Pixel, x0, y0, FocusOptions, cancellationToken);
            if (focus.HalfFluxDiameterPixels is not > 0 || !focus.HasMeasurement)
            {
                results.Add(new(detection.Index, rejection ?? focus.ReasonCode, null));
                continue;
            }
            var center = focus.Centroid!.Value;
            var dx = center.X - detection.Pixel.X;
            var dy = center.Y - detection.Pixel.Y;
            if (dx * dx + dy * dy > 1)
            {
                results.Add(new(detection.Index, rejection ?? "focus-centroid-mismatch", null));
                continue;
            }

            widths.Add(focus.HalfFluxDiameterPixels.Value);
            results.Add(new(detection.Index, FocusStarReasonCodes.Valid, focus.HalfFluxDiameterPixels.Value)
            {
                BayerCoreTrailUpperPixels = upper
            });
        }

        var omitted = stars.Detections.Count - count;
        var (status, reason) = FocusDisposition(stars, widths.Count, omitted);
        return new(status, reason, widths.Count, widths.Count == 0 ? null : widths.Average(),
            ExposureMeasurementProducts.Median(widths), omitted, FocusSettings, results);
    }

    internal static bool IsRoundBayerCore(double upper)
        => double.IsFinite(upper) && upper >= 0 && upper < TrailDivisionPixels;

    private static double? BayerCoreTrailUpperBound(ReadOnlySpan<double> pixels, ReadOnlySpan<bool> valid,
        ReadOnlySpan<bool> clipped, int width, int originX, int originY, StellarDetection detection)
    {
        // The existing minimum focus aperture bounds this independent shape check. Squared signal
        // weights the star's core above noisy wings. Subtract the local noise variance so the
        // squared background has zero expected weight.
        var radius = FocusOptions.MinimumApertureRadiusPixels;
        var cx = detection.Pixel.X - originX;
        var cy = detection.Pixel.Y - originY;
        var centerX = (int)cx;
        var centerY = (int)cy;
        var extent = (int)Math.Ceiling(radius);
        var noise = detection.NoiseSigma * detection.NoiseSigma;
        double mass = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        for (var y = centerY - extent; y <= centerY + extent; y++)
        {
            for (var x = centerX - extent; x <= centerX + extent; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                var index = y * width + x;
                if (!valid[index] || clipped[index]) return null;
                var excess = pixels[index] - detection.Background;
                var weight = excess * excess - noise;
                mass += weight;
                sx += weight * dx;
                sy += weight * dy;
                sxx += weight * dx * dx;
                sxy += weight * dx * dy;
                syy += weight * dy * dy;
            }
        }
        if (!(mass > 0)) return null;
        var mx = sx / mass;
        var my = sy / mass;
        var delta = (sxx - syy) / mass - mx * mx + my * my;
        var cross = 2 * (sxy / mass - mx * my);
        var quadrupole = Math.Sqrt(delta * delta + cross * cross);
        var trace = (sxx + syy) / mass - mx * mx - my * my;
        if (!(trace > quadrupole)) return null;

        double uncertainty = 0;
        for (var y = centerY - extent; y <= centerY + extent; y++)
        {
            for (var x = centerX - extent; x <= centerX + extent; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                var excess = pixels[y * width + x] - detection.Background;
                dx -= mx;
                dy -= my;
                var q = dx * dx - dy * dy - delta;
                var u = 2 * dx * dy - cross;
                var weightVariance = 4 * Math.Max(0, excess * excess - noise) * noise + 2 * noise * noise;
                uncertainty += weightVariance * (q * q + u * u);
            }
        }
        // For a Gaussian optical core, squaring halves its covariance. A uniform trail's
        // variance is length squared / 12. Keep a flagged source out of HFD when the
        // resulting equivalent trail plus the correlated noise allowance reaches the division.
        return Math.Sqrt(24 * (quadrupole + BayerNoiseCorrelationFactor * Math.Sqrt(uncertainty) / mass));
    }

    internal static string? FocusDetectionRejection(StellarSourceConditions conditions)
        => conditions.HasFlag(StellarSourceConditions.Saturated) ? FocusStarReasonCodes.Saturated
            : conditions.HasFlag(StellarSourceConditions.Trailed) ? TrailedSource : null;

    internal static string? FocusUnavailableReason(ExposureSourcesV1 sources, FrameLayoutDescriptor? layout)
    {
        if (sources.Status == ExposureMeasurementStatus.Unavailable ||
            sources is
            {
                Status: ExposureMeasurementStatus.Unsuitable,
                Reason: ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded or ExposureMeasurementSourceReasonCodes.InsufficientBackground
            })
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
