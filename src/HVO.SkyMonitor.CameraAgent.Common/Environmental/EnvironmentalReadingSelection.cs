using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

/// <summary>Shared current/capture eligibility for configured adapters; retained history is never changed.</summary>
public static class EnvironmentalReadingSelection
{
    public static string? SourceGroup(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.SchemaVersion != EnvironmentalObservationSchemaVersions.V3) return null;
        var parameters = fact.Source.Provenance.Parameters;
        var identity = parameters.TryGetProperty("stationId", out var station) ? station.GetString() :
            parameters.TryGetProperty("deviceGroup", out var device) ? device.GetString() : fact.Source.SourceId;
        return fact.Source.Provider + "/" + identity;
    }

    public static IReadOnlyList<LocalEnvironmentalObservationRecord> Select(
        IReadOnlyList<LocalEnvironmentalObservationRecord> candidates, EnvironmentalAcquisitionOptions options,
        IReadOnlyList<EnvironmentalSourceRuntimeState> states, EnvironmentalObservationKind kind,
        DateTimeOffset from, DateTimeOffset through)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(states);
        var unavailable = states.Where(state => state.LastCompletedUtc <= through &&
            state.LastDisposition is EnvironmentalAcquisitionDisposition.Failed or EnvironmentalAcquisitionDisposition.Missing or EnvironmentalAcquisitionDisposition.TimedOut)
            .Where(state => options.Sources.Any(source => source.Id == state.SourceId && source.Type is "LocalWeatherStation" or "InfraredSky" or "SkyQualityMeter" or "OnlineWeather"))
            .Select(state => state.SourceId).ToArray();
        var available = candidates.Where(candidate => !unavailable.Any(id => candidate.Fact.Source.SourceId == id ||
            candidate.Fact.Source.SourceId.StartsWith(id + "-", StringComparison.Ordinal))).ToArray();
        if (options.WeatherSourcePriority.Count == 0 || !options.Sources.Any(source => source.Type == "OnlineWeather" && source.Kind == kind))
            return available;
        foreach (var id in options.WeatherSourcePriority)
        {
            var eligible = available.Where(candidate => candidate.Fact.Source.SourceId == id &&
                candidate.Fact.Source.Kind is EnvironmentalObservationSourceKind.Measured or EnvironmentalObservationSourceKind.Imported &&
                candidate.Fact.Value.Quality is EnvironmentalObservationQuality.Good or EnvironmentalObservationQuality.Unknown &&
                candidate.Fact.ValidFromUtc <= from && candidate.Fact.ValidThroughUtc >= through && candidate.Fact.StaleAfterUtc > through).ToArray();
            if (eligible.Length > 0) return eligible;
        }
        return [];
    }
}
