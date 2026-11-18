using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

/// <summary>Shared current/capture eligibility for configured adapters; retained history is never changed.</summary>
public static class EnvironmentalReadingSelection
{
    public static string? SourceGroup(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var parameters = fact.Source.Provenance.Parameters;
        var identity = parameters.ValueKind == System.Text.Json.JsonValueKind.Object &&
            parameters.TryGetProperty("stationId", out var station) && station.ValueKind == System.Text.Json.JsonValueKind.String ? station.GetString() :
            parameters.ValueKind == System.Text.Json.JsonValueKind.Object &&
            parameters.TryGetProperty("deviceGroup", out var device) && device.ValueKind == System.Text.Json.JsonValueKind.String ? device.GetString() : fact.Source.SourceId;
        return fact.Source.Provider + "/" + identity;
    }

    private static string Owner(EnvironmentalObservationFactV1 fact)
        => fact.Source.Provenance.Parameters.ValueKind == System.Text.Json.JsonValueKind.Object &&
            fact.Source.Provenance.Parameters.TryGetProperty("acquisitionSourceId", out var owner) && owner.ValueKind == System.Text.Json.JsonValueKind.String
            ? owner.GetString()! : fact.Source.SourceId;

    /// <summary>Local measured/derived evidence and simulations each describe one environmental origin.</summary>
    public static string? MixingGroup(EnvironmentalObservationSourceKind kind, string? identity)
        => identity is null ? null : kind switch
        {
            EnvironmentalObservationSourceKind.Measured or EnvironmentalObservationSourceKind.Derived => "local",
            EnvironmentalObservationSourceKind.Simulated => "simulated",
            _ => identity
        };

    public static IReadOnlyList<string> UnavailableSources(EnvironmentalAcquisitionOptions options,
        IReadOnlyList<EnvironmentalSourceRuntimeState> states, DateTimeOffset through)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(states);
        return states.Where(state => state.LastCompletedUtc <= through &&
            state.LastDisposition is EnvironmentalAcquisitionDisposition.Failed or EnvironmentalAcquisitionDisposition.Missing or EnvironmentalAcquisitionDisposition.TimedOut)
            .Where(state => options.Sources.Any(source => source.Id == state.SourceId && source.Type is "LocalWeatherStation" or "InfraredSky" or "SkyQualityMeter" or "OnlineWeather"))
            .Select(state => state.SourceId).ToArray();
    }

    public static IReadOnlyList<LocalEnvironmentalObservationRecord> Select(
        IReadOnlyList<LocalEnvironmentalObservationRecord> candidates, EnvironmentalAcquisitionOptions options,
        IReadOnlyList<EnvironmentalSourceRuntimeState> states, EnvironmentalObservationKind kind,
        DateTimeOffset from, DateTimeOffset through, IReadOnlyList<string>? captureUnavailableSources = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(states);
        var unavailable = UnavailableSources(options, states, through)
            .Concat(captureUnavailableSources ?? []).ToHashSet(StringComparer.Ordinal);
        var available = candidates.Where(candidate => !candidate.Fact.Source.IsLineageInput &&
            !unavailable.Contains(Owner(candidate.Fact)) &&
            // Older v3 IR inputs did not carry a readingRole. Their explicit acquisition owner still identifies them.
            (Owner(candidate.Fact) == candidate.Fact.Source.SourceId ||
                options.Sources.Any(source => source.Id == Owner(candidate.Fact) && source.Kind == candidate.Fact.Value.Kind))).ToArray();
        if (options.WeatherSourcePriority.Count == 0 || !options.Sources.Any(source => source.Type == "OnlineWeather" && source.Kind == kind))
            return available;
        foreach (var id in options.WeatherSourcePriority)
        {
            var eligible = available.Where(candidate => candidate.Fact.Source.SourceId == id &&
                candidate.Fact.Source.Kind is EnvironmentalObservationSourceKind.Measured or EnvironmentalObservationSourceKind.Imported &&
                candidate.Fact.Value.Quality is EnvironmentalObservationQuality.Good or EnvironmentalObservationQuality.Unknown &&
                candidate.Fact.ValidFromUtc <= from && candidate.Fact.ValidThroughUtc >= through &&
                (from == through ? candidate.Fact.StaleAfterUtc > through : candidate.Fact.StaleAfterUtc >= through)).ToArray();
            if (eligible.Length > 0) return eligible;
        }
        return [];
    }
}
