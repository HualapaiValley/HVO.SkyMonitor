using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>The existing physical predicted-scene query, at its exact registered step version.</summary>
public static class ProjectedSceneOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "projected-scene-options-v1",
        "ProjectedScene",
        "canonical-projected-scene-v1",
        "Projected scene query",
        "Select catalog stars for physical predicted-scene staging. VirtualSky uses its separate module settings for stars and its authoritative rendered scene. The deep-sky controls also apply to VirtualSky unless its module explicitly overrides them. Save, validate and explicitly activate a new graph revision to change subsequent physical captures; retained scenes stay unchanged.",
        [
            ProcessingOptionDescriptor.Toggle("deepSkyEnabled", "deepSkyEnabled", "Enable deep-sky annotations", "Use the independently selected OpenNGC catalog for expected positions.", false),
            ProcessingOptionDescriptor.Toggle("deepSkyNgc", "deepSkyNgc", "NGC designations", "Include NGC objects; overlapping selections draw one canonical object.", true),
            ProcessingOptionDescriptor.Toggle("deepSkyMessier", "deepSkyMessier", "Messier designations", "Include Messier objects, including objects without an NGC or IC identifier.", true),
            ProcessingOptionDescriptor.Toggle("deepSkyIc", "deepSkyIc", "IC designations", "Include IC objects.", true),
            ProcessingOptionDescriptor.RealNumber("deepSkyMaximumMagnitude", "deepSkyMaximumMagnitude", "Deep-sky maximum V magnitude", "Faintest included V magnitude; B magnitude never substitutes for V.", 12, -30, 30, "mag"),
            ProcessingOptionDescriptor.Toggle("deepSkyIncludeUnknownMagnitude", "deepSkyIncludeUnknownMagnitude", "Include unknown V magnitude", "Keep objects whose V magnitude is unknown.", true),
            ProcessingOptionDescriptor.RealNumber("deepSkyMinimumSizeArcminutes", "deepSkyMinimumSizeArcminutes", "Deep-sky minimum size", "Inclusive major-axis lower bound.", 0, 0, 1200, "arcmin"),
            ProcessingOptionDescriptor.RealNumber("deepSkyMaximumSizeArcminutes", "deepSkyMaximumSizeArcminutes", "Deep-sky maximum size", "Inclusive major-axis upper bound; supported approximate extents only.", 1200, 0, 1200, "arcmin"),
            ProcessingOptionDescriptor.Toggle("deepSkyIncludeUnknownSize", "deepSkyIncludeUnknownSize", "Include unknown size", "Keep unknown sizes as class symbols; do not invent an extent.", true),
            ProcessingOptionDescriptor.RealNumber("maximumMagnitude", "maximumMagnitude", "Maximum star magnitude",
                "Include catalog stars through this magnitude; larger values include fainter stars. Existing constellation endpoint and solar-system selection rules still apply.",
                6.5, -30, 30, "mag"),
            ProcessingOptionDescriptor.WholeNumber("maximumResults", "maximumResults", "Maximum star results",
                "Limit magnitude-selected catalog stars after projection. Requested constellation endpoint stars can be added beyond this query limit.",
                2000, 1, ProjectedSceneJson.MaximumObjectCount, "stars"),
            ProcessingOptionDescriptor.Toggle("includeConstellationEndpointStars", "includeConstellationEndpointStars", "Include constellation endpoint stars",
                "Also include available endpoint stars for the graph's existing constellation selection, even beyond the magnitude and result limits. This does not select constellations.",
                false),
            ProcessingOptionDescriptor.TextCollection("constellationIds", "constellationIds", "Constellation IDs",
                "Request these existing constellation figures. Empty selects no figures or endpoint stars. Nonblank IDs, duplicates and source order are retained without consulting installed display choices.", 128, 16),
            ProcessingOptionDescriptor.EnumCollection<SolarSystemBody>("solarSystemBodies", "solarSystemBodies", "Solar-system bodies",
                "Request these defined solar-system bodies. Empty selects none. Duplicates and source order are allowed; existing numeric and named values keep their source spelling.")
        ]);
}
