using System.Globalization;
using System.Text;
using System.Xml;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

internal sealed class ScientificExportMetadata(
    ReconstructionDescriptor descriptor, ScientificExportObservation observation,
    ScientificExportEncoding encoding, ScientificExportLineage lineage)
{
    internal FrameLayoutDescriptor Layout => descriptor.Layout;
    internal string? BayerPattern => Layout.CfaPattern == ColorFilterArrayPattern.None
        ? null : BayerRggb16Demosaicer.ResolvePattern(Layout).ToString().ToUpperInvariant();

    internal IEnumerable<ScientificKeyword> Keywords()
    {
        yield return Text("DATE-OBS", descriptor.Timing.ExposureStartedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            "UTC exposure start", "Observation:Time:Start", "TimePoint");
        yield return Number("EXPTIME", descriptor.Controls.EffectiveExposure.TotalSeconds, "Exposure in seconds", "Instrument:ExposureTime");
        yield return Number("GAIN", descriptor.Controls.EffectiveGain, "Effective camera gain", "Instrument:Camera:Gain");
        yield return Text("INSTRUME", descriptor.Artifact.SourceId, "Capture camera", "Instrument:Camera:Name");
        if (descriptor.Controls.EffectiveTemperatureC is { } temperature)
            yield return Number("CCD-TEMP", temperature, "Sensor temperature in Celsius", "Instrument:Sensor:Temperature");
        if (observation.LatitudeDegrees is { } latitude)
            yield return Number("SITELAT", latitude, "Latitude in degrees", "Observation:Location:Latitude");
        if (observation.LongitudeDegrees is { } longitude)
            yield return Number("SITELONG", longitude, "East longitude in degrees", "Observation:Location:Longitude");
        if (observation.ElevationMeters is { } elevation)
            yield return Number("SITEELEV", elevation, "Elevation in metres", "Observation:Location:Elevation");
        if (!string.IsNullOrWhiteSpace(observation.Observatory))
            yield return Text("OBSERVAT", observation.Observatory, "Observatory", "Observation:Location:Name");
        if (!string.IsNullOrWhiteSpace(observation.Observer))
            yield return Text("OBSERVER", observation.Observer, "Observer", "Observer:Name");
        yield return Text("ROWORDER", "TOP-DOWN", "Rows follow the stored sensor order");
        if (BayerPattern is { } pattern)
        {
            yield return Text("BAYERPAT", pattern, "CFA pattern at first stored pixel");
            yield return Integer("XBAYROFF", 0, "Pattern already includes X phase");
            yield return Integer("YBAYROFF", 0, "Pattern already includes Y phase");
        }
        yield return Text("HVOENC", encoding.Encoder, "Encoder identity", "HVO:Encoder:Name");
        yield return Text("HVOENCV", encoding.Version, "Encoder version", "HVO:Encoder:Version");
        yield return Text("HVOOPT", encoding.Options, "Encoder options", "HVO:Encoder:Options");
        yield return Text("HVOSRCID", lineage.ArtifactId.ToString("D"), "Source artifact ID", "HVO:Source:ArtifactId");
        yield return Text("HVOCAPID", lineage.CaptureId.ToString("D"), "Source capture ID", "HVO:Source:CaptureId");
        yield return Integer("HVOSEQ", lineage.CaptureSequence, "Source capture sequence", "HVO:Source:CaptureSequence");
        yield return Text("HVOHASH", lineage.Sha256, "Source artifact SHA-256", "HVO:Source:SHA256");
        if (observation.SolutionSha256 is { } solution)
            yield return Text("HVOSOLVE", solution, "Retained solution identity", "HVO:Source:AstrometrySHA256");
        if (observation.Wcs is not { } wcs || !wcs.IsValid()) yield break;

        yield return Text("RADESYS", "FK5", "J2000 mean equator and equinox");
        yield return Number("EQUINOX", 2000);
        yield return Text("CTYPE1", $"RA---{wcs.Projection}");
        yield return Text("CTYPE2", $"DEC--{wcs.Projection}");
        yield return Text("CUNIT1", "deg");
        yield return Text("CUNIT2", "deg");
        yield return Number("CRPIX1", wcs.ReferenceX);
        yield return Number("CRPIX2", wcs.ReferenceY);
        yield return Number("CRVAL1", wcs.RightAscensionDegrees);
        yield return Number("CRVAL2", wcs.DeclinationDegrees);
        yield return Number("CD1_1", wcs.Cd11);
        yield return Number("CD1_2", wcs.Cd12);
        yield return Number("CD2_1", wcs.Cd21);
        yield return Number("CD2_2", wcs.Cd22);
        yield return Number("LONPOLE", 180);
        if (wcs.ZenithalCubic is { } cubic)
        {
            yield return Number("PV2_0", 0);
            yield return Number("PV2_1", 1);
            yield return Number("PV2_3", cubic);
        }
    }

    private static ScientificKeyword Text(string name, string value, string comment = "", string? property = null,
        string type = "String") => new(name, XmlText(value), comment, property, type);

    private static ScientificKeyword Number(string name, double value, string comment = "", string? property = null)
        => new(name, value.ToString("G17", CultureInfo.InvariantCulture), comment, property, "Float64");

    private static ScientificKeyword Integer(string name, long value, string comment, string? property = null)
        => new(name, value.ToString(CultureInfo.InvariantCulture), comment, property, "Int64");

    private static string XmlText(string value)
    {
        var clean = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (clean.Length >= 256) break;
            clean.Append(rune.IsBmp && !XmlConvert.IsXmlChar((char)rune.Value) ? " " : rune.ToString());
        }
        return clean.ToString();
    }
}

internal sealed record ScientificKeyword(string Name, string Value, string Comment, string? Property, string Type)
{
    internal bool IsFitsString => Type is "String" or "TimePoint";
}
