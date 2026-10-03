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
    string? SolarEphemerisModel)
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
            Text("stellarSolarBackgroundEphemerisModel"));

        string? Text(string key) => extra is not null && extra.TryGetValue(key, out var value) ? value : null;

        double? Number(string key)
            => double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value) ? value : null;
    }
}
