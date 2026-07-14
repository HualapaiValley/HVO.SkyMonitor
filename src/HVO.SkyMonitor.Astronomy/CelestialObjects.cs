using System.Security.Cryptography;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Identifies a solar-system body supported by an ephemeris provider.</summary>
public enum SolarSystemBody
{
    Sun,
    Moon,
    Mercury,
    Venus,
    Mars,
    Jupiter,
    Saturn,
    Uranus,
    Neptune
}

/// <summary>A geocentric solar-system body position in the mean J2000 equatorial frame.</summary>
public sealed record SolarSystemPosition(EquatorialPoint EquatorialJ2000, double VisualMagnitude);

/// <summary>Returns deterministic geocentric J2000 positions for a supplied UTC instant.</summary>
public interface IPlanetEphemeris
{
    /// <summary>Gets the explicit algorithm or fixture version.</summary>
    string ModelVersion { get; }

    /// <summary>Gets a light-time-corrected equatorial position and visual magnitude.</summary>
    SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc);
}

/// <summary>Immutable segment linking two stable Hipparcos identifiers in a constellation figure.</summary>
public sealed record ConstellationSegment(string ConstellationId, string FromHipparcosId, string ToHipparcosId);

/// <summary>Immutable source and preprocessing identity for constellation artwork.</summary>
public sealed record ConstellationTopologyMetadata(
    string Name,
    string Version,
    Uri SourceUrl,
    string SourceSha256,
    string License,
    string PreprocessingVersion);

/// <summary>Provides immutable constellation topology without rendering concerns.</summary>
public interface IConstellationTopology
{
    /// <summary>Gets immutable topology source and preprocessing provenance.</summary>
    ConstellationTopologyMetadata Metadata { get; }

    /// <summary>Gets the ordered segments for a constellation identifier.</summary>
    IReadOnlyList<ConstellationSegment> GetSegments(string constellationId);
}

/// <summary>In-memory ephemeris for deterministic fixtures and explicitly provided positions.</summary>
public sealed class FixedPlanetEphemeris : IPlanetEphemeris
{
    private readonly Dictionary<SolarSystemBody, SolarSystemPosition> _positions;

    /// <summary>Creates a fixture ephemeris from body-to-position mappings.</summary>
    public FixedPlanetEphemeris(
        IReadOnlyDictionary<SolarSystemBody, SolarSystemPosition> positions,
        string modelVersion = "fixed-fixture-v1")
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        foreach (var (body, position) in positions)
        {
            if (!Enum.IsDefined(body) || !double.IsFinite(position.EquatorialJ2000.RightAscensionHours) ||
                position.EquatorialJ2000.RightAscensionHours is < 0 or >= 24 ||
                !double.IsFinite(position.EquatorialJ2000.DeclinationDegrees) ||
                position.EquatorialJ2000.DeclinationDegrees is < -90 or > 90 ||
                !double.IsFinite(position.VisualMagnitude))
            {
                throw new ArgumentOutOfRangeException(nameof(positions));
            }
        }

        _positions = new Dictionary<SolarSystemBody, SolarSystemPosition>(positions);
        ModelVersion = modelVersion;
    }

    /// <inheritdoc />
    public string ModelVersion { get; }

    /// <inheritdoc />
    public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
    {
        if (!Enum.IsDefined(body))
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        return _positions.TryGetValue(body, out var position)
            ? position
            : throw new KeyNotFoundException($"No fixed position is configured for {body}.");
    }
}

/// <summary>Offline VSOP87/NOVAS-derived ephemeris powered by Astronomy Engine.</summary>
public sealed class AstronomyEnginePlanetEphemeris : IPlanetEphemeris
{
    /// <inheritdoc />
    public string ModelVersion => "astronomy-engine-2.1.19-eqj-v1";

    /// <inheritdoc />
    public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
    {
        if (!Enum.IsDefined(body))
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        var time = new CosineKitty.AstroTime(utc.ToUniversalTime().UtcDateTime);
        var engineBody = ToEngineBody(body);
        var vector = CosineKitty.Astronomy.GeoVector(engineBody, time, CosineKitty.Aberration.Corrected);
        var equatorial = CosineKitty.Astronomy.EquatorFromVector(vector);
        var magnitude = body switch
        {
            SolarSystemBody.Sun => -26.74,
            SolarSystemBody.Moon => CosineKitty.Astronomy.Illumination(engineBody, time).mag,
            _ => CosineKitty.Astronomy.Illumination(engineBody, time).mag
        };
        return new SolarSystemPosition(new EquatorialPoint(equatorial.ra, equatorial.dec), magnitude);
    }

    private static CosineKitty.Body ToEngineBody(SolarSystemBody body) => body switch
    {
        SolarSystemBody.Sun => CosineKitty.Body.Sun,
        SolarSystemBody.Moon => CosineKitty.Body.Moon,
        SolarSystemBody.Mercury => CosineKitty.Body.Mercury,
        SolarSystemBody.Venus => CosineKitty.Body.Venus,
        SolarSystemBody.Mars => CosineKitty.Body.Mars,
        SolarSystemBody.Jupiter => CosineKitty.Body.Jupiter,
        SolarSystemBody.Saturn => CosineKitty.Body.Saturn,
        SolarSystemBody.Uranus => CosineKitty.Body.Uranus,
        SolarSystemBody.Neptune => CosineKitty.Body.Neptune,
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };
}

/// <summary>In-memory constellation topology with deterministic insertion order.</summary>
public sealed class InMemoryConstellationTopology : IConstellationTopology
{
    private readonly Dictionary<string, IReadOnlyList<ConstellationSegment>> _segments;

    /// <summary>Creates topology from immutable segments.</summary>
    public InMemoryConstellationTopology(
        IEnumerable<ConstellationSegment> segments,
        ConstellationTopologyMetadata? metadata = null,
        string? artifactSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var values = segments.ToArray();
        if (values.Any(segment => string.IsNullOrWhiteSpace(segment.ConstellationId) ||
            !IsValidHipparcosId(segment.FromHipparcosId) || !IsValidHipparcosId(segment.ToHipparcosId)))
        {
            throw new ArgumentException(
                "Constellation segments require nonblank identifiers and positive HIP endpoints.", nameof(segments));
        }

        _segments = values.GroupBy(segment => segment.ConstellationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ConstellationSegment>)group.ToArray(), StringComparer.Ordinal);
        AllSegments = Array.AsReadOnly(values);
        Metadata = metadata ?? new ConstellationTopologyMetadata(
            "in-memory", "fixture", new Uri("https://example.invalid/constellations"),
            "unspecified", "unspecified", "fixture-v1");
        ArtifactSha256 = artifactSha256;
    }

    /// <inheritdoc />
    public ConstellationTopologyMetadata Metadata { get; }

    /// <summary>Gets all validated segments in deterministic source order.</summary>
    public IReadOnlyList<ConstellationSegment> AllSegments { get; }

    /// <summary>Gets the SHA-256 of the serialized topology artifact when one exists.</summary>
    public string? ArtifactSha256 { get; }

    /// <inheritdoc />
    public IReadOnlyList<ConstellationSegment> GetSegments(string constellationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constellationId);
        return _segments.TryGetValue(constellationId, out var segments) ? segments : Array.Empty<ConstellationSegment>();
    }

    private static bool IsValidHipparcosId(string value)
        => int.TryParse(value, out var parsed) && parsed > 0;
}

/// <summary>Creates the standard D3-Celestial constellation artwork.</summary>
public static class StandardConstellationTopology
{
    private const string ExpectedArtifactSha256 = "70C253A00E0909AE0236DEC0411AFE837EBF8E493B2BE7F84373B63C95C91621";
    private const string ResourceName =
        "HVO.SkyMonitor.Astronomy.Data.d3-celestial-v0.7.32-topology.tsv";

    /// <summary>Loads all 88 figures and 743 HIP-linked segments from the embedded resource.</summary>
    public static IConstellationTopology CreateD3Celestial()
    {
        using var source = typeof(StandardConstellationTopology).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded topology resource '{ResourceName}' was not found.");
        using var content = new MemoryStream();
        source.CopyTo(content);
        var bytes = content.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(checksum, ExpectedArtifactSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Embedded constellation topology SHA-256 does not match its pinned identity.");
        }
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false));
        var segments = new List<ConstellationSegment>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 3)
            {
                throw new InvalidDataException("Embedded constellation topology contains a malformed row.");
            }
            segments.Add(new ConstellationSegment(fields[0], fields[1], fields[2]));
        }

        if (segments.Count != 743 || segments.Select(segment => segment.ConstellationId).Distinct().Count() != 88)
        {
            throw new InvalidDataException("Embedded constellation topology has unexpected counts.");
        }

        return new InMemoryConstellationTopology(segments, new ConstellationTopologyMetadata(
            "D3-Celestial constellation lines",
            "v0.7.32",
            new Uri("https://github.com/ofrohn/d3-celestial/tree/v0.7.32/data"),
            "294f66bef5d5cf50b1e17f16d2efa1d97a15131612c68dd935adef6e7373e13c",
            "BSD-3-Clause",
            "hip-coordinate-map-v1"), checksum);
    }
}
