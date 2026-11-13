using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Designation families included in a deep-sky query; combinations form a deduplicated union.</summary>
[Flags]
public enum DeepSkyDesignations
{
    None = 0,
    Ngc = 1,
    Messier = 2,
    Ic = 4,
    All = Ngc | Messier | Ic
}

/// <summary>Bounded expected-position selection from the independently installed OpenNGC catalog.</summary>
public sealed record OpenNgcDeepSkyOptions(
    bool Enabled = false,
    bool IncludeNgc = true,
    bool IncludeMessier = true,
    bool IncludeIc = true,
    double MaximumMagnitude = 12,
    bool IncludeUnknownMagnitude = true,
    double MinimumSizeArcminutes = 0,
    double MaximumSizeArcminutes = 1200,
    bool IncludeUnknownSize = true,
    int MaximumObjects = 128,
    int MaximumFootprints = 32,
    double MinimumGlyphPixels = 8)
{
    public const string SelectionVersion = "openngc-designation-query-v1";

    [JsonIgnore]
    public DeepSkyDesignations Designations => (IncludeNgc ? DeepSkyDesignations.Ngc : 0) |
        (IncludeMessier ? DeepSkyDesignations.Messier : 0) | (IncludeIc ? DeepSkyDesignations.Ic : 0);

    public DeepSkyQuery ToQuery(int maximumResults)
    {
        var query = new DeepSkyQuery(maximumResults, Brightness: new(
            DeepSkyMagnitudeBand.Visual, MaximumMagnitude, IncludeUnknownMagnitude), Designations: Designations,
            MinimumSizeArcminutes: MinimumSizeArcminutes, MaximumSizeArcminutes: MaximumSizeArcminutes,
            IncludeUnknownSize: IncludeUnknownSize);
        query.Validate();
        if (MaximumMagnitude is < -30 or > 30 || MaximumSizeArcminutes > 1200)
            throw new ArgumentException("Deep-sky magnitude or size exceeds the supported range.");
        return query;
    }

    public ProjectedSceneDeepSkySelection ToSelection()
    {
        _ = ToQuery(MaximumObjects);
        var selection = new ProjectedSceneDeepSkySelection(MaximumObjects, MaximumFootprints, 0,
            DeepSkyOutline.WidestLevel, MinimumGlyphPixels, SelectionVersion, this);
        selection.Validate();
        return selection;
    }
}
