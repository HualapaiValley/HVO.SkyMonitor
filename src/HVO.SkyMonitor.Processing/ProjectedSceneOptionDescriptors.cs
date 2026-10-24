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
        "Select catalog stars for physical predicted-scene staging. VirtualSky uses its separate module settings and authoritative rendered scene. Save, validate and explicitly activate a new graph revision to change subsequent physical captures; retained scenes stay unchanged.",
        [
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
