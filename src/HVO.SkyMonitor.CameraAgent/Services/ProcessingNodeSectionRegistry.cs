using System.Collections.Immutable;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Read-only metadata describing a built-in stage supplied by the centered live profile (#1918).</summary>
internal sealed record BuiltInStageSummary(
    string StepAlias,
    string Title,
    string RoleSummary,
    string FixedSettingsSummary);

/// <summary>
/// Maps every registered optional processing-graph filter and overlay to its stable editor section slug
/// and missing-node guidance, and describes built-in #1918 stages for read-only inspection.
/// </summary>
internal static class ProcessingNodeSectionRegistry
{
    internal sealed record SectionEntry(
        string Slug,
        string DisplayName,
        ImmutableArray<string> Aliases,
        string MissingNodeMessage);

    private static readonly ImmutableArray<SectionEntry> Entries =
    [
        new("scene-overlay", "Scene overlay",
            [ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias, SceneStylePresentation.StepAlias],
            "This graph has no supported scene overlay node. Add ScenePresentationLayer with its ProjectedScene input to configure visibility, sizes and constellation style."),
        new("constellations", "Constellations",
            [ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias, SceneStylePresentation.StepAlias],
            "This graph has no supported scene overlay node. Add ScenePresentationLayer with its ProjectedScene input to configure visibility, sizes and constellation style."),
        new("deep-sky", "Deep-sky overlay",
            [ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias, SceneStylePresentation.StepAlias],
            "This graph has no supported scene overlay node for deep-sky styling. Add ScenePresentationLayer or SceneStylePresentationLayerV1 with its ProjectedScene input to configure deep-sky style and label settings; saving, validation and activation remain separate."),
        new("frame-facts", "Frame Facts",
            [FrameFactsPresentation.StepAlias],
            "This graph has no Frame Facts node. Add FrameFactsPresentationLayerV1 with its ProjectedScene and combined-preview inputs, and explicitly opt manifest/materializer consumers in before previewing the plan."),
        new("legacy-annotation", "Legacy annotation",
            ["Annotation"],
            "This graph has no supported legacy Annotation node. Select a retained graph with Annotation to edit its existing styling. Missing or unsupported nodes are not replaced or upgraded; saving, validation and activation remain separate."),
        new("weather-cloud-overlay", "Weather and cloud overlay",
            ["WeatherCloudOverlay"],
            "This graph has no weather and cloud overlay node. Add WeatherCloudOverlay with an explicit preview and CloudAssessment dependency to configure it; saving and activation remain separate."),
        new("cloud-layer", "Cloud layer",
            ["CloudPresentationLayer"],
            "This graph has no supported cloud layer node. Select a retained graph with CloudPresentationLayer to edit labels and line thickness. Dimensions and assessment bindings must already be configured; missing or unsupported nodes are not created or upgraded. Saving, validation and activation remain separate."),
        new("environment-layer", "Environment layer",
            ["EnvironmentPresentationLayer"],
            "This graph has no supported environment layer node. Select a retained graph with EnvironmentPresentationLayer to edit scale, inset and line spacing. Dimensions and source/stack bindings must already be configured; missing or unsupported nodes are not created or upgraded. Saving, validation and activation remain separate."),
        new("predicted-satellite-layer", "Predicted satellite layer",
            ["CaptureSatelliteLayer"],
            "This graph has no supported predicted satellite layer node. Select a saved graph with CaptureSatelliteLayer and its capture-bound prediction inputs. Saving, validation and activation remain separate."),
        new("overlay-manifest", "Overlay manifest",
            ["OverlayManifest"],
            "This graph has no supported Overlay manifest node. Existing layer producers and required dependencies must be configured explicitly."),
        new("presentation-materializer", "Presentation materializer",
            ["PresentationMaterializer"],
            "This graph has no supported PresentationMaterializer node. Select a saved graph with PresentationMaterializer and its OverlayManifest input to configure enabled layers. Saving, validation and activation remain separate."),
        new("projected-scene", "Projected scene query",
            ["ProjectedScene"],
            "This graph has no supported projected scene node. Add ProjectedScene with an explicit raw dependency to configure physical predicted-scene queries. VirtualSky keeps its separate module settings and authoritative rendered scene; saving, validation and activation remain separate."),
        new("calibration", "Calibration",
            ["Calibration"],
            "This graph has no calibration node. Add Calibration with an explicit raw dependency to configure it. Existing models and master provisioning stay unchanged; saving, validation and activation remain separate."),
        new("cloud-assessment", "Cloud assessment",
            ["CloudAssessment"],
            "This graph has no cloud assessment node. Add CloudAssessment with an explicit raw or calibrated image dependency to configure it. Clear-reference provisioning stays unchanged; saving, validation and activation remain separate."),
        new("regional-stellar-visibility", "Regional stellar visibility",
            [RegionalStellarVisibilityProducts.StepAlias],
            "This graph has no regional stellar visibility node. Add RegionalStellarVisibility to configure per-tile star visibility evidence alongside clear-reference cloud assessment; saving, validation and activation remain separate."),
        new("image-quality", "Image quality",
            ["ImageQuality"],
            "This graph has no supported ImageQuality node. Add ImageQuality with an explicit image dependency to configure quality statistics and output routing. Saving, validation and activation remain separate."),
        new("capture-telemetry", "Capture telemetry",
            ["Telemetry"],
            "This graph has no supported Telemetry node. Add Telemetry with its required input dependency to configure capture telemetry publishing. Saving, validation and activation remain separate."),
        new("simulated-cloud-observations", "Simulated cloud observations",
            ["VirtualSkyCloudObservation"],
            "This graph has no supported VirtualSkyCloudObservation node. Add VirtualSkyCloudObservation to configure simulated cloud observation publishing on VirtualSky captures. Saving, validation and activation remain separate."),
        new("rolling-combination", "Rolling combination",
            ["RollingCombination"],
            "This graph has no supported RollingCombination node. Add RollingCombination with a compatible linear input dependency to configure rolling window size, kind and limits. Saving, validation and activation remain separate."),
        new("astrometry-prediction", "Astrometry catalog prediction",
            ["AstrometryPrediction"],
            "This graph has no supported AstrometryPrediction node. Add AstrometryPrediction with an explicit raw dependency to configure catalog star prediction limits. Saving, validation and activation remain separate."),
        new("measured-stellar-associations", "Measured stellar associations",
            ["MeasuredStellarAssociations"],
            "This graph has no supported MeasuredStellarAssociations node. Add MeasuredStellarAssociations with its raw and scene inputs to configure association routing. Saving, validation and activation remain separate."),
        new("measured-astrometry", "Measured astrometry",
            ["MeasuredAstrometry"],
            "This graph has no supported MeasuredAstrometry node. Add MeasuredAstrometry with its measured association input to configure capture stride and output routing. Saving, validation and activation remain separate."),
        new("exposure-stellar-associations", "Exposure stellar associations",
            [ExposureAssociationProducts.StepAlias],
            "This graph has no supported ExposureStellarAssociations node. Select a centered-profile graph containing ExposureStellarAssociations to configure its output routing. Saving, validation and activation remain separate."),
        new("exposure-measured-astrometry", "Exposure measured astrometry",
            [CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias],
            "This graph has no supported ExposureMeasuredAstrometry node. Select a centered-profile graph containing ExposureMeasuredAstrometry to configure its capture stride and output routing. Saving, validation and activation remain separate."),
        new("capture-satellite-tracks", "Capture satellite predictions",
            ["CaptureSatelliteTracks"],
            "This graph has no supported CaptureSatelliteTracks node. Add CaptureSatelliteTracks with an explicit raw dependency to configure satellite track prediction routing, satellite object-label visibility, and stale diagnostics. Saving, validation and activation remain separate."),
        new("artifact-storage", "Artifact storage",
            ["Storage"],
            "This graph has no supported Storage node. Select a graph with Storage to inspect or align its storage settings with the active capture profile. Saving, validation and activation remain separate."),
        new("preview", "Preview",
            ["Preview"],
            "This graph has no supported Preview node. Add Preview with an explicit raw dependency to configure display stretch and output routing. Saving, validation and activation remain separate."),
        new("calibrated-preview", "Calibrated preview",
            ["CalibratedPreview"],
            "This graph has no supported CalibratedPreview node. Add CalibratedPreview with a Calibration dependency to configure calibrated preview display stretch and output routing. Saving, validation and activation remain separate."),
        new("combined-preview", "Combined preview",
            ["CombinedPreview"],
            "This graph has no supported CombinedPreview node. Add CombinedPreview with a RollingCombination dependency to configure combined preview display stretch and output routing. Saving, validation and activation remain separate."),
        new("jpeg-encoding", "JPEG encoding",
            ["JpegEncoding"],
            "This graph has no supported JpegEncoding node. Add JpegEncoding with a packed preview dependency to configure JPEG quality, maximum dimension and output routing. Saving, validation and activation remain separate.")
    ];

    private static readonly Dictionary<string, SectionEntry> BySlug = Entries.ToDictionary(
        static entry => entry.Slug, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> PrimarySlugByAlias = BuildPrimarySlugs();

    public static ImmutableArray<SectionEntry> All => Entries;

    public static bool IsKnownSection(string? section)
        => !string.IsNullOrWhiteSpace(section) && BySlug.ContainsKey(section);

    public static SectionEntry? FindSection(string? section)
        => !string.IsNullOrWhiteSpace(section) && BySlug.TryGetValue(section, out var entry) ? entry : null;

    public static string SectionForAlias(string? stepAlias)
        => !string.IsNullOrWhiteSpace(stepAlias) && PrimarySlugByAlias.TryGetValue(stepAlias, out var slug)
            ? slug
            : "constellations";

    public static bool MatchesSection(string section, string? stepAlias)
        => FindSection(section) is { } entry &&
            !string.IsNullOrWhiteSpace(stepAlias) &&
            entry.Aliases.Any(alias => string.Equals(alias, stepAlias, StringComparison.OrdinalIgnoreCase));

    public static bool IsSceneAlias(string? stepAlias)
        => stepAlias is ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias or SceneStylePresentation.StepAlias;

    public static string OptionsSectionId(IReadOnlyList<ProcessingGraphDraftModel.NodeRow> nodes, ProcessingGraphDraftModel.NodeRow node)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(node);
        var alias = node.OptionsForm?.Descriptor.StepAlias;
        var section = SectionForAlias(alias);
        var isScene = IsSceneAlias(alias);
        ProcessingGraphDraftModel.NodeRow? first = null;
        var index = -1;
        for (var i = 0; i < nodes.Count; i++)
        {
            var row = nodes[i];
            if (first is null && (isScene
                ? IsSceneAlias(row.OptionsForm?.Descriptor.StepAlias)
                : string.Equals(row.OptionsForm?.Descriptor.StepAlias, alias, StringComparison.Ordinal)))
            {
                first = row;
            }
            if (ReferenceEquals(row, node))
            {
                index = i;
            }
        }
        return ReferenceEquals(node, first) || index < 0 ? section : $"{section}-{index}";
    }

    public static BuiltInStageSummary DescribeBuiltInStage(string stepAlias)
    {
        if (string.Equals(stepAlias, ExposureMeasurementProducts.StepAlias, StringComparison.OrdinalIgnoreCase))
        {
            return new BuiltInStageSummary(
                ExposureMeasurementProducts.StepAlias,
                "Exposure measurement (built-in)",
                "Built-in mandatory measurement stage supplied by the centered live profile (#1918). Runs raw brightness, source centroid, and half-flux diameter attempts for every admitted exposure and cannot be disabled.",
                $"Fixed built-in recipe ({ExposureMeasurementProducts.SchemaVersion}); samples up to {ExposureMeasurementProducts.MaximumHfdSamples} eligible detections for HFD; no user-editable node options.");
        }
        return new BuiltInStageSummary(
            ExposureCatalogPredictionProducts.StepAlias,
            "Exposure catalog prediction (built-in)",
            "Built-in system-managed stage supplied by the centered live profile (#1918). Computes an independent catalog star prediction from admitted raw metadata for optional centered association and astrometry steps and cannot be disabled independently.",
            "Fixed durable catalog prediction from admitted raw capture; no user-editable node options.");
    }

    private static Dictionary<string, string> BuildPrimarySlugs()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias] = "constellations",
            [SceneStylePresentation.StepAlias] = "constellations",
            [FrameFactsPresentation.StepAlias] = "frame-facts",
            ["Annotation"] = "legacy-annotation"
        };
        foreach (var entry in Entries)
        {
            foreach (var alias in entry.Aliases)
            {
                map.TryAdd(alias, entry.Slug);
            }
        }
        return map;
    }
}
