using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class LocalWeatherStationSource(EnvironmentalSourceDescriptor descriptor, EnvironmentalDeviceOptions options,
    IEnvironmentalDevice? device = null) : EnvironmentalAdapterSource(descriptor, options, device, "LocalWeatherStation",
        EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RelativeHumidity,
        EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationKind.WindSpeed,
        EnvironmentalObservationKind.WindDirection, EnvironmentalObservationKind.WindGust,
        EnvironmentalObservationKind.PrecipitationRate, EnvironmentalObservationKind.RainState)
{
    protected override ValueTask<EnvironmentalSourceAcquisitionResult> ParseAsync(EnvironmentalDeviceSample sample,
        EnvironmentalSourceAcquisitionContext context, CancellationToken cancellationToken)
    {
        using var json = ParseJson(sample.Payload);
        var name = Descriptor.Kind switch
        {
            EnvironmentalObservationKind.AirTemperature => "temperature",
            EnvironmentalObservationKind.RelativeHumidity => "humidity",
            EnvironmentalObservationKind.AtmosphericPressure => "pressure",
            EnvironmentalObservationKind.WindSpeed => "windSpeed",
            EnvironmentalObservationKind.WindDirection => "windDirection",
            EnvironmentalObservationKind.WindGust => "windGust",
            EnvironmentalObservationKind.PrecipitationRate => "rainRate",
            _ => "rain"
        };
        if (!json.RootElement.TryGetProperty(name, out var field)) return ValueTask.FromResult(Unavailable("device-missing-field"));
        if (field.TryGetProperty("fault", out var fault) && fault.GetBoolean() ||
            field.TryGetProperty("counterReset", out var reset) && reset.GetBoolean())
            return ValueTask.FromResult(Unavailable("device-sensor-fault"));
        if (Descriptor.Kind == EnvironmentalObservationKind.WindDirection &&
            (!json.RootElement.TryGetProperty("windSpeed", out var wind) || wind.GetProperty("value").GetDouble() <= 0))
            return ValueTask.FromResult(Unavailable("wind-direction-calm"));
        var unit = field.GetProperty("unit").GetString();
        var quality = field.TryGetProperty("suspect", out var suspect) && suspect.GetBoolean()
            ? EnvironmentalObservationQuality.Suspect : EnvironmentalObservationQuality.Good;
        EnvironmentalObservationValue value;
        if (Descriptor.Kind == EnvironmentalObservationKind.RainState)
        {
            if (unit != "bool") return ValueTask.FromResult(Unavailable("device-invalid-unit"));
            value = new(Descriptor.Kind, EnvironmentalObservationUnit.Boolean, null, field.GetProperty("value").GetBoolean(), quality);
        }
        else
        {
            var numeric = field.GetProperty("value").GetDouble();
            var conversion = (Descriptor.Kind, unit) switch
            {
                (EnvironmentalObservationKind.AirTemperature, "C") => (1d, 0d, EnvironmentalObservationUnit.DegreesCelsius),
                (EnvironmentalObservationKind.AirTemperature, "F") => (5d / 9, -32d * 5 / 9, EnvironmentalObservationUnit.DegreesCelsius),
                (EnvironmentalObservationKind.RelativeHumidity, "%") => (1d, 0d, EnvironmentalObservationUnit.Percent),
                (EnvironmentalObservationKind.AtmosphericPressure, "Pa") => (1d, 0d, EnvironmentalObservationUnit.Pascals),
                (EnvironmentalObservationKind.AtmosphericPressure, "hPa") => (100d, 0d, EnvironmentalObservationUnit.Pascals),
                (EnvironmentalObservationKind.WindSpeed or EnvironmentalObservationKind.WindGust, "m/s") => (1d, 0d, EnvironmentalObservationUnit.MetersPerSecond),
                (EnvironmentalObservationKind.WindSpeed or EnvironmentalObservationKind.WindGust, "mph") => (0.44704, 0d, EnvironmentalObservationUnit.MetersPerSecond),
                (EnvironmentalObservationKind.WindDirection, "deg") => (1d, 0d, EnvironmentalObservationUnit.DegreesTrue),
                (EnvironmentalObservationKind.PrecipitationRate, "mm/h") => (1d, 0d, EnvironmentalObservationUnit.MillimetersPerHour),
                (EnvironmentalObservationKind.PrecipitationRate, "in/h") => (25.4, 0d, EnvironmentalObservationUnit.MillimetersPerHour),
                _ => (double.NaN, 0d, EnvironmentalObservationUnit.Fraction)
            };
            var canonical = numeric * conversion.Item1 + conversion.Item2;
            if (!double.IsFinite(canonical) || Descriptor.Kind == EnvironmentalObservationKind.AirTemperature && canonical is < -100 or > 80)
                return ValueTask.FromResult(Unavailable("device-out-of-range"));
            value = new(Descriptor.Kind, conversion.Item3, canonical, null, quality,
                field.TryGetProperty("uncertainty", out var uncertainty) ? uncertainty.GetDouble() * conversion.Item1 : null,
                numeric, unit);
        }
        if (Descriptor.Kind == EnvironmentalObservationKind.PrecipitationRate && sample.ObservedFromUtc is null)
            return ValueTask.FromResult(Unavailable("rain-interval-missing"));
        return ValueTask.FromResult(Produced(Fact(sample, context, value,
            from: Descriptor.Kind == EnvironmentalObservationKind.PrecipitationRate ? sample.ObservedFromUtc : null)));
    }
}
