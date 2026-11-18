using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class OnlineWeatherSource : IEnvironmentalSource
{
    private readonly OnlineWeatherSourceOptions options;
    private readonly WeatherProviderClient client;
    public OnlineWeatherSource(EnvironmentalSourceDescriptor descriptor, OnlineWeatherSourceOptions options, WeatherProviderClient client)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        if (descriptor.Type != "OnlineWeather" || !Enum.IsDefined(options.Provider) ||
            options.StationDistanceKilometers is { } distance && (!double.IsFinite(distance) || distance is < 0 or > 20040) ||
            options.StationElevationMeters is { } elevation && (!double.IsFinite(elevation) || elevation is < -500 or > 10000) ||
            options.StationId is not { Length: >= 1 and <= 32 } || options.StationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            descriptor.Triggers.Count != 1 || descriptor.Triggers[0] != EnvironmentalAcquisitionTrigger.Periodic ||
            descriptor.PeriodSeconds < (options.Provider == WeatherProvider.Nws ? 300 : 60) ||
            descriptor.StaleAfterSeconds != (options.Provider == WeatherProvider.Nws ? 5400 : 300) ||
            descriptor.ValidForSeconds != (options.Provider == WeatherProvider.Nws ? 7200 : 900) ||
            options.Provider == WeatherProvider.Hvo && (!options.HvoStationConfirmed || options.HvoEndpoint is not { IsAbsoluteUri: true } uri ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new ArgumentException("Online weather source configuration is invalid.");
        Descriptor = descriptor;
        this.options = options;
        this.client = client;
    }
    public EnvironmentalSourceDescriptor Descriptor { get; }

    public async ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(EnvironmentalSourceAcquisitionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sample = await client.ReadAsync(options, cancellationToken).ConfigureAwait(false);
        if (sample.Body.IsEmpty) return EnvironmentalAdapterSource.Unavailable(sample.Reason);
        if (sample.SampledAtUtc.AddSeconds(Descriptor.StaleAfterSeconds) <= context.ObservedAtUtc)
            return EnvironmentalAdapterSource.Unavailable("provider-stale");
        try
        {
            using var json = EnvironmentalPayloadJson.Parse(sample.Body);
            var reading = options.Provider == WeatherProvider.Nws ? Nws(json.RootElement) : Hvo(json.RootElement);
            if (reading is null) return EnvironmentalAdapterSource.Unavailable("provider-missing-field");
            var parameters = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                provider = options.Provider.ToString(),
                stationId = options.StationId,
                stationDistanceKilometers = options.StationDistanceKilometers,
                stationElevationMeters = options.StationElevationMeters,
                protocol = "current-weather-v1",
                attribution = options.Provider == WeatherProvider.Nws ? "NOAA/NWS" : "Hualapai Valley Observatory"
            }));
            var parametersIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
            var sourceVersion = CaptureContractJson.ComputeCanonicalJsonSha256(new
            { Algorithm = "current-weather-v1", ParametersSha256 = parametersIdentity });
            var source = new EnvironmentalObservationSource(options.Provider.ToString(), Descriptor.Id, sourceVersion,
                EnvironmentalObservationSourceKind.Imported, new(new("current-weather", "adapter-v1"), parameters,
                    parametersIdentity));
            var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new { source, sample.SampledAtUtc, reading });
            var fact = new EnvironmentalObservationFactV1(EnvironmentalObservationSchemaVersions.V3,
                new Guid(Convert.FromHexString(identity).AsSpan(0, 16)), source, sample.SampledAtUtc, null, null,
                sample.SampledAtUtc, sample.SampledAtUtc.AddSeconds(Descriptor.ValidForSeconds),
                sample.SampledAtUtc.AddSeconds(Descriptor.StaleAfterSeconds), reading, [], Descriptor.RigId);
            return EnvironmentalObservationFactJson.Validate(fact).IsValid
                ? new(EnvironmentalSourceAcquisitionOutcome.Produced, "produced", fact) : EnvironmentalAdapterSource.Unavailable("provider-out-of-range");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return EnvironmentalAdapterSource.Unavailable("provider-malformed"); }
    }

    private EnvironmentalObservationValue? Nws(JsonElement root)
    {
        var (field, expectedUnit, multiplier, unit) = Descriptor.Kind switch
        {
            EnvironmentalObservationKind.AirTemperature => ("temperature", "wmoUnit:degC", 1d, EnvironmentalObservationUnit.DegreesCelsius),
            EnvironmentalObservationKind.RelativeHumidity => ("relativeHumidity", "wmoUnit:percent", 1d, EnvironmentalObservationUnit.Percent),
            EnvironmentalObservationKind.AtmosphericPressure => ("barometricPressure", "wmoUnit:Pa", 1d, EnvironmentalObservationUnit.Pascals),
            EnvironmentalObservationKind.SeaLevelPressure => ("seaLevelPressure", "wmoUnit:Pa", 1d, EnvironmentalObservationUnit.Pascals),
            EnvironmentalObservationKind.WindSpeed => ("windSpeed", "wmoUnit:km_h-1", 1d / 3.6, EnvironmentalObservationUnit.MetersPerSecond),
            EnvironmentalObservationKind.WindGust => ("windGust", "wmoUnit:km_h-1", 1d / 3.6, EnvironmentalObservationUnit.MetersPerSecond),
            EnvironmentalObservationKind.WindDirection => ("windDirection", "wmoUnit:degree_(angle)", 1d, EnvironmentalObservationUnit.DegreesTrue),
            _ => ("", "", 1d, EnvironmentalObservationUnit.Fraction)
        };
        if (!root.GetProperty("properties").TryGetProperty(field, out var value) ||
            !value.TryGetProperty("value", out var numeric) || numeric.ValueKind == JsonValueKind.Null ||
            value.GetProperty("unitCode").GetString() != expectedUnit) return null;
        return new(Descriptor.Kind, unit, numeric.GetDouble() * multiplier, null, EnvironmentalObservationQuality.Unknown);
    }
    private EnvironmentalObservationValue? Hvo(JsonElement root)
    {
        var (field, multiplier, offset, unit) = Descriptor.Kind switch
        {
            EnvironmentalObservationKind.AirTemperature => ("outTemp", 5d / 9, -32d * 5 / 9, EnvironmentalObservationUnit.DegreesCelsius),
            EnvironmentalObservationKind.RelativeHumidity => ("outHumidity", 1d, 0d, EnvironmentalObservationUnit.Percent),
            EnvironmentalObservationKind.WindSpeed => ("windSpeed", 0.44704, 0d, EnvironmentalObservationUnit.MetersPerSecond),
            EnvironmentalObservationKind.WindDirection => ("windDir", 1d, 0d, EnvironmentalObservationUnit.DegreesTrue),
            EnvironmentalObservationKind.SeaLevelPressure => ("barometer", 3386.37526, 0d, EnvironmentalObservationUnit.Pascals),
            _ => ("", 1d, 0d, EnvironmentalObservationUnit.Fraction)
        };
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return new(Descriptor.Kind, unit, value.GetDouble() * multiplier + offset, null, EnvironmentalObservationQuality.Unknown);
    }
}
