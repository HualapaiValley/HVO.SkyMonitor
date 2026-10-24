using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

/// <summary>Discoverable source and unit documentation for a scalar and its generated status token.</summary>
public sealed record FrameFactsRetainedToken(
    FrameFactsTokenDefinition Definition, string Source, string Unit, string Provenance);

/// <summary>The versioned, producer-independent catalogue. It does not register a product or change a graph.</summary>
public static class FrameFactsRetainedCatalog
{
    public const string Version = "hvo-frame-facts-retained-v1";

    internal static ImmutableArray<(EnvironmentalObservationKind Kind, string Key, string Unit)> Environment { get; } =
    [
        (EnvironmentalObservationKind.AirTemperature, "airTemperature", "C"),
        (EnvironmentalObservationKind.RelativeHumidity, "relativeHumidity", "%"),
        (EnvironmentalObservationKind.AtmosphericPressure, "atmosphericPressure", "Pa"),
        (EnvironmentalObservationKind.WindSpeed, "windSpeed", "m/s"),
        (EnvironmentalObservationKind.WindDirection, "windDirection", "degrees true"),
        (EnvironmentalObservationKind.WindGust, "windGust", "m/s"),
        (EnvironmentalObservationKind.PrecipitationRate, "precipitationRate", "mm/h"),
        (EnvironmentalObservationKind.RainState, "rainState", "TRUE/FALSE"),
        (EnvironmentalObservationKind.SkyBrightness, "skyBrightness", "mag/arcsec2"),
        (EnvironmentalObservationKind.SkyQuality, "skyQuality", "mag/arcsec2"),
        (EnvironmentalObservationKind.CloudCover, "cloudCover", "fraction [0,1]"),
        (EnvironmentalObservationKind.CameraSensorTemperature, "cameraSensorTemperature", "C")
    ];

    public static ImmutableArray<FrameFactsRetainedToken> Entries { get; } = CreateEntries();
    public static FrameFactsTokenCatalog Catalog { get; } = new(Entries.Select(entry => entry.Definition));

    private static ImmutableArray<FrameFactsRetainedToken> CreateEntries()
    {
        var entries = ImmutableArray.CreateBuilder<FrameFactsRetainedToken>();
        void Add(string key, FrameFactsValueKind kind, string source, string unit, string provenance)
            => entries.Add(new(new(key, kind), source, unit, provenance));

        const string descriptor = "ReconstructionDescriptor";
        foreach (var name in new[] { "agentId", "rigId", "captureId", "sourceId", "artifactId" })
            Add("capture." + name, FrameFactsValueKind.Text, descriptor + ".Capture/Artifact", "text", "Retained identity");
        foreach (var name in new[] { "sequence", "width", "height" })
            Add("capture." + name, FrameFactsValueKind.WholeNumber, descriptor + ".Capture/Layout", name == "sequence" ? "count" : "pixels", "Retained capture");
        foreach (var (name, unit) in new[] { ("exposureSeconds", "s"), ("gain", "camera gain units"), ("offset", "camera offset units"),
            ("sensorSetpointC", "C"), ("sensorTemperatureC", "C") })
            Add("acquisition." + name, FrameFactsValueKind.Number, descriptor + ".Controls", unit,
                name == "sensorSetpointC" ? "Configured setpoint, not a measurement" : "Module-reported effective control, not an environmental observation");
        Add("acquisition.scheduleProfile", FrameFactsValueKind.Text, descriptor + ".CycleEvidence.ScheduleAdmission.SetpointProfileId", "text", "Retained admission");
        Add("acquisition.cadenceMode", FrameFactsValueKind.Text, descriptor + ".CycleEvidence.CadenceMode", "enum text", "Retained acquisition");
        Add("acquisition.cadenceSeconds", FrameFactsValueKind.Number, "FrameFactsRetainedCaptureExtrasV1.CadenceSeconds (retained submission interval)", "s", "Retained acquisition; never current configuration");
        foreach (var name in new[] { "sourceUtc", "captureUtc", "captureEndUtc", "artifactCreatedUtc", "processingUtc", "captureLocal" })
            Add("time." + name, FrameFactsValueKind.Timestamp,
                name switch
                {
                    "sourceUtc" => "FrameFactsRetainedCaptureExtrasV1.SourceTimestamp (CameraFrame.TimestampUtc)",
                    "processingUtc" => "FrameFactsRetainedCaptureExtrasV1.ProcessingCompletedUtc (GraphExecutionEvidenceV1.CompletedUtc)",
                    "captureLocal" => "ExposureStartedUtc + FrameFactsRetainedLocalTimeV1 frozen offset",
                    "artifactCreatedUtc" => descriptor + ".Artifact.CreatedUtc",
                    _ => descriptor + ".Timing.ExposureStartedUtc/ExposureEndedUtc"
                }, "timestamp", name == "captureLocal" ? "Explicit retained zone/rule/offset" : "Retained instant normalized to UTC");
        Add("time.captureLegacyUtc", FrameFactsValueKind.Text, descriptor + ".Timing.ExposureStartedUtc", "yyyy-MM-ddTHH:mm:ss.fffZ", "Explicit UTC legacy display");
        Add("time.zoneId", FrameFactsValueKind.Text, "FrameFactsRetainedLocalTimeV1.ZoneId", "zone identifier", "Frozen local-time evidence");
        Add("time.zoneRuleIdentity", FrameFactsValueKind.Text, "FrameFactsRetainedLocalTimeV1.RuleIdentity", "identity", "Frozen local-time evidence");
        Add("stack.count", FrameFactsValueKind.WholeNumber, "FrameFactsRetainedStackV1.SourceArtifactIds.Count", "frames", "Retained stack lineage");
        Add("stack.integrationSeconds", FrameFactsValueKind.Number, "FrameFactsRetainedStackV1.TotalIntegration", "s", "Retained integration, never count times current exposure");
        Add("stack.identity", FrameFactsValueKind.Text, "FrameFactsRetainedStackV1.Identity", "SHA-256", "Retained stack product");
        foreach (var family in new[] { "calibration", "processing" })
            foreach (var field in new[] { "name", "version", "sha256", "shortHash" })
                Add($"lineage.{family}.{field}", FrameFactsValueKind.Text, descriptor + ".Profiles." + family,
                    field is "sha256" or "shortHash" ? "SHA-256 (shortHash: first 12)" : "text", "Retained capture-time profile");
        foreach (var field in new[] { "name", "version", "sha256" })
            Add("lineage.catalog." + field, FrameFactsValueKind.Text, "ProjectedSceneV1.Catalog", field == "sha256" ? "SHA-256" : "text", "Retained scene catalogue");
        foreach (var field in new[] { "id", "source" })
            Add("location." + field, FrameFactsValueKind.Text, descriptor + ".Location", "text", "Retained coordinate-free location provenance");
        Add("location.version", FrameFactsValueKind.WholeNumber, descriptor + ".Location.Version", "version", "Retained coordinate-free location provenance");
        foreach (var field in new[] { "latitude", "longitude", "elevation" })
            Add("location." + field, FrameFactsValueKind.Number, "DeploymentLocationSnapshot", field == "elevation" ? "m" : "degrees", "Matching retained location provenance and effective interval");
        Add("location.timeZone", FrameFactsValueKind.Text, "DeploymentLocationSnapshot.TimeZoneId", "zone identifier", "Matching retained location; does not imply a local offset");
        Add("astronomy.kind", FrameFactsValueKind.Text, "ProjectedSceneV1.Kind", "enum text", "Predicted, VirtualRenderAuthoritative or ImageRegistered; never detection");
        Add("astronomy.effectiveUtc", FrameFactsValueKind.Timestamp, "ProjectedSceneV1.EffectiveUtc", "UTC timestamp", "Retained scene time, distinct from capture time");
        Add("astronomy.algorithm", FrameFactsValueKind.Text, "ProjectedSceneV1.AstronomyAlgorithmVersion", "version", "Retained scene algorithm");
        Add("astronomy.refraction", FrameFactsValueKind.Text, "ProjectedSceneV1.Refraction.Enabled", "TRUE/FALSE", "Configured scene refraction");
        foreach (var field in new[] { "observerLatitude", "observerLongitude", "observerElevation", "boresightAltitude", "boresightAzimuth", "roll" })
            Add("astronomy." + field, FrameFactsValueKind.Number, "ProjectedSceneV1.Observer/Projection", field == "observerElevation" ? "m" : "degrees", "Configured scene geometry, not measured location or pointing");
        foreach (var body in new[] { "sun", "moon" })
            foreach (var coordinate in new[] { "geometricAltitude", "apparentAltitude", "azimuth" })
                Add($"astronomy.{body}.{coordinate}", FrameFactsValueKind.Number,
                    "ProjectedSceneV1.Objects[solar-system:" + (body == "sun" ? "Sun" : "Moon") + "].GeometricHorizontal/ApparentHorizontal",
                    "degrees", "Retained derived geometry with scene kind; absent objects are unavailable, never recomputed");
        foreach (var (kind, key, unit) in Environment)
        {
            var prefix = "environment." + key;
            Add(prefix, kind == EnvironmentalObservationKind.RainState ? FrameFactsValueKind.Text : FrameFactsValueKind.Number,
                "EnvironmentalObservationFactV1.Value." + kind, unit, "Retained association and observation; source kind preserved");
            Add(prefix + ".sourceKind", FrameFactsValueKind.Text, "EnvironmentalObservationFactV1.Source.Kind", "enum text", "Measured/Simulated/Imported/Manual/Derived, never promoted");
            Add(prefix + ".quality", FrameFactsValueKind.Text, "EnvironmentalObservationFactV1.Value.Quality", "enum text", "Retained observation quality");
            Add(prefix + ".observedUtc", FrameFactsValueKind.Timestamp, "EnvironmentalObservationFactV1.ObservedAtUtc", "UTC timestamp", "Retained observation time");
        }
        return entries.ToImmutable();
    }
}
