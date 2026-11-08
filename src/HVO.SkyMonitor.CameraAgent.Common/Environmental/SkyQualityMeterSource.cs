using System.Globalization;
using System.Text;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class SkyQualityMeterSource(EnvironmentalSourceDescriptor descriptor, EnvironmentalDeviceOptions options,
    IEnvironmentalDevice? device = null) : EnvironmentalAdapterSource(descriptor, options, device, "SkyQualityMeter", EnvironmentalObservationKind.SkyQuality)
{
    protected override ValueTask<EnvironmentalSourceAcquisitionResult> ParseAsync(EnvironmentalDeviceSample sample,
        EnvironmentalSourceAcquisitionContext context, CancellationToken cancellationToken)
    {
        var fields = Encoding.ASCII.GetString(sample.Payload.Span).Trim().Split(',');
        if (fields.Length != 6 || fields[0] != "r") return ValueTask.FromResult(Unavailable("sqm-malformed"));
        var magnitude = Number(fields[1], 'm');
        var frequency = Number(fields[2], "Hz");
        var counts = Number(fields[3], 'c');
        var integration = Number(fields[4], 's');
        var temperature = Number(fields[5], 'C');
        if (magnitude <= 0) return ValueTask.FromResult(Unavailable("sqm-saturated"));
        if (magnitude > 30 || frequency < 0 || counts < 0 || counts != Math.Floor(counts) || frequency != Math.Floor(frequency) || integration is <= 0 or > 3600 ||
            temperature is < -100 or > 100) return ValueTask.FromResult(Unavailable("sqm-out-of-range"));
        var fact = Fact(sample, context, new(Descriptor.Kind, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond,
            magnitude, null, EnvironmentalObservationQuality.Good, Metadata: new(SensorTemperatureCelsius: temperature)),
            from: sample.SampledAtUtc.AddSeconds(-integration));
        return ValueTask.FromResult(Produced(fact));
    }
    private static double Number(string text, char suffix) => Number(text, suffix.ToString());
    private static double Number(string text, string suffix)
    {
        text = text.Trim();
        if (!text.EndsWith(suffix, StringComparison.Ordinal) ||
            !double.TryParse(text.AsSpan(0, text.Length - suffix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value)) throw new FormatException("Invalid SQM field.");
        return value;
    }
}
