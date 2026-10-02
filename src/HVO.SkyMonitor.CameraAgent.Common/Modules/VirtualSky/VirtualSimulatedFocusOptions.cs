using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>
/// Declared simulated focus control for VirtualSky manual-focus previews. The rendered Gaussian point-spread width follows
/// <c>sigma(p) = sqrt(sigma0^2 + (k * (p - best))^2)</c>, capped at <see cref="MaximumSigmaPixels"/>, where
/// <c>sigma0</c> is the configured <see cref="VirtualSkyCameraModuleOptions.PsfSigmaPixels"/>. Positions are simulated
/// steps with no physical scale. The model applies only to focus previews; ordinary captures always render the configured
/// point-spread function.
/// </summary>
public sealed record VirtualSimulatedFocusOptions
{
    public const string ModelId = "virtual-defocus-gaussian-quadrature-v1";
    public const string Units = "simulated focus steps";

    /// <summary>Largest rendered sigma; its four-sigma kernel support stays within the 64-pixel raster limit.</summary>
    public const double SigmaCeilingPixels = 16;

    public bool Enabled { get; init; } = true;

    public double MinimumPosition { get; init; }

    public double MaximumPosition { get; init; } = 1000;

    /// <summary>Position a session starts at when the operator does not choose one; deliberately away from best focus.</summary>
    public double DefaultPosition { get; init; } = 200;

    /// <summary>The declared position of best focus. Recorded in provenance; it is the model, not a measured optimum.</summary>
    public double BestPosition { get; init; } = 560;

    /// <summary>Added Gaussian sigma per simulated step away from <see cref="BestPosition"/>.</summary>
    public double DefocusSigmaPixelsPerStep { get; init; } = 0.014;

    public double MaximumSigmaPixels { get; init; } = 8;

    internal void Validate(double baseSigmaPixels)
    {
        if (!double.IsFinite(MinimumPosition) || !double.IsFinite(MaximumPosition) || MinimumPosition >= MaximumPosition ||
            MaximumPosition - MinimumPosition > 1_000_000 ||
            !double.IsFinite(DefaultPosition) || DefaultPosition < MinimumPosition || DefaultPosition > MaximumPosition ||
            !double.IsFinite(BestPosition) || BestPosition < MinimumPosition || BestPosition > MaximumPosition ||
            !double.IsFinite(DefocusSigmaPixelsPerStep) || DefocusSigmaPixelsPerStep <= 0 ||
            !double.IsFinite(MaximumSigmaPixels) || MaximumSigmaPixels > SigmaCeilingPixels ||
            MaximumSigmaPixels < baseSigmaPixels)
        {
            throw new ArgumentException("The simulated focus model is invalid.");
        }
    }

    /// <summary>Rendered sigma at <paramref name="position"/>; never narrower than the configured base sigma.</summary>
    public double SigmaPixels(double baseSigmaPixels, double position)
    {
        var defocus = DefocusSigmaPixelsPerStep * (position - BestPosition);
        return Math.Min(Math.Max(MaximumSigmaPixels, baseSigmaPixels),
            Math.Sqrt(baseSigmaPixels * baseSigmaPixels + defocus * defocus));
    }

    /// <summary>Kernel support: the configured radius, widened to four sigma so the raster stays qualified.</summary>
    public static double RadiusPixels(double configuredRadiusPixels, double sigmaPixels)
        => Math.Max(configuredRadiusPixels, 4 * sigmaPixels);

    internal CameraSimulatedFocusModel Describe(double baseSigmaPixels)
        => new(ModelId, Units, MinimumPosition, MaximumPosition, DefaultPosition, ParametersSha256(baseSigmaPixels));

    internal string ParametersSha256(double baseSigmaPixels)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = ModelId,
            baseSigmaPixels = baseSigmaPixels.ToString("R", CultureInfo.InvariantCulture),
            minimum = MinimumPosition.ToString("R", CultureInfo.InvariantCulture),
            maximum = MaximumPosition.ToString("R", CultureInfo.InvariantCulture),
            best = BestPosition.ToString("R", CultureInfo.InvariantCulture),
            slope = DefocusSigmaPixelsPerStep.ToString("R", CultureInfo.InvariantCulture),
            ceiling = MaximumSigmaPixels.ToString("R", CultureInfo.InvariantCulture)
        })));
}
