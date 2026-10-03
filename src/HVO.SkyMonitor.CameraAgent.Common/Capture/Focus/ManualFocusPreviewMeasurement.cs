using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Result of measuring one preview: the sample inputs, the tracked target, and the display images.</summary>
internal sealed record ManualFocusPreviewOutcome(
    FocusStarMeasurement Measurement,
    PixelPoint? Target,
    bool TargetSelected,
    ManualFocusFrameWindow? Window,
    string FrameSha256,
    ManualFocusPreviewImages Images);

/// <summary>
/// Turns one preview frame into a single-star measurement. The target is selected from frame pixels (optionally near an
/// operator or catalog hint) and then tracked: each later exposure is measured in a fixed window around the previous
/// pixel centroid, so the reported centroid is always image-derived and in exact source-frame coordinates.
/// </summary>
internal static class ManualFocusPreviewMeasurement
{
    /// <summary>Half-size of the measurement window: the background annulus plus an eight-pixel tracking margin.</summary>
    public const int MeasurementHalfWindow = 56;
    public const int AutomaticSearchSize = 1024;
    public const double TargetSearchRadiusPixels = 24;
    public const int OverviewMaximumDimension = 640;
    public const int MaximumCatalogStars = 12;
    private const int JpegQuality = 92;

    public static FocusStarMeasurementOptions MeasurementOptions { get; } = new();

    public static ManualFocusPreviewOutcome Measure(
        CameraFrame frame,
        long sequence,
        PixelPoint? trackedTarget,
        PixelPoint? hint,
        CancellationToken cancellationToken,
        MeteringImageCircle? imageCircle = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var options = MeasurementOptions;
        var frameSha256 = Convert.ToHexStringLower(SHA256.HashData(frame.PixelData.Span));
        var target = trackedTarget;
        var selected = false;
        FocusStarMeasurement measurement;
        ManualFocusFrameWindow? window = null;
        FocusStarTarget? selection = null;
        if (target is null)
        {
            selection = SelectTarget(frame, hint, options, cancellationToken, imageCircle);
            if (selection.Position is { } position)
            {
                target = position;
                selected = true;
            }
        }
        if (target is not { } current)
        {
            // Keep the selector's own reason: a clipped or starved sky is not the same fault as a sky without stars.
            var (status, reason) = selection?.ReasonCode switch
            {
                FocusStarReasonCodes.Saturated => (FocusStarStatus.Saturated, FocusStarReasonCodes.Saturated),
                FocusStarReasonCodes.BackgroundUnavailable =>
                    (FocusStarStatus.BackgroundUnavailable, FocusStarReasonCodes.BackgroundUnavailable),
                FocusStarReasonCodes.CrowdedAutomaticTarget =>
                    (FocusStarStatus.NoStar, FocusStarReasonCodes.CrowdedAutomaticTarget),
                _ => (FocusStarStatus.NoStar,
                    hint is null ? FocusStarReasonCodes.NoCandidate : FocusStarReasonCodes.NoCandidateNearSelection)
            };
            measurement = new FocusStarMeasurement(status, reason,
                null, null, null, null, null, null, null, null, 0, 0, FocusStarMeasurer.SettingsIdentity(options));
        }
        else
        {
            window = ManualFocusFrameSampler.ExtractWindow(frame,
                (int)Math.Floor(current.X) - MeasurementHalfWindow,
                (int)Math.Floor(current.Y) - MeasurementHalfWindow,
                2 * MeasurementHalfWindow,
                2 * MeasurementHalfWindow,
                cancellationToken,
                imageCircle);
            measurement = FocusStarMeasurer.Measure(window.Pixels, window.ValidMask, window.SaturatedMask,
                window.Width, window.Height, current, window.OriginX, window.OriginY, options, cancellationToken);
            // Follow the measured centroid; a frame with no star keeps the last position so the star can return.
            if (measurement.Centroid is { } centroid && measurement.Status != FocusStarStatus.NoStar)
            {
                target = centroid;
            }
        }
        var images = CreateImages(frame, sequence, measurement, window, imageCircle, cancellationToken);
        return new ManualFocusPreviewOutcome(measurement, target, selected, window, frameSha256, images);
    }

    public static FocusStarTarget SelectTarget(
        CameraFrame frame,
        PixelPoint? hint,
        FocusStarMeasurementOptions options,
        CancellationToken cancellationToken,
        MeteringImageCircle? imageCircle = null)
    {
        int x, y, width, height;
        if (hint is { } near)
        {
            var half = (int)Math.Ceiling(TargetSearchRadiusPixels + options.AnnulusOuterRadiusPixels) + 2;
            x = (int)Math.Floor(near.X) - half;
            y = (int)Math.Floor(near.Y) - half;
            width = height = 2 * half;
        }
        else
        {
            width = Math.Min(frame.Width, AutomaticSearchSize);
            height = Math.Min(frame.Height, AutomaticSearchSize);
            x = (frame.Width - width) / 2;
            y = (frame.Height - height) / 2;
        }
        var window = ManualFocusFrameSampler.ExtractWindow(frame, x, y, width, height, cancellationToken, imageCircle);
        var local = hint is { } point ? new PixelPoint(point.X - window.OriginX, point.Y - window.OriginY) : (PixelPoint?)null;
        var selection = FocusStarMeasurer.SelectTarget(window.Pixels, window.ValidMask, window.SaturatedMask,
            window.Width, window.Height, options, local, TargetSearchRadiusPixels, cancellationToken);
        return selection.Position is { } position
            ? selection with { Position = new PixelPoint(position.X + window.OriginX, position.Y + window.OriginY) }
            : selection;
    }

    /// <summary>Identity of the request-scoped settings that produced a sample.</summary>
    public static string PreviewSettingsIdentity(ManualFocusPreviewSettings settings, double? simulatedFocusPosition)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "manual-focus-preview-settings-v1",
            exposureTicks = settings.Exposure.Ticks,
            gain = settings.Gain,
            simulatedFocusPosition
        })));

    private static ManualFocusPreviewImages CreateImages(
        CameraFrame frame,
        long sequence,
        FocusStarMeasurement measurement,
        ManualFocusFrameWindow? window,
        MeteringImageCircle? imageCircle,
        CancellationToken cancellationToken)
    {
        var overview = ManualFocusFrameSampler.CreateOverview(frame, OverviewMaximumDimension, cancellationToken, imageCircle);
        // Stretch from the sky only: the dark corners outside an image circle would otherwise set the black point and
        // push the whole sky to white.
        var (low, high) = Percentiles(overview.Pixels, 0.01, 0.999, overview.SkyMask);
        var overviewJpeg = Encode(overview.Pixels, overview.Width, overview.Height, low, high, squareRoot: true,
            cancellationToken);
        byte[]? starJpeg = null;
        if (window is not null)
        {
            var (starLow, starHigh) = measurement.Background is { } background && measurement.PeakAboveBackground is > 0
                ? (background, background + measurement.PeakAboveBackground.Value)
                : Percentiles(window.Pixels, 0.05, 1);
            starJpeg = Encode(window.Pixels, window.Width, window.Height, starLow, starHigh, squareRoot: false,
                cancellationToken);
        }
        return new ManualFocusPreviewImages(sequence, overviewJpeg, overview.Width, overview.Height, overview.BinFactor,
            frame.Width, frame.Height, starJpeg, window?.OriginX ?? 0, window?.OriginY ?? 0, window?.Width ?? 0,
            window?.Height ?? 0, CatalogStars(frame));
    }

    private static ManualFocusCatalogStar[] CatalogStars(CameraFrame frame)
    {
        if (frame.Metadata.Scene?.Objects is not { Count: > 0 } objects)
        {
            return [];
        }
        var margin = MeasurementOptions.AnnulusOuterRadiusPixels;
        return objects
            .Where(item => double.IsFinite(item.Magnitude) && double.IsFinite(item.PixelX) && double.IsFinite(item.PixelY) &&
                item.PixelX >= margin && item.PixelY >= margin &&
                item.PixelX <= frame.Width - margin && item.PixelY <= frame.Height - margin)
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Take(MaximumCatalogStars)
            .Select(static item => new ManualFocusCatalogStar(item.Id,
                string.IsNullOrWhiteSpace(item.DisplayName) ? null : item.DisplayName, item.Magnitude,
                new PixelPoint(item.PixelX, item.PixelY)))
            .ToArray();
    }

    private static (double Low, double High) Percentiles(
        double[] pixels,
        double lowFraction,
        double highFraction,
        bool[]? mask = null)
    {
        var step = Math.Max(1, pixels.Length / 65536);
        var sample = new List<double>(pixels.Length / step + 1);
        for (var index = 0; index < pixels.Length; index += step)
        {
            if (double.IsFinite(pixels[index]) && (mask is null || mask[index]))
            {
                sample.Add(pixels[index]);
            }
        }
        if (sample.Count == 0)
        {
            return (0, 1);
        }
        sample.Sort();
        var low = sample[(int)Math.Clamp(lowFraction * (sample.Count - 1), 0, sample.Count - 1)];
        var high = sample[(int)Math.Clamp(highFraction * (sample.Count - 1), 0, sample.Count - 1)];
        return high > low ? (low, high) : (low, low + 1);
    }

    private static byte[] Encode(
        double[] pixels,
        int width,
        int height,
        double low,
        double high,
        bool squareRoot,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[pixels.Length];
        var range = high - low;
        for (var index = 0; index < pixels.Length; index++)
        {
            var normalized = Math.Clamp((pixels[index] - low) / range, 0, 1);
            if (squareRoot)
            {
                normalized = Math.Sqrt(normalized);
            }
            bytes[index] = (byte)Math.Round(normalized * 255);
        }
        return JpegImageCodec.EncodeMono8ToJpeg(width, height, bytes, null, JpegQuality, cancellationToken);
    }
}
