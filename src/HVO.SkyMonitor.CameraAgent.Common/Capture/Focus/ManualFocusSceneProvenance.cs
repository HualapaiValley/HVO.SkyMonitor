using System.Globalization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>
/// Recorded scene facts for a focus sample. Celestial time and actual solar geometry remain independent of illumination
/// controls. Missing metadata and older saved records retain unknown values rather than inferred nighttime facts.
/// </summary>
public sealed record ManualFocusSceneProvenance(
    DateTimeOffset? CelestialUtc,
    VirtualExposureProvenance? VirtualExposure,
    double? SunAltitudeDegrees,
    string? IlluminationMode,
    string? EffectiveIllumination,
    string? IlluminationAlgorithm,
    bool? BackgroundExplicit,
    double? ExplicitBackgroundElectronsPerSecond,
    double? EffectiveBackgroundElectronsPerSecond,
    string? SolarEphemerisModel,
    string? CatalogName = null,
    string? CatalogVersion = null,
    string? CatalogChecksumSha256 = null,
    string? RigProfileVersion = null,
    string? RigProfileHashSha256 = null,
    string? ProjectionModel = null,
    string? ProjectionAlgorithmVersion = null,
    string? ProjectionCalibrationVersion = null,
    string? AstronomyAlgorithmVersion = null,
    string? SensorRecipeVersion = null)
{
    internal static ManualFocusSceneProvenance? FromMetadata(FrameMetadata metadata)
    {
        var scene = metadata.Scene;
        var extra = metadata.Extra;
        if (scene is null && (extra is null ||
            (!extra.ContainsKey("skyIlluminationMode") && !extra.ContainsKey("stellarSolarAltitudeDegrees"))))
        {
            return null;
        }
        var altitude = Number("stellarSolarAltitudeDegrees");
        var explicitRate = Number("skyExplicitBackgroundElectronsPerSecond");
        var effectiveRate = Number("stellarBackgroundElectronsPerSecond");
        return new(scene?.SceneUtc, scene?.VirtualExposure,
            altitude is >= -90 and <= 90 ? altitude : null,
            Text("skyIlluminationMode"), Text("skyIlluminationEffective"), Text("skyIlluminationAlgorithm"),
            bool.TryParse(Text("stellarBackgroundExplicit"), out var explicitBackground) ? explicitBackground : null,
            explicitRate is >= 0 ? explicitRate : null,
            effectiveRate is >= 0 ? effectiveRate : null,
            Text("stellarSolarBackgroundEphemerisModel"),
            Bounded(scene?.CatalogName), Bounded(scene?.CatalogVersion), Hash(scene?.CatalogChecksumSha256),
            Bounded(scene?.RigProfileVersion), Hash(scene?.RigProfileHashSha256), Bounded(scene?.ProjectionModel),
            Bounded(scene?.ProjectionAlgorithmVersion), Bounded(scene?.ProjectionCalibrationVersion),
            Bounded(scene?.AstronomyAlgorithmVersion), Bounded(scene?.SensorRecipeVersion));

        string? Text(string key) => extra is not null && extra.TryGetValue(key, out var value) ? Bounded(value) : null;

        double? Number(string key)
            => double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value) ? value : null;
    }

    internal static string? Bounded(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl) ? value : null;

    private static string? Hash(string? value)
        => value?.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
}

/// <summary>Exact declared and effective Gaussian PSF inputs. Unknown inputs remain null for older or other modules.</summary>
public sealed record ManualFocusPsfProvenance(
    double? BaseSigmaPixels,
    double? BaseRadiusPixels,
    double? EffectiveSigmaPixels,
    double? EffectiveRadiusPixels,
    bool? SimulatedControlEnabled,
    string? SimulatedUnits,
    double? MinimumPosition,
    double? MaximumPosition,
    double? DefaultPosition,
    double? BestPosition,
    double? SigmaPixelsPerStep,
    double? MaximumSigmaPixels)
{
    internal static ManualFocusPsfProvenance? FromMetadata(FrameMetadata metadata, CameraSimulatedFocusModel? model)
    {
        var extra = metadata.Extra;
        if (extra is null || !extra.ContainsKey("psfSigmaPixels"))
        {
            return null;
        }
        return new(Number("psfBaseSigmaPixels"), Number("psfBaseRadiusPixels"), Number("psfSigmaPixels"),
            Number("psfRadiusPixels"), bool.TryParse(Text("simulatedFocusEnabled"), out var enabled) ? enabled : null,
            ManualFocusSceneProvenance.Bounded(model?.Units), Finite(model?.MinimumPosition),
            Finite(model?.MaximumPosition), Finite(model?.DefaultPosition), Number("simulatedFocusBestPosition"),
            Number("simulatedFocusSigmaPixelsPerStep"), Number("simulatedFocusMaximumSigmaPixels"));

        string? Text(string key) => extra.TryGetValue(key, out var value) ? value : null;
        double? Number(string key)
            => double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? Finite(value) : null;
        static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
    }
}
