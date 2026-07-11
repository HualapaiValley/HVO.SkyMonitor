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

/// <summary>Returns deterministic apparent equatorial positions for a supplied UTC instant.</summary>
public interface IPlanetEphemeris
{
    /// <summary>Gets an apparent equatorial position, or <see langword="null"/> when unavailable.</summary>
    EquatorialPoint? GetEquatorialPosition(SolarSystemBody body, DateTimeOffset utc);
}

/// <summary>Immutable segment linking two catalog object identifiers in a constellation figure.</summary>
public sealed record ConstellationSegment(string ConstellationId, string FromObjectId, string ToObjectId);

/// <summary>Provides immutable constellation topology without rendering concerns.</summary>
public interface IConstellationTopology
{
    /// <summary>Gets the ordered segments for a constellation identifier.</summary>
    IReadOnlyList<ConstellationSegment> GetSegments(string constellationId);
}

/// <summary>In-memory ephemeris for deterministic fixtures and explicitly provided positions.</summary>
public sealed class FixedPlanetEphemeris : IPlanetEphemeris
{
    private readonly Dictionary<SolarSystemBody, EquatorialPoint> _positions;

    /// <summary>Creates a fixture ephemeris from body-to-position mappings.</summary>
    public FixedPlanetEphemeris(IReadOnlyDictionary<SolarSystemBody, EquatorialPoint> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        _positions = new Dictionary<SolarSystemBody, EquatorialPoint>(positions);
    }

    /// <inheritdoc />
    public EquatorialPoint? GetEquatorialPosition(SolarSystemBody body, DateTimeOffset utc)
        => _positions.TryGetValue(body, out var position) ? position : null;
}

/// <summary>In-memory constellation topology with deterministic insertion order.</summary>
public sealed class InMemoryConstellationTopology : IConstellationTopology
{
    private readonly Dictionary<string, IReadOnlyList<ConstellationSegment>> _segments;

    /// <summary>Creates topology from immutable segments.</summary>
    public InMemoryConstellationTopology(IEnumerable<ConstellationSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments.GroupBy(segment => segment.ConstellationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ConstellationSegment>)group.ToArray(), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyList<ConstellationSegment> GetSegments(string constellationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constellationId);
        return _segments.TryGetValue(constellationId, out var segments) ? segments : Array.Empty<ConstellationSegment>();
    }
}
