using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Separates global source activity from an HVO estimate for optical observing.</summary>
public static class SpaceWeatherRelevance
{
    /// <summary>Computes relevance using the source code as well as family, since only WARK warnings are geomagnetic.</summary>
    public static LocalRelevance Categorize(SpaceWeatherFamily family, string code, string? level, double? statedLatitude, double? siteGeomagneticLatitude)
    {
        if (statedLatitude is { } stated && (!double.IsFinite(stated) || stated is < 0 or > 99)) throw new ArgumentOutOfRangeException(nameof(statedLatitude));
        if (siteGeomagneticLatitude is { } phi && (!double.IsFinite(phi) || phi is < -90 or > 90)) throw new ArgumentOutOfRangeException(nameof(siteGeomagneticLatitude));
        LocalRelevance Unknown(LocalRelevanceCategory category) => new(category, null, null, siteGeomagneticLatitude, null, GeomagneticLatitude.ModelVersion);
        if (family == SpaceWeatherFamily.Unclassified) return Unknown(LocalRelevanceCategory.Unknown);
        if (family is not (SpaceWeatherFamily.Watch or SpaceWeatherFamily.KAlert)
            && !(family == SpaceWeatherFamily.Warning && SwpcText.Whole(code, "WARK0[4-9]"))) return Unknown(LocalRelevanceCategory.NoLocalObservingImpact);
        if (siteGeomagneticLatitude is null) return Unknown(LocalRelevanceCategory.Unknown);
        // fixtures.py uses `poleward or typical`: a stated zero therefore falls back to the typical level.
        var required = statedLatitude is > 0 ? statedLatitude : level switch
        {
            "K4" => 65d, "G1" => 60d, "G2" => 55d, "G3" => 50d, "G4" => 45d, "G5" => 40d, _ => (double?)null
        };
        if (required is null) return Unknown(LocalRelevanceCategory.Unknown);
        var difference = Difference(required.Value, siteGeomagneticLatitude.Value);
        var category = difference <= 0 ? LocalRelevanceCategory.InImpactArea : difference <= 5 ? LocalRelevanceCategory.PossibleNearHorizon
            : difference <= 10 ? LocalRelevanceCategory.Unlikely : LocalRelevanceCategory.NotExpected;
        return new(category, statedLatitude is > 0 ? "stated" : "typical", required, siteGeomagneticLatitude, difference, GeomagneticLatitude.ModelVersion);
    }
    /// <summary>Computes the rounded latitude difference from the actual binary64 inputs.</summary>
    public static double Difference(double requiredLatitudeDeg, double siteGeomagneticLatitudeDeg)
    {
        if (!double.IsFinite(requiredLatitudeDeg)) throw new ArgumentOutOfRangeException(nameof(requiredLatitudeDeg));
        if (!double.IsFinite(siteGeomagneticLatitudeDeg)) throw new ArgumentOutOfRangeException(nameof(siteGeomagneticLatitudeDeg));
        return GeomagneticLatitude.RoundToTenth(requiredLatitudeDeg - Math.Abs(siteGeomagneticLatitudeDeg));
    }
}
