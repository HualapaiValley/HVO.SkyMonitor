using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class InfraredSkySource : EnvironmentalAdapterSource
{
    private readonly IEnvironmentalObservationPublisher? publisher;
    public InfraredSkySource(EnvironmentalSourceDescriptor descriptor, EnvironmentalDeviceOptions options,
        IEnvironmentalDevice? device = null, IEnvironmentalObservationPublisher? publisher = null)
        : base(descriptor, options, device, "InfraredSky", EnvironmentalObservationKind.SkyTemperature,
            EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.CloudCover,
            EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.SkyBrightness)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.ClearDeltaCelsius) || !double.IsFinite(options.CloudyDeltaCelsius) ||
            options.ClearDeltaCelsius >= options.CloudyDeltaCelsius || options.ClearDeltaCelsius < -150 ||
            options.CloudyDeltaCelsius > 50 || options.ModelVersion != "sky-ambient-linear-v1")
            throw new ArgumentException("Infrared cloud model configuration is invalid.");
        this.publisher = publisher;
    }

    protected override async ValueTask<EnvironmentalSourceAcquisitionResult> ParseAsync(EnvironmentalDeviceSample sample,
        EnvironmentalSourceAcquisitionContext context, CancellationToken cancellationToken)
    {
        using var json = ParseJson(sample.Payload);
        var root = json.RootElement;
        if (root.TryGetProperty("fault", out var fault) && fault.GetBoolean()) return Unavailable("device-sensor-fault");
        if (Descriptor.Kind == EnvironmentalObservationKind.RainState)
        {
            if (root.TryGetProperty("drying", out var drying) && drying.GetBoolean()) return Unavailable("wet-sensor-drying");
            return Produced(Fact(sample, context, new(Descriptor.Kind, EnvironmentalObservationUnit.Boolean,
                null, root.GetProperty("rain").GetBoolean(), EnvironmentalObservationQuality.Good)));
        }
        if (Descriptor.Kind == EnvironmentalObservationKind.SkyBrightness)
            return Produced(Fact(sample, context, new(Descriptor.Kind, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond,
                root.GetProperty("brightness").GetDouble(), null, EnvironmentalObservationQuality.Good)));
        var sky = root.GetProperty("skyCelsius").GetDouble();
        var ambient = root.GetProperty("ambientCelsius").GetDouble();
        if (!double.IsFinite(sky) || !double.IsFinite(ambient) || sky is < -150 or > 80 || ambient is < -100 or > 80)
            return Unavailable("device-out-of-range");
        if (Descriptor.Kind != EnvironmentalObservationKind.CloudCover)
            return Produced(Fact(sample, context, new(Descriptor.Kind, EnvironmentalObservationUnit.DegreesCelsius,
                Descriptor.Kind == EnvironmentalObservationKind.SkyTemperature ? sky : ambient, null, EnvironmentalObservationQuality.Good)));
        if (publisher is null) return Unavailable("lineage-publisher-unavailable");
        var inputs = new[]
        {
            Fact(sample, context, new(EnvironmentalObservationKind.SkyTemperature, EnvironmentalObservationUnit.DegreesCelsius, sky, null, EnvironmentalObservationQuality.Good), "-sky"),
            Fact(sample, context, new(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, ambient, null, EnvironmentalObservationQuality.Good), "-ambient")
        };
        // Commit dependencies first. Retries reproduce their identities and are handled by the existing durable publisher.
        foreach (var input in inputs) await publisher.PublishAsync(input, cancellationToken).ConfigureAwait(false);
        var cover = Math.Clamp((sky - ambient - DeviceOptions.ClearDeltaCelsius) /
            (DeviceOptions.CloudyDeltaCelsius - DeviceOptions.ClearDeltaCelsius), 0, 1);
        return Produced(Fact(sample, context, new(Descriptor.Kind, EnvironmentalObservationUnit.Fraction, cover, null,
            EnvironmentalObservationQuality.Good), sourceKind: EnvironmentalObservationSourceKind.Derived,
            lineage: inputs.Select(f => new EnvironmentalObservationReference(EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(f), f.ObservationId)).ToArray()));
    }
}
